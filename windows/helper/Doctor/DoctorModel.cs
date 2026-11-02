using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIStack.Doctor;

/// Laptop Doctor tiers. The tier is a property of the action type, never decided at run time
/// (same table as Sources/AIStack/Doctor/DoctorModel.swift).
public enum DoctorTier { Observe = 0, Automatic = 1, Plan = 2 }

public enum DoctorAction
{
    Report,
    Compress, EvictCloud, ArchiveFolder, Quarantine,
    MoveToTrash, EmptyTrash, DisableStartupItem, DisableBrowserExtension, QuitProcess, UninstallApp, MoveFile,
}

public static class Actions
{
    // Switch expressions without a discard arm: a new action without a tier is a compiler warning (CS8509), and
    // an unknown value at run time throws instead of silently getting a tier.
    public static DoctorTier Tier(this DoctorAction a) => a switch
    {
        DoctorAction.Report => DoctorTier.Observe,
        DoctorAction.Compress or DoctorAction.EvictCloud or DoctorAction.ArchiveFolder or DoctorAction.Quarantine => DoctorTier.Automatic,
        DoctorAction.MoveToTrash or DoctorAction.EmptyTrash or DoctorAction.DisableStartupItem or DoctorAction.DisableBrowserExtension
            or DoctorAction.QuitProcess or DoctorAction.UninstallApp or DoctorAction.MoveFile => DoctorTier.Plan,
    };

    /// Only reversible Tier 2 kinds may be auto-approved by an "always do this" rule.
    public static bool Ruleable(this DoctorAction a) => a switch
    {
        DoctorAction.MoveToTrash or DoctorAction.DisableStartupItem or DoctorAction.DisableBrowserExtension => true,
        DoctorAction.Report or DoctorAction.Compress or DoctorAction.EvictCloud or DoctorAction.ArchiveFolder or DoctorAction.Quarantine
            or DoctorAction.EmptyTrash or DoctorAction.QuitProcess or DoctorAction.UninstallApp or DoctorAction.MoveFile => false,
    };
}

public record Fingerprint(long Size, double Mtime, string? Sha256);

public class Finding
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public DoctorAction Action { get; set; }
    public int Tier { get; set; }
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public string Reason { get; set; } = "";
    public string? Flag { get; set; }
    public string? ProcessName { get; set; }
    public string? Executable { get; set; }
    public int? Pid { get; set; }
    public Fingerprint? Fingerprint { get; set; }

    public static string ItemId(string category, string path) =>
        "p-" + DoctorFS.Sha256(System.Text.Encoding.UTF8.GetBytes(category + "\0" + path))[..16];
}

public record DoctorReport(int Version, string GeneratedAt, string Root, bool DryRun, List<Finding> Findings,
                           List<string> Skipped, Dictionary<string, string> Posture);

public class PlanGroup
{
    public string Category { get; set; } = "";
    public DoctorAction Action { get; set; }
    public string Label { get; set; } = "";
    public long TotalBytes { get; set; }
    public List<Finding> Items { get; set; } = new();
}

public class TreatmentPlan
{
    public int Version { get; set; } = 1;
    public string Checkup { get; set; } = "";
    public string Root { get; set; } = "";
    public List<PlanGroup> Groups { get; set; } = new();
}

public static class DoctorFS
{
    /// A fixture root must not be the real profile or a folder above it: `--root %USERPROFILE%` would run Tier 1 and
    /// Tier 2 on real data under test-mode rules. Compared on full paths (junction targets too), ignoring case.
    public static bool CoversRealHome(string root)
    {
        static string Norm(string p)
        {
            var f = Path.GetFullPath(p);
            try { if (new DirectoryInfo(f).ResolveLinkTarget(true) is { } t) f = t.FullName; } catch (IOException) { }
            f = f.ToLowerInvariant().Replace('\\', '/');
            return f.EndsWith('/') ? f : f + "/";
        }
        var homes = new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            Environment.GetEnvironmentVariable("USERPROFILE"), Environment.GetEnvironmentVariable("HOME") };
        var r = Norm(root);
        return homes.Where(h => !string.IsNullOrEmpty(h)).Any(h => Norm(h!).StartsWith(r, StringComparison.Ordinal));
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };
    public static readonly JsonSerializerOptions JsonLine = new(Json) { WriteIndented = false };

    public static readonly StringComparison PathCmp =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string? Sha256File(string path)
    {
        try { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
        catch { return null; }
    }

    public static double Unix(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;
    public static DateTime FromUnix(double s) => DateTime.UnixEpoch.AddTicks((long)Math.Round(s * TimeSpan.TicksPerSecond));
    public static string Iso() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    public static bool IsDir(string p) => Directory.Exists(p) && !IsLink(p);
    public static bool IsLink(string p)
    {
        // Any reparse point counts as a link, except cloud placeholders (OneDrive & co. files are reparse points too).
        try { return File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint) && !DoctorCloud.IsCloudTag(DoctorCloud.ReparseTag(p)); }
        catch { return false; }
    }
    public static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    public static IEnumerable<string> List(string dir)
    {
        string[] names;
        try { names = Directory.GetFileSystemEntries(dir); } catch { yield break; }
        foreach (var p in names.OrderBy(x => x, StringComparer.Ordinal))
        {
            var n = System.IO.Path.GetFileName(p);
            if (n is ".DS_Store" or "desktop.ini" or "Thumbs.db") continue;
            yield return p;
        }
    }

    /// Every regular file under `path` (or `path` itself), never following links.
    public static List<string> Files(string path)
    {
        var out_ = new List<string>();
        if (IsLink(path)) return out_;
        if (File.Exists(path)) { out_.Add(path); return out_; }
        foreach (var p in List(path)) out_.AddRange(Files(p));
        return out_;
    }

    /// Logical size, newest file mtime and file count of a file or tree (an empty folder uses its own mtime).
    public static (long Size, double Newest, int Count) TreeStats(string path)
    {
        long size = 0; double newest = 0; int count = 0;
        void Walk(string p)
        {
            if (IsLink(p)) { try { newest = Math.Max(newest, Unix(File.GetLastWriteTimeUtc(p))); } catch { } return; }
            if (Directory.Exists(p)) { foreach (var c in List(p)) Walk(c); return; }
            if (!File.Exists(p)) return;
            var fi = new FileInfo(p);
            size += fi.Length; count++;
            newest = Math.Max(newest, Unix(fi.LastWriteTimeUtc));
        }
        Walk(path);
        if (count == 0) { try { newest = Unix(Directory.GetLastWriteTimeUtc(path)); } catch { } }
        return (size, newest, count);
    }

    public static Fingerprint? FingerprintOf(string path)
    {
        if (Directory.Exists(path)) { var t = TreeStats(path); return new Fingerprint(t.Size, t.Newest, null); }
        if (!File.Exists(path)) return null;
        var fi = new FileInfo(path);
        return new Fingerprint(fi.Length, Unix(fi.LastWriteTimeUtc), fi.Length <= 1L << 30 ? Sha256File(path) : null);
    }

    /// Bytes the file occupies on disk (NTFS: the compressed size); the logical length elsewhere.
    public static long Allocated(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            uint lo = GetCompressedFileSizeW(path, out uint hi);
            if (lo != 0xFFFFFFFF || Marshal.GetLastWin32Error() == 0) return ((long)hi << 32) | lo;
        }
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    public static bool IsCompressed(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.Compressed); } catch { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);
}
