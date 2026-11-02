using System.Text.Json;

namespace AIStack.Doctor;

/// `shared/doctor-catalogue.json`, shared with the macOS app. The JSON can raise a tier but never lower it,
/// and can't make a lossy action ruleable.
public class DoctorCatalogue
{
    public class MatchSpec
    {
        public string Kind { get; set; } = "";
        public List<string>? Extensions { get; set; }
        public string? OlderThanDays { get; set; }
        public string? Name { get; set; }
    }
    public class Category
    {
        public string Id { get; set; } = "";
        public DoctorAction Action { get; set; }
        public int Tier { get; set; }
        public bool? Ruleable { get; set; }
        public string Label { get; set; } = "";
        public MatchSpec Match { get; set; } = new();
        public Dictionary<string, List<string>> Paths { get; set; } = new();
    }

    public int Version { get; set; }
    public Dictionary<string, double> Defaults { get; set; } = new();
    public List<Category> Categories { get; set; } = new();
    public Dictionary<string, List<string>> NeverPaths { get; set; } = new();
    public List<string> NeverNameSuffixes { get; set; } = new();
    public List<string> IncompressibleExtensions { get; set; } = new();

    public static DoctorCatalogue Load(string? path)
    {
        var candidates = new[] { path, System.IO.Path.Combine(AppContext.BaseDirectory, "doctor-catalogue.json"),
                                 System.IO.Path.Combine(Environment.CurrentDirectory, "shared", "doctor-catalogue.json") }
            .Where(p => p != null).Cast<string>().ToList();
        var found = candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("catalogue not found: " + string.Join(", ", candidates));
        var cat = JsonSerializer.Deserialize<DoctorCatalogue>(File.ReadAllText(found), DoctorFS.Json)!;
        foreach (var c in cat.Categories)
        {
            if (c.Tier < (int)c.Action.Tier()) throw new InvalidOperationException($"catalogue category {c.Id} lowers its action's tier");
            if (c.Ruleable == true && !c.Action.Ruleable()) throw new InvalidOperationException($"catalogue category {c.Id} marks a non-ruleable action ruleable");
        }
        return cat;
    }

    public double Value(string key) => Defaults.TryGetValue(key, out var v) ? v : 0;
    public DoctorTier TierOf(Category c) => (DoctorTier)Math.Max(c.Tier, (int)c.Action.Tier());

    /// %USERPROFILE%, %APPDATA% and %LOCALAPPDATA% relative to `home`, so a fixture root behaves like a real profile.
    public static string Expand(string p, string home)
    {
        string Sub(string tail) => System.IO.Path.GetFullPath(System.IO.Path.Combine(home, tail.TrimStart('\\', '/').Replace('\\', System.IO.Path.DirectorySeparatorChar)));
        foreach (var (k, v) in new[] { ("%USERPROFILE%", ""), ("%APPDATA%", "AppData/Roaming"), ("%LOCALAPPDATA%", "AppData/Local") })
            if (p.StartsWith(k, StringComparison.OrdinalIgnoreCase))
                return Sub(System.IO.Path.Combine(v, p[k.Length..].TrimStart('\\', '/')));
        if (p == "~") return home;
        if (p.StartsWith("~/")) return Sub(p[2..]);
        return p;
    }
}

/// The never-list. Runs on every path for every tier, before anything else.
/// Hard-coded rules here apply even if the catalogue JSON is edited.
public class DoctorGuard
{
    static readonly string[] HardNever = {
        "AppData/Roaming/Microsoft/Credentials", "AppData/Local/Microsoft/Credentials", "AppData/Roaming/Microsoft/Protect",
        "AppData/Local/Google/Chrome/User Data", "AppData/Local/Microsoft/Edge/User Data", "AppData/Roaming/Mozilla/Firefox",
        "AppData/Local/Microsoft/Outlook", "AppData/Local/AIStack/doctor",
    };
    static readonly string[] HardSuffixes = { ".pst", ".ost", ".kdbx" };

    readonly string home;
    readonly DoctorCatalogue catalogue;
    readonly List<string> never;
    public DateTime Now = DateTime.UtcNow;

    public DoctorGuard(string home, DoctorCatalogue catalogue)
    {
        this.home = System.IO.Path.GetFullPath(home).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        this.catalogue = catalogue;
        never = HardNever.Concat((catalogue.NeverPaths.GetValueOrDefault("windows") ?? new())
                .Select(p => Rel(DoctorCatalogue.Expand(p, this.home))).Where(r => r != null).Cast<string>())
            .Select(n => n.Replace('\\', '/').TrimEnd('/')).ToList();
    }

    string? Rel(string full)
    {
        var f = System.IO.Path.GetFullPath(full);
        var prefix = home + System.IO.Path.DirectorySeparatorChar;
        return f.StartsWith(prefix, DoctorFS.PathCmp) ? f[prefix.Length..].Replace('\\', '/') : null;
    }

    /// null = allowed; otherwise the reason it is refused.
    public string? Refusal(string path, bool forWrite = true)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path); } catch { return "invalid path"; }
        var rel = Rel(full);
        if (rel == null) return "outside the home folder";
        // A junction or symlink anywhere on the way could point outside the home folder.
        var dir = full;
        while (dir.Length > home.Length)
        {
            if (DoctorFS.IsLink(dir)) return "path goes through a link";
            dir = System.IO.Path.GetDirectoryName(dir) ?? "";
        }
        foreach (var n in never)
        {
            if (rel.Equals(n, DoctorFS.PathCmp) || rel.StartsWith(n + "/", DoctorFS.PathCmp)) return $"never-list location ({n})";
            // A folder that holds a never-list location (e.g. %LOCALAPPDATA%\Google holds Chrome's profile).
            if (n.StartsWith(rel + "/", DoctorFS.PathCmp)) return $"contains a never-list location ({n})";
        }
        foreach (var comp in rel.Split('/'))
            if (HardSuffixes.Concat(catalogue.NeverNameSuffixes).Any(s => comp.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                return $"never-list item ({comp})";
        if (GitAncestor(full) is string repo) return $"inside a git repository ({repo})";
        if (!DoctorFS.Exists(full)) return "missing";
        if (forWrite)
        {
            var days = Math.Max(catalogue.Value("neverModifiedWithinDays"), 7);
            if (DoctorFS.Unix(Now) - DoctorFS.TreeStats(full).Newest < days * 86400) return $"modified in the last {(int)days} days";
        }
        return null;
    }

    string? GitAncestor(string full)
    {
        var dir = full;
        while (dir.Length > home.Length)
        {
            if (DoctorFS.Exists(System.IO.Path.Combine(dir, ".git"))) return dir;
            dir = System.IO.Path.GetDirectoryName(dir) ?? "";
        }
        return null;
    }
}
