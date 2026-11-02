using System.Globalization;

namespace AIStack.Doctor;

/// The user's Laptop Doctor settings, as one checkup sees them (same as DoctorCheckupOptions on macOS).
public class DoctorCheckupOptions
{
    /// Tier 1 kinds the user switched on. Quarantine has no switch: Defender acts on its own and we only report it.
    public HashSet<DoctorAction> Tier1 { get; set; } = new() { DoctorAction.Compress, DoctorAction.EvictCloud, DoctorAction.ArchiveFolder, DoctorAction.Quarantine };
    public Dictionary<string, int> Thresholds { get; set; } = new();
    public List<string> ArchiveFolders { get; set; } = new();
}

/// One checkup: recover an interrupted run → scan → Tier 1 (only the switched-on kinds) → treatment plan.
/// Writes report.json, plan.json and outcome.json to `<dir>/<checkup id>/`.
public static class DoctorCheckup
{
    /// The N-day thresholds a user may change. Other defaults (compression gain, the 7-day rule) stay fixed.
    public static readonly HashSet<string> UserDayKeys = new() { "coldDays", "cloudColdDays", "archiveDays", "installerAgeDays", "inactiveProjectDays" };

    /// Carried: undecided items from earlier plans; Expired: carried items older than 30 days or whose target changed
    /// or is gone; Suppressed: already approved or declined for the same contents; Ruled: what "always" rules ran.
    public record Result(string Checkup, string Folder, DoctorExecutor.Outcome Tier1, int Tier1Disabled, int PlanItems, long PlanBytes,
                         int Carried, int Expired, int Suppressed, DoctorExecutor.Outcome? Ruled);

    public static Result Run(string home, DoctorCatalogue catalogue, DoctorCheckupOptions options, DoctorJournal journal,
                             string dir, bool testRoot, string checkup)
    {
        // Nothing modified in the last 7 days is ever touched, so a lower value would only mislead.
        foreach (var (k, v) in options.Thresholds)
            if (UserDayKeys.Contains(k)) catalogue.Defaults[k] = Math.Max(v, 7);
        var ex = new DoctorExecutor(home, catalogue, journal, checkup, testRoot);
        ex.Recover(DoctorJournal.Read(journal.Path));
        var scanner = new DoctorScanner(home, catalogue, options.ArchiveFolders, testRoot);
        var findings = scanner.Scan();
        var auto = findings.Where(f => f.Action.Tier() == DoctorTier.Automatic).ToList();
        var outcome = ex.RunTier1(auto.Where(f => options.Tier1.Contains(f.Action)).ToList());
        // Carry over undecided items, then let the user's rules approve their kinds. Ruled items still go through
        // Execute(): the never-list, the approval check and the re-check that the target is unchanged.
        var merged = DoctorPendingBook.Load(Path.Combine(dir, "pending.json")).MergeWith(findings, DoctorJournal.Read(journal.Path), checkup);
        var rules = DoctorRuleBook.Load(Path.Combine(dir, "rules.json"), catalogue);
        var ruledItems = new List<Finding>();
        var left = new List<Finding>();
        // Rules act only on what this checkup's own scan found; an entry only carried over from pending.json (which
        // anything able to write the support folder could edit) goes on the plan for the user instead.
        var confirmed = findings.Select(f => f.Id).ToHashSet();
        foreach (var f in merged.Items)
            if (confirmed.Contains(f.Id) && rules.RuleFor(f) is { } r) { ruledItems.Add(f); ex.Approve(new() { f }, "rule:" + r.Id); } else left.Add(f);
        DoctorExecutor.Outcome? ruled = null;
        if (ruledItems.Count > 0)
            ruled = ex.Execute(DoctorCli.MakePlan(ruledItems, catalogue, checkup, home),
                               DoctorJournal.Read(journal.Path).Where(x => x.Checkup == checkup).ToList());
        var ruledIds = ruledItems.Select(f => f.Id).ToHashSet();
        merged.Book.Items.RemoveAll(e => ruledIds.Contains(e.Item.Id));
        var plan = DoctorCli.MakePlan(left, catalogue, checkup, home);
        var report = new DoctorReport(1, checkup, home, false, findings, scanner.Skipped, testRoot ? new() : DoctorScanner.Posture());

        var folder = Path.Combine(dir, checkup.Replace(":", "-"));
        Directory.CreateDirectory(folder);
        var items = plan.Groups.SelectMany(g => g.Items).ToList();
        var result = new Result(checkup, folder, outcome, auto.Count(f => !options.Tier1.Contains(f.Action)), items.Count, items.Sum(i => i.Bytes),
                                merged.Carried, merged.Expired, merged.Suppressed, ruled);
        DoctorCli.Emit(report, Path.Combine(folder, "report.json"));
        DoctorCli.Emit(plan, Path.Combine(folder, "plan.json"));
        DoctorCli.Emit(result, Path.Combine(folder, "outcome.json"));
        DoctorCli.Emit(merged.Book, Path.Combine(dir, "pending.json"));
        return result;
    }
}

/// When a scheduled checkup is due (same rules as DoctorSchedule on macOS). Weekday 1 = Sunday … 7 = Saturday.
public static class DoctorSchedule
{
    /// The latest weekly slot at or before `now`, in `now`'s own clock (local time in the app, UTC in tests).
    public static DateTime LastSlot(DateTime now, int weekday, int hour)
    {
        var slot = now.Date.AddHours(hour).AddDays(-(((int)now.DayOfWeek - (weekday - 1) + 7) % 7));
        return slot > now ? slot.AddDays(-7) : slot;
    }

    /// Due once per slot; a missed slot runs at the next chance; no last run (just switched on) waits for the next slot.
    public static bool IsDue(DateTime now, DateTime? lastRun, int weekday, int hour) =>
        lastRun is DateTime l && l < LastSlot(now, weekday, hour);

    /// At most one approval request a day, however often checkups run.
    public static bool MayNotify(DateTime now, DateTime? lastNotified) =>
        lastNotified is not DateTime l || (l.Date != now.Date && l < now);

    public static DateTime? ParseUtc(string? s) =>
        s == null ? null : DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
