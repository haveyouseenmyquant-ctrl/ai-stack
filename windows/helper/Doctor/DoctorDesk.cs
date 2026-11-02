namespace AIStack.Doctor;

/// What the report's buttons do, without any UI (same as DoctorDesk on macOS), exposed as `decide`, `history` and
/// `undo-checkup` so the safety suite drives the exact path a UI will use.
/// - approve: an approval per item (by the user), then only those items run through Execute().
/// - decline: a decline per item; nothing on disk is touched.
/// - always: a rule for every category involved (refused as a whole if any isn't ruleable), then approve.
/// Decided items leave pending.json.
public static class DoctorDesk
{
    public class DecideResult
    {
        public int Decided { get; set; }
        public List<string> Rules { get; set; } = new();
        public DoctorExecutor.Outcome? Outcome { get; set; }
    }

    public static DecideResult Decide(string decision, HashSet<string>? ids, TreatmentPlan plan, string home, DoctorCatalogue catalogue,
                                      DoctorJournal journal, string dir, bool testRoot)
    {
        if (decision is not ("approve" or "decline" or "always")) throw new InvalidOperationException("--decision approve|decline|always");
        var decided = DecidedIds(plan, DoctorJournal.Read(journal.Path));
        var items = plan.Groups.SelectMany(g => g.Items).Where(f => (ids?.Contains(f.Id) ?? true) && !decided.Contains(f.Id)).ToList();
        var res = new DecideResult { Decided = items.Count };
        if (items.Count == 0) return res;
        var ex = new DoctorExecutor(home, catalogue, journal, plan.Checkup, testRoot);
        if (decision == "always")
        {
            var book = DoctorRuleBook.Load(Path.Combine(dir, "rules.json"), catalogue);
            var cats = items.Select(f => f.Category).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
            if (cats.FirstOrDefault(c => !DoctorRuleBook.Allowed(c, catalogue)) is string bad)
                throw new InvalidOperationException($"{bad} can't become a rule: only reversible Recycle Bin moves and disabling startup items can");
            foreach (var c in cats) res.Rules.Add(book.Add(c, catalogue).Id);
            Directory.CreateDirectory(dir);
            DoctorCli.Emit(book, Path.Combine(dir, "rules.json"));
        }
        if (decision == "decline") ex.Decline(items, "user");
        else
        {
            ex.Approve(items, "user");
            // Newest approval per item first: only the decision just made counts.
            var approvals = Enumerable.Reverse(DoctorJournal.Read(journal.Path)).Where(r => r.Checkup == plan.Checkup).ToList();
            var chosen = items.Select(f => f.Id).ToHashSet();
            var sub = new TreatmentPlan
            {
                Checkup = plan.Checkup, Root = plan.Root,
                Groups = plan.Groups.Select(g => new PlanGroup { Category = g.Category, Action = g.Action, Label = g.Label, TotalBytes = g.TotalBytes,
                                                                 Items = g.Items.Where(f => chosen.Contains(f.Id)).ToList() })
                                    .Where(g => g.Items.Count > 0).ToList(),
            };
            res.Outcome = ex.Execute(sub, approvals);
        }
        var pendingPath = Path.Combine(dir, "pending.json");
        var pending = DoctorPendingBook.Load(pendingPath);
        pending.Items.RemoveAll(e => items.Any(f => f.Id == e.Item.Id));
        Directory.CreateDirectory(dir);
        DoctorCli.Emit(pending, pendingPath);
        return res;
    }

    public static HashSet<string> DecidedIds(TreatmentPlan plan, List<JournalRecord> records)
    {
        var ids = plan.Groups.SelectMany(g => g.Items).Select(f => f.Id).ToHashSet();
        return records.Where(r => r.Phase is "approval" or "decline" && r.PlanItem is string p && ids.Contains(p)).Select(r => r.PlanItem!).ToHashSet();
    }

    public class HistoryEntry
    {
        public string Id { get; set; } = "";
        public string Checkup { get; set; } = "";
        public string Ts { get; set; } = "";
        public DoctorAction Action { get; set; }
        public string Path { get; set; } = "";
        public long FreedBytes { get; set; }
        public string? By { get; set; }
        public bool Undone { get; set; }
        public bool CanUndo { get; set; }
        public string? Note { get; set; }
    }

    public class HistoryCheckup
    {
        public string Checkup { get; set; } = "";
        public List<HistoryEntry> Entries { get; set; } = new();
    }

    /// Every completed action, newest checkup first. Undo works for compression, archives, re-downloading evicted
    /// cloud files, and Recycle Bin moves while the item is still there. Defender keeps its own quarantine.
    public static List<HistoryCheckup> History(List<JournalRecord> records)
    {
        var undone = records.Where(r => r.Phase == "undo-done").Select(r => r.Id).ToHashSet();
        var approvedBy = new Dictionary<string, string?>();
        foreach (var r in records.Where(r => r.Phase == "approval" && r.PlanItem != null)) approvedBy[r.PlanItem!] = r.By;
        var by = new Dictionary<string, List<HistoryEntry>>();
        foreach (var r in records.Where(r => r.Phase == "done"))
        {
            var isUndone = undone.Contains(r.Id);
            var can = !isUndone;
            string? note = null;
            switch (r.Action)
            {
                case DoctorAction.Compress: case DoctorAction.ArchiveFolder: case DoctorAction.EvictCloud: break;
                case DoctorAction.MoveToTrash:
                    if (!isUndone && !(r.To is string to && DoctorFS.Exists(to))) { can = false; note = "no longer in the Recycle Bin"; }
                    break;
                case DoctorAction.Quarantine: can = false; note = "restore it from Windows Security"; break;
                case DoctorAction.Report: case DoctorAction.EmptyTrash: case DoctorAction.DisableStartupItem: case DoctorAction.DisableBrowserExtension:
                case DoctorAction.QuitProcess: case DoctorAction.UninstallApp: case DoctorAction.MoveFile:
                    can = false; break;
                default: throw new InvalidOperationException($"no history rule for {r.Action}");
            }
            (by.TryGetValue(r.Checkup, out var l) ? l : by[r.Checkup] = new()).Add(new HistoryEntry
            {
                Id = r.Id, Checkup = r.Checkup, Ts = r.Ts, Action = r.Action, Path = r.From ?? "", FreedBytes = r.FreedBytes ?? 0,
                By = r.PlanItem is string p && approvedBy.TryGetValue(p, out var b) ? b : null, Undone = isUndone, CanUndo = can, Note = note,
            });
        }
        return by.Select(kv => new HistoryCheckup { Checkup = kv.Key, Entries = kv.Value.OrderByDescending(e => e.Ts, StringComparer.Ordinal).ToList() })
                 .OrderByDescending(h => h.Checkup, StringComparer.Ordinal).ToList();
    }

    public static DoctorExecutor.Outcome Undo(string checkup, string home, DoctorCatalogue catalogue, DoctorJournal journal, bool testRoot)
    {
        var ex = new DoctorExecutor(home, catalogue, journal, checkup, testRoot);
        var o = new DoctorExecutor.Outcome();
        var entries = History(DoctorJournal.Read(journal.Path)).FirstOrDefault(h => h.Checkup == checkup)?.Entries ?? new();
        foreach (var e in entries.Where(e => e.CanUndo))
        {
            var r = ex.Undo(DoctorJournal.Read(journal.Path), e.Id);
            o.Done += r.Done; o.Failed += r.Failed; o.Skipped += r.Skipped;
        }
        return o;
    }
}
