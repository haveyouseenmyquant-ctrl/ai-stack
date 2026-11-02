using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;

namespace AIStack.Doctor;

/// Tier 0 only: reads metadata and samples, never writes user files (Windows twin of Scanner.swift).
public class DoctorScanner
{
    readonly string home;
    readonly DoctorCatalogue catalogue;
    readonly DoctorGuard guardian;
    readonly List<string> optedIn;
    readonly bool testRoot;
    readonly double now = DoctorFS.Unix(DateTime.UtcNow);
    public readonly List<string> Skipped = new();

    public DoctorScanner(string home, DoctorCatalogue catalogue, List<string> optedInFolders, bool testRoot)
    {
        this.home = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar);
        this.catalogue = catalogue;
        guardian = new DoctorGuard(this.home, catalogue);
        optedIn = optedInFolders.Select(Path.GetFullPath).ToList();
        this.testRoot = testRoot;
    }

    /// In test mode the Recycle Bin is `<root>/RecycleBinFixture`.
    public static string TrashDir(string home) => Path.Combine(home, "RecycleBinFixture");

    record Hit(string Path, long Bytes, string Reason, string? Flag = null);

    public List<Finding> Scan()
    {
        var out_ = new List<Finding>();
        // Tier 1 candidates first, destructive options after.
        foreach (var c in catalogue.Categories.OrderBy(c => (int)catalogue.TierOf(c)))
        {
            var roots = (c.Paths.GetValueOrDefault("windows") ?? new()).Select(p => DoctorCatalogue.Expand(p, home)).ToList();
            var found = new List<Hit>();
            switch (c.Match.Kind)
            {
                case "coldCompressible": found = ColdCompressible(); break;
                case "cloudFiles": found = CloudFiles(); break;
                case "optedInFolders":
                    found = optedIn.Where(DoctorFS.IsDir).Select(f => new Hit(f, DoctorFS.TreeStats(f).Size, "you marked this folder for archiving")).ToList();
                    break;
                case "children": found = roots.SelectMany(r => Children(r, Days(c.Match.OlderThanDays))).ToList(); break;
                case "extensions": found = roots.SelectMany(r => WithExtensions(r, c.Match.Extensions ?? new(), Days(c.Match.OlderThanDays))).ToList(); break;
                case "duplicates": found = Duplicates(roots); break;
                case "namedDirs": found = roots.SelectMany(r => NamedDirs(r, c.Match.Name ?? "", Days(c.Match.OlderThanDays))).ToList(); break;
                case "self":
                    if (c.Action == DoctorAction.EmptyTrash)
                    {
                        if (testRoot) roots = new() { TrashDir(home) };
                        else Skipped.Add("trash: Recycle Bin size not read yet");
                    }
                    foreach (var r in roots)
                    {
                        var t = DoctorFS.TreeStats(r);
                        if (t.Count > 0) found.Add(new Hit(r, t.Size, $"{t.Count} items"));
                    }
                    break;
                case "startupItems": found = RunKeys(); break;
                case "clamav":
                    // Windows Defender quarantines files itself, so these are reported (Tier 0), never acted on.
                    foreach (var d in DefenderDetections(roots))
                        out_.Add(new Finding { Id = Finding.ItemId(c.Id, d.Path), Category = c.Id, Action = DoctorAction.Report, Tier = 0,
                                               Path = d.Path, Bytes = File.Exists(d.Path) ? new FileInfo(d.Path).Length : 0,
                                               Reason = $"Windows Defender detected {d.Threat}; restore or remove it in Windows Security > Protection history" });
                    break;
                case "busyProcesses":
                    foreach (var b in BusyProcesses())
                        out_.Add(new Finding { Id = Finding.ItemId(c.Id, $"pid:{b.Pid}:{b.Name}"), Category = c.Id, Action = c.Action,
                                               Tier = (int)catalogue.TierOf(c), Path = b.Path, Reason = $"{b.Cpu:F0}% CPU",
                                               ProcessName = b.Name, Pid = b.Pid });
                    break;
                default: Skipped.Add($"unknown matcher {c.Match.Kind} in {c.Id}"); break;
            }
            foreach (var h in found)
            {
                if (c.Action != DoctorAction.Report && guardian.Refusal(h.Path) is string why)
                {
                    Skipped.Add($"{c.Id}: {Rel(h.Path)}: {why}");
                    continue;
                }
                out_.Add(new Finding { Id = Finding.ItemId(c.Id, h.Path), Category = c.Id, Action = c.Action, Tier = (int)catalogue.TierOf(c),
                                       Path = h.Path, Bytes = h.Bytes, Reason = h.Reason, Flag = h.Flag, Fingerprint = DoctorFS.FingerprintOf(h.Path) });
            }
        }
        return out_;
    }

    double Days(string? key) => key == null ? 0 : catalogue.Value(key);
    string Rel(string p) => p.StartsWith(home + Path.DirectorySeparatorChar, DoctorFS.PathCmp) ? "~\\" + p[(home.Length + 1)..] : p;
    double AgeDays(DateTime utc) => (now - DoctorFS.Unix(utc)) / 86400;

    // ---- Tier 1: cold, compressible files

    static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".sys", ".msi", ".msix", ".appx", ".ocx", ".cpl", ".scr", ".com", ".bat", ".cmd", ".ps1", ".vbs" };

    List<Hit> ColdCompressible()
    {
        double cold = catalogue.Value("coldDays"), gainNeeded = catalogue.Value("minCompressionGain");
        long minBytes = (long)catalogue.Value("minCompressBytes");
        var noExt = new HashSet<string>(catalogue.IncompressibleExtensions, StringComparer.OrdinalIgnoreCase);
        var out_ = new List<Hit>();
        void Walk(string dir)
        {
            foreach (var p in DoctorFS.List(dir))
            {
                var name = Path.GetFileName(p);
                if (DoctorFS.IsLink(p)) continue;
                if (Directory.Exists(p))
                {
                    // Hidden folders, AppData (app and system data), git trees and opted-in folders are not walked.
                    if (name.StartsWith('.') || p.Equals(Path.Combine(home, "AppData"), DoctorFS.PathCmp)) continue;
                    if (DoctorFS.Exists(Path.Combine(p, ".git")) || optedIn.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
                    try { if (File.GetAttributes(p).HasFlag(FileAttributes.Hidden | FileAttributes.System)) continue; } catch { continue; }
                    Walk(p);
                    continue;
                }
                FileInfo fi;
                try { fi = new FileInfo(p); } catch { continue; }
                if (!fi.Exists || fi.Length < minBytes || fi.Attributes.HasFlag(FileAttributes.Compressed)
                    || fi.Attributes.HasFlag(FileAttributes.System) || fi.Attributes.HasFlag(FileAttributes.Offline)
                    || fi.Attributes.HasFlag(FileAttributes.ReparsePoint)   // cloud placeholders: eviction frees them
                    || noExt.Contains(fi.Extension) || CodeExtensions.Contains(fi.Extension)) continue;
                // Last access is only kept coarsely by NTFS (and our own hashing touches it); modified time decides.
                if (AgeDays(fi.LastWriteTimeUtc) < cold) continue;
                var gain = SampleGain(p);
                if (gain < gainNeeded) continue;
                out_.Add(new Hit(p, fi.Length, $"cold {AgeDays(fi.LastWriteTimeUtc):F0} days, sample shrinks {gain * 100:F0}%"));
            }
        }
        Walk(home);
        return out_;
    }

    // ---- Tier 1: local copies of cloud files

    /// In-sync, fully local, unpinned placeholders under any sync root in the home folder, untouched for cloudColdDays.
    List<Hit> CloudFiles()
    {
        var out_ = new List<Hit>();
        if (!OperatingSystem.IsWindows()) return out_;
        double cold = catalogue.Value("cloudColdDays");
        void Walk(string dir)
        {
            foreach (var p in DoctorFS.List(dir))
            {
                if (DoctorFS.IsLink(p)) continue;
                if (Directory.Exists(p))
                {
                    if (p.Equals(Path.Combine(home, "AppData"), DoctorFS.PathCmp)) continue;
                    Walk(p);
                    continue;
                }
                try { if (!File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) continue; } catch { continue; }
                if (DoctorCloud.PlaceholderState(p) is not { InSync: true, Pinned: false, Dehydrated: false }) continue;
                var fi = new FileInfo(p);
                if (AgeDays(fi.LastWriteTimeUtc) < cold) continue;
                out_.Add(new Hit(p, DoctorFS.Allocated(p), $"synced to the cloud, untouched {AgeDays(fi.LastWriteTimeUtc):F0} days"));
            }
        }
        Walk(home);
        return out_;
    }

    /// Deflate trial on the first 64 KiB (NTFS LZNT1 saves somewhat less; execution checks the real result).
    static double SampleGain(string p)
    {
        try
        {
            var buf = new byte[64 << 10];
            int n;
            using (var f = File.OpenRead(p)) n = f.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            if (n == 0) return 0;
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(buf, 0, n);
            return 1 - (double)ms.Length / n;
        }
        catch { return 0; }
    }

    // ---- Tier 2 matchers

    List<Hit> Children(string dir, double olderThan) =>
        DoctorFS.List(dir).Select(p => (p, t: DoctorFS.TreeStats(p))).Where(x => x.t.Size > 0 && (now - x.t.Newest) / 86400 >= olderThan)
            .Select(x => new Hit(x.p, x.t.Size, $"{x.t.Count} files, untouched {(now - x.t.Newest) / 86400:F0} days")).ToList();

    List<Hit> WithExtensions(string dir, List<string> exts, double olderThan) =>
        DoctorFS.List(dir).Where(p => File.Exists(p) && exts.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Select(p => new FileInfo(p)).Where(fi => AgeDays(fi.LastWriteTimeUtc) >= olderThan)
            .Select(fi => new Hit(fi.FullName, fi.Length, $"downloaded {AgeDays(fi.LastWriteTimeUtc):F0} days ago")).ToList();

    List<Hit> Duplicates(List<string> roots)
    {
        long minBytes = (long)catalogue.Value("duplicateMinBytes");
        var bySize = new Dictionary<long, HashSet<string>>();
        foreach (var r in roots)
            foreach (var f in DoctorFS.Files(r))
            {
                var rel = f[r.Length..];
                if (rel.Split(Path.DirectorySeparatorChar).Any(c => c.StartsWith('.'))) continue;
                var len = new FileInfo(f).Length;
                if (len >= minBytes) (bySize.TryGetValue(len, out var s) ? s : bySize[len] = new()).Add(f);
            }
        var out_ = new List<Hit>();
        foreach (var (size, files) in bySize.Where(kv => kv.Value.Count > 1))
            foreach (var same in files.GroupBy(f => DoctorFS.Sha256File(f)).Where(g => g.Key != null && g.Count() > 1))
            {
                // Keep the oldest copy; propose the rest.
                var sorted = same.OrderBy(f => File.GetLastWriteTimeUtc(f)).ThenBy(f => f, StringComparer.Ordinal).ToList();
                foreach (var f in sorted.Skip(1)) out_.Add(new Hit(f, size, $"identical to {Rel(sorted[0])}"));
            }
        return out_;
    }

    List<Hit> NamedDirs(string root, string name, double olderThan)
    {
        var out_ = new List<Hit>();
        void Walk(string dir)
        {
            foreach (var p in DoctorFS.List(dir).Where(DoctorFS.IsDir))
            {
                var n = Path.GetFileName(p);
                if (n.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    var age = (now - DoctorFS.TreeStats(dir).Newest) / 86400;
                    if (age >= olderThan) out_.Add(new Hit(p, DoctorFS.TreeStats(p).Size, $"project untouched {age:F0} days"));
                }
                else if (!n.StartsWith('.')) Walk(p);
            }
        }
        Walk(root);
        return out_;
    }

    // ---- Persistence: HKCU Run / RunOnce values (the finding's path is the program they start)

    List<Hit> RunKeys()
    {
        var out_ = new List<Hit>();
        if (!OperatingSystem.IsWindows()) return out_;
        foreach (var key in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" })
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key);
            if (k == null) continue;
            foreach (var name in k.GetValueNames())
            {
                var program = ProgramOf(Environment.ExpandEnvironmentVariables(k.GetValue(name)?.ToString() ?? ""));
                // In test mode only items pointing into the fixture count; the runner's own items are not ours to judge.
                if (testRoot && !program.StartsWith(home + Path.DirectorySeparatorChar, DoctorFS.PathCmp)) continue;
                if (program == "" || !File.Exists(program)) { out_.Add(new Hit(program, 0, $"{name} starts a missing program", "orphaned")); continue; }
                if (!IsSigned(program)) out_.Add(new Hit(program, 0, $"{name} (HKCU\\{key}) starts an unsigned program", "unsigned"));
            }
        }
        return out_;
    }

    static string ProgramOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"')) { var end = command.IndexOf('"', 1); return end > 0 ? command[1..end] : command.Trim('"'); }
        var exe = command.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command;
    }

    /// Has an embedded Authenticode signature. (Chain and revocation checks via WinVerifyTrust come with the UI work.)
    static bool IsSigned(string path)
    {
        try { return X509Certificate.CreateFromSignedFile(path) != null; } catch { return false; }
    }

    // ---- Processes

    record Busy(int Pid, double Cpu, string Name, string Path);

    List<Busy> BusyProcesses()
    {
        double threshold = catalogue.Value("busyCpuPercent");
        var me = Environment.ProcessId;
        Dictionary<int, TimeSpan> Sample()
        {
            var r = new Dictionary<int, TimeSpan>();
            foreach (var p in Process.GetProcesses()) { try { r[p.Id] = p.TotalProcessorTime; } catch { } finally { p.Dispose(); } }
            return r;
        }
        var a = Sample(); var t0 = DateTime.UtcNow;
        Thread.Sleep(1500);
        var b = Sample(); var secs = (DateTime.UtcNow - t0).TotalSeconds;
        var out_ = new List<Busy>();
        foreach (var (pid, cpuB) in b)
        {
            if (pid == me || !a.TryGetValue(pid, out var cpuA)) continue;
            var pct = (cpuB - cpuA).TotalSeconds / secs * 100;
            if (pct < threshold) continue;
            var cmd = CommandLine(pid);
            if (testRoot && !cmd.Contains(home, StringComparison.OrdinalIgnoreCase)) continue;
            var words = SplitArgs(cmd);
            if (words.Count == 0) continue;
            var script = words.Skip(1).FirstOrDefault(w => new[] { ".py", ".js", ".ps1", ".rb" }.Contains(System.IO.Path.GetExtension(w).ToLowerInvariant()));
            var exe = script ?? words[0];
            out_.Add(new Busy(pid, pct, System.IO.Path.GetFileName(exe), exe));
        }
        return out_;
    }

    static List<string> SplitArgs(string cmd)
    {
        var out_ = new List<string>(); var cur = new System.Text.StringBuilder(); bool q = false;
        foreach (var ch in cmd)
        {
            if (ch == '"') { q = !q; continue; }
            if (ch == ' ' && !q) { if (cur.Length > 0) { out_.Add(cur.ToString()); cur.Clear(); } continue; }
            cur.Append(ch);
        }
        if (cur.Length > 0) out_.Add(cur.ToString());
        return out_;
    }

    /// The process's command line (Windows: CIM via PowerShell, only for the few busy processes; elsewhere ps).
    static string CommandLine(int pid)
    {
        var (tool, args) = OperatingSystem.IsWindows()
            ? ("powershell.exe", $"-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"")
            : ("/bin/ps", $"-o args= -p {pid}");
        return Run(tool, args).Text.Trim();
    }

    public static (int Status, string Text) Run(string tool, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, args)
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, text);
        }
        catch (Exception e) { return (-1, e.Message); }
    }

    // ---- Windows Defender (read-only: history, plus a custom scan with remediation off)

    /// Files under `roots` that Defender has detected: its detection history, then a custom scan of the roots with
    /// -DisableRemediation (reports without touching anything) for files real-time protection hasn't seen.
    List<(string Path, string Threat)> DefenderDetections(List<string> roots)
    {
        var hits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) { Skipped.Add("malware: Windows Defender needs Windows"); return new(); }
        bool Under(string p) => roots.Any(r => p.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        void Add(string resource, string threat)
        {
            // Resources look like "file:_C:\path\name" (or "containerfile:_…" for archives).
            var i = resource.IndexOf(":_", StringComparison.Ordinal);
            if (i < 0) return;
            var p = resource[(i + 2)..].Trim();
            if (p.Contains("->")) p = p[..p.IndexOf("->", StringComparison.Ordinal)];
            try { p = Path.GetFullPath(p); } catch { return; }
            if (Under(p)) hits.TryAdd(p, threat);
        }
        void History()
        {
            var r = Run("powershell.exe", "-NoProfile -NonInteractive -Command \"$t=@{}; Get-MpThreat | % { $t[[string]$_.ThreatID]=$_.ThreatName }; " +
                        "Get-MpThreatDetection | % { $n=$t[[string]$_.ThreatID]; foreach($x in $_.Resources){ $n + '|' + $x } }\"");
            if (r.Status != 0) { Skipped.Add("malware: Defender history unreadable: " + r.Text.Trim().Split('\n')[0]); return; }
            foreach (var line in r.Text.Split('\n'))
                if (line.Split('|', 2) is [var threat, var res]) Add(res.Trim(), threat.Trim().Length > 0 ? threat.Trim() : "a threat");
        }
        History();
        var mp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        if (File.Exists(mp))
        {
            foreach (var root in roots.Where(Directory.Exists))
            {
                var r = Run(mp, $"-Scan -ScanType 3 -File \"{root}\" -DisableRemediation");
                // Exit code 2 = threats found. The listing format varies by version, so pick out every "file:_" resource.
                if (r.Status == 2)
                    foreach (var line in r.Text.Split('\n'))
                        if (line.Contains(":_")) Add(line.Trim(), "a threat");
            }
            History();
        }
        else Skipped.Add("malware: MpCmdRun.exe not found; only Defender's history was read");
        return hits.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    // ---- Security posture (read-only queries; no admin needed)

    public static Dictionary<string, string> Posture()
    {
        var r = new Dictionary<string, string>();
        if (!OperatingSystem.IsWindows()) return r;
        var mp = Run("powershell.exe", "-NoProfile -NonInteractive -Command \"$s=Get-MpComputerStatus; " +
                     "'realtime=' + $s.RealTimeProtectionEnabled; 'signatureAgeDays=' + $s.AntivirusSignatureAge\"").Text;
        foreach (var line in mp.Split('\n'))
            if (line.Split('=', 2) is [var k, var v]) r["defender." + k.Trim()] = v.Trim().ToLowerInvariant();
        var fw = Run("netsh", "advfirewall show currentprofile state").Text;
        r["firewall"] = fw.Contains("ON") ? "on" : fw.Contains("OFF") ? "off" : "unknown";
        return r;
    }
}
