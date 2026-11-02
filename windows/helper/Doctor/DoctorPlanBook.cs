using System.Globalization;
using System.Text.Json;

namespace AIStack.Doctor;

/// "Always do this" rules, `<checkups dir>/rules.json` (same as DoctorRuleBook on macOS). A rule auto-approves one
/// catalogue category in later plans. Only categories the catalogue marks ruleable whose action is reversible
/// (Recycle Bin moves, disabling a startup item) can have one; checked on add and every time the file is read.
public class DoctorRule
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Created { get; set; } = "";
}

public class DoctorRuleBook
{
    public int Version { get; set; } = 1;
    public List<DoctorRule> Rules { get; set; } = new();

    public static bool Allowed(string category, DoctorCatalogue catalogue) =>
        catalogue.Categories.FirstOrDefault(c => c.Id == category) is { } c
        && c.Ruleable == true && c.Action.Ruleable() && catalogue.TierOf(c) == DoctorTier.Plan;

    public static DoctorRuleBook Load(string path, DoctorCatalogue catalogue)
    {
        DoctorRuleBook book;
        try { book = JsonSerializer.Deserialize<DoctorRuleBook>(File.ReadAllText(path), DoctorFS.Json) ?? new(); }
        catch { book = new(); }
        book.Rules = book.Rules.Where(r => Allowed(r.Category, catalogue)).ToList();   // a hand-edited file can't widen the rules
        return book;
    }

    public DoctorRule Add(string category, DoctorCatalogue catalogue)
    {
        if (!Allowed(category, catalogue))
            throw new InvalidOperationException($"{category} can't become a rule: only reversible Recycle Bin moves and disabling startup items can");
        if (Rules.FirstOrDefault(r => r.Category == category) is { } existing) return existing;
        var r = new DoctorRule { Id = "r-" + DoctorJournal.NewId()[..8], Category = category, Created = DoctorFS.Iso() };
        Rules.Add(r);
        return r;
    }

    /// Removing a rule only stops future auto-approvals; what it already did stays in History with its own Undo.
    public bool Remove(string id) => Rules.RemoveAll(r => r.Id == id) > 0;

    public DoctorRule? RuleFor(Finding f) => Rules.FirstOrDefault(r => r.Category == f.Category && f.Action.Ruleable());
}

/// Undecided plan items, `<checkups dir>/pending.json`: an item the user hasn't approved or declined stays on the
/// next plan (one entry per item) until it ages out after 30 days.
public class DoctorPendingBook
{
    public class Entry
    {
        public string FirstSeen { get; set; } = "";
        public Finding Item { get; set; } = new();
    }
    public int Version { get; set; } = 1;
    public List<Entry> Items { get; set; } = new();

    public const double MaxAgeDays = 30;

    public static DoctorPendingBook Load(string path)
    {
        try { return JsonSerializer.Deserialize<DoctorPendingBook>(File.ReadAllText(path), DoctorFS.Json) ?? new(); }
        catch { return new(); }
    }

    public record Merge(List<Finding> Items, DoctorPendingBook Book, int Carried, int Expired, int Suppressed);

    /// Fresh findings plus still-valid carried items, minus items already decided for the same contents.
    /// A carried item the fresh scan no longer finds is kept only while its target is unchanged and it is younger
    /// than 30 days. A declined item stays off later plans until its target changes.
    public Merge MergeWith(List<Finding> fresh, List<JournalRecord> records, string now)
    {
        var decided = records.Where(r => r.Phase is "approval" or "decline" && r.PlanItem != null)
            .GroupBy(r => r.PlanItem!).ToDictionary(g => g.Key, g => g.Select(r => r.Fingerprint).ToList());
        bool IsDecided(Finding f) => decided.TryGetValue(f.Id, out var fps) && fps.Any(fp => fp != null && Same(fp, f.Fingerprint, true));
        var nowDate = Parse(now) ?? DateTime.UtcNow;
        var old = Items.GroupBy(e => e.Item.Id).ToDictionary(g => g.Key, g => g.First());
        var items = new List<Finding>();
        var book = new DoctorPendingBook();
        int carried = 0, expired = 0, suppressed = 0;
        var seen = new HashSet<string>();
        foreach (var f in fresh.Where(f => f.Tier == (int)DoctorTier.Plan))
        {
            if (!seen.Add(f.Id)) continue;
            if (IsDecided(f)) { suppressed++; continue; }
            items.Add(f);
            book.Items.Add(new Entry { FirstSeen = old.TryGetValue(f.Id, out var e) ? e.FirstSeen : now, Item = f });
        }
        foreach (var e in Items)
        {
            if (!seen.Add(e.Item.Id)) continue;
            var age = (nowDate - (Parse(e.FirstSeen) ?? nowDate)).TotalDays;
            if (age > MaxAgeDays) { expired++; continue; }
            if (IsDecided(e.Item)) { suppressed++; continue; }
            if (DoctorFS.FingerprintOf(e.Item.Path) is not { } cur || !Same(cur, e.Item.Fingerprint, false)) { expired++; continue; }
            items.Add(e.Item);
            book.Items.Add(e);
            carried++;
        }
        return new Merge(items, book, carried, expired, suppressed);
    }

    static DateTime? Parse(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    static bool Same(Fingerprint a, Fingerprint? b, bool hash) =>
        b != null && a.Size == b.Size && Math.Abs(a.Mtime - b.Mtime) < 0.001 && (!hash || a.Sha256 == b.Sha256);
}
