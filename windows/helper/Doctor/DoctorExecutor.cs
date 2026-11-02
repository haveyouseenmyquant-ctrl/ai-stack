using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AIStack.Doctor;

/// The only code that writes user files. Every action passes Guard → (Tier 2: approval) → journal intent.
/// Windows twin of Executor.swift; same journal phases, so `verify-journal` and `recover` read both.
public class DoctorExecutor
{
    readonly string home;
    readonly DoctorGuard guardian;
    readonly DoctorJournal journal;
    readonly string checkup;
    readonly bool testRoot;

    public DoctorExecutor(string home, DoctorCatalogue catalogue, DoctorJournal journal, string checkup, bool testRoot)
    {
        this.home = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar);
        guardian = new DoctorGuard(this.home, catalogue);
        this.journal = journal;
        this.checkup = checkup;
        this.testRoot = testRoot;
    }

    public class Outcome { public int Done { get; set; } public int Skipped { get; set; } public int Refused { get; set; } public int Failed { get; set; } public long FreedBytes { get; set; } }

    // ---- Tier 1

    public Outcome RunTier1(List<Finding> findings)
    {
        var o = new Outcome();
        foreach (var f in findings.Where(f => f.Action.Tier() == DoctorTier.Automatic))
        {
            switch (f.Action)
            {
                case DoctorAction.Compress: Tally(o, Compress(f)); break;
                case DoctorAction.ArchiveFolder: case DoctorAction.EvictCloud: case DoctorAction.Quarantine:
                    // Not implemented on Windows yet: journalled as skipped so nothing happens silently.
                    Log(f, "skipped", "not implemented yet"); o.Skipped++; break;
                case DoctorAction.Report: case DoctorAction.MoveToTrash: case DoctorAction.EmptyTrash: case DoctorAction.DisableStartupItem:
                case DoctorAction.DisableBrowserExtension: case DoctorAction.QuitProcess: case DoctorAction.UninstallApp: case DoctorAction.MoveFile:
                    o.Refused++; break;
                default: throw new InvalidOperationException($"no tier for {f.Action}");
            }
        }
        return o;
    }

    static void Tally(Outcome o, (string Result, long Freed) r)
    {
        switch (r.Result)
        {
            case "done": o.Done++; o.FreedBytes += r.Freed; break;
            case "skipped": o.Skipped++; break;
            case "refused": o.Refused++; break;
            default: o.Failed++; break;
        }
    }

    void Log(Finding f, string phase, string? reason) =>
        journal.Append(new JournalRecord { Id = DoctorJournal.NewId(), Checkup = checkup, Phase = phase, Action = f.Action,
                                           Tier = (int)f.Action.Tier(), PlanItem = f.Id, From = f.Path, Reason = reason });

    public static FileState? State(string path, bool hash = true)
    {
        if (!File.Exists(path)) return null;
        var fi = new FileInfo(path);
        return new FileState(fi.Length, (DoctorFS.Allocated(path) + 511) / 512, DoctorFS.Unix(fi.LastWriteTimeUtc),
                             DoctorFS.Unix(fi.CreationTimeUtc), (int)fi.Attributes, hash ? DoctorFS.Sha256File(path) : null);
    }

    /// NTFS compression into a fresh sibling → copy bytes → restore times and attributes → verify → atomic replace.
    /// The original is untouched until the final rename, so a crash leaves it intact (plus at most a temp file).
    (string, long) Compress(Finding f)
    {
        var path = f.Path;
        if (guardian.Refusal(path) is string why) { Log(f, "refused", why); return ("refused", 0); }
        if (!OperatingSystem.IsWindows()) { Log(f, "skipped", "NTFS compression needs Windows"); return ("skipped", 0); }
        var before = State(path);
        if (before == null) { Log(f, "skipped", "missing"); return ("skipped", 0); }
        if (f.Fingerprint is { } fp && (fp.Size != before.Size || Math.Abs(fp.Mtime - before.Mtime) > 0.001))
        { Log(f, "skipped", "changed since scan"); return ("skipped", 0); }
        if (IsOpen(path)) { Log(f, "skipped", "open in another process"); return ("skipped", 0); }
        var id = DoctorJournal.NewId();
        var temp = TempSibling(path, id);
        var rec = new JournalRecord { Id = id, Checkup = checkup, Phase = "intent", Action = DoctorAction.Compress, Tier = 1, PlanItem = f.Id,
                                      From = path, To = path, Temp = temp, Before = before, Reason = f.Reason };
        journal.Append(rec);
        (string, long) Fail(string reason)
        {
            try { File.Delete(temp); } catch { }
            var e = rec.Copy(); e.Phase = "failed"; e.Reason = reason; e.Ts = DoctorFS.Iso();
            journal.Append(e);
            return ("failed", 0);
        }
        try
        {
            CopyInto(path, temp, compressed: true);
            CrashPoint("compress-copied");
            SetTimes(temp, before.Mtime, before.Birth);
            File.SetAttributes(temp, (FileAttributes)before.Mode);   // the Compressed bit itself is ignored here
        }
        catch (Exception e) { return Fail("copy: " + e.Message); }
        var after = State(temp);
        if (after == null || after.Sha256 != before.Sha256 || after.Size != before.Size) return Fail("verify: bytes differ");
        if (!DoctorFS.IsCompressed(temp) || after.Blocks >= before.Blocks) return Fail("did not shrink");
        var now = State(path, hash: false);
        if (now == null || now.Size != before.Size || now.Mtime != before.Mtime) return Fail("changed during compression");
        CrashPoint("compress-verified");
        try { File.Move(temp, path, overwrite: true); } catch (Exception e) { return Fail("rename: " + e.Message); }
        var freed = (before.Blocks - after.Blocks) * 512;
        var done = rec.Copy(); done.Phase = "done"; done.After = after; done.FreedBytes = freed; done.Ts = DoctorFS.Iso(); done.Temp = null;
        journal.Append(done);
        return ("done", freed);
    }

    /// Streams the bytes into a new file whose NTFS compression state is set before any data is written.
    static void CopyInto(string src, string dst, bool compressed)
    {
        using (var o = new FileStream(dst, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            SetCompression(o.SafeFileHandle, compressed);
            using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
            i.CopyTo(o, 1 << 20);
            o.Flush(flushToDisk: true);
        }
    }

    // ---- Undo

    public Outcome Undo(List<JournalRecord> records, string? entry)
    {
        var o = new Outcome();
        var undone = records.Where(r => r.Phase == "undo-done").Select(r => r.Id).ToHashSet();
        foreach (var r in Enumerable.Reverse(records).Where(r => r.Phase == "done" && !undone.Contains(r.Id) && (entry == null || r.Id == entry)))
        {
            switch (r.Action)
            {
                case DoctorAction.Compress: if (Decompress(r)) o.Done++; else o.Failed++; break;
                case DoctorAction.MoveToTrash: if (RestoreFromTrash(r)) o.Done++; else o.Failed++; break;
                case DoctorAction.Report: case DoctorAction.EvictCloud: case DoctorAction.ArchiveFolder: case DoctorAction.Quarantine:
                case DoctorAction.EmptyTrash: case DoctorAction.DisableStartupItem: case DoctorAction.DisableBrowserExtension:
                case DoctorAction.QuitProcess: case DoctorAction.UninstallApp: case DoctorAction.MoveFile:
                    o.Skipped++; break;
                default: throw new InvalidOperationException($"no undo for {r.Action}");
            }
        }
        return o;
    }

    bool Decompress(JournalRecord r)
    {
        if (r.From is not string path || r.Before is not FileState before) return false;
        if (guardian.Refusal(path, forWrite: false) is string why) { AppendUndo(r, "undo-failed", why); return false; }
        var temp = TempSibling(path, r.Id + "-u");
        AppendUndo(r, "undo-intent", null, temp);
        try
        {
            CopyInto(path, temp, compressed: false);
            CrashPoint("decompress-copied");
            SetTimes(temp, before.Mtime, before.Birth);
            File.SetAttributes(temp, (FileAttributes)before.Mode & ~FileAttributes.Compressed);
            var s = State(temp);
            if (s == null || s.Sha256 != before.Sha256 || DoctorFS.IsCompressed(temp)) throw new IOException("verify");
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e)
        {
            try { File.Delete(temp); } catch { }
            AppendUndo(r, "undo-failed", e.Message); return false;
        }
        AppendUndo(r, "undo-done", null);
        return true;
    }

    bool RestoreFromTrash(JournalRecord r)
    {
        if (r.From is not string from || r.To is not string to || !DoctorFS.Exists(to) || DoctorFS.Exists(from))
        { AppendUndo(r, "undo-failed", "not in the Recycle Bin any more"); return false; }
        AppendUndo(r, "undo-intent", null);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(from)!);
            if (Directory.Exists(to)) Directory.Move(to, from); else File.Move(to, from);
        }
        catch (Exception e) { AppendUndo(r, "undo-failed", e.Message); return false; }
        AppendUndo(r, "undo-done", null);
        return true;
    }

    void AppendUndo(JournalRecord r, string phase, string? reason, string? temp = null)
    {
        var u = r.Copy(); u.Phase = phase; u.Reason = reason; u.Ts = DoctorFS.Iso(); u.Temp = temp;
        journal.Append(u);
    }

    // ---- Crash recovery

    /// An `intent` without a matching end record: remove the temp sibling (the original is intact until the rename).
    public int Recover(List<JournalRecord> records)
    {
        string[] ends = { "done", "failed", "skipped", "refused", "undo-done", "undo-failed" };
        var ended = records.Where(r => ends.Contains(r.Phase)).Select(r => r.Id + "|" + (r.Phase.StartsWith("undo") ? "u" : "")).ToHashSet();
        int n = 0;
        foreach (var r in records.Where(r => r.Phase is "intent" or "undo-intent"))
        {
            if (ended.Contains(r.Id + "|" + (r.Phase == "undo-intent" ? "u" : "")) || r.Temp is not string temp) continue;
            if (r.From is string orig && File.Exists(orig)) { try { File.Delete(temp); } catch { } }
            else if (r.From is string o2 && File.Exists(temp)) File.Move(temp, o2);   // never expected, but never leave it missing
            var e = r.Copy(); e.Phase = r.Phase == "intent" ? "failed" : "undo-failed"; e.Reason = "interrupted; recovered"; e.Ts = DoctorFS.Iso();
            journal.Append(e);
            n++;
        }
        return n;
    }

    // ---- Tier 2

    /// Records the user's decision. Rules can only approve ruleable action types.
    public void Approve(List<Finding> items, string by)
    {
        foreach (var f in items.Where(f => f.Action.Tier() == DoctorTier.Plan))
        {
            if (by.StartsWith("rule:") && !f.Action.Ruleable()) continue;
            journal.Append(new JournalRecord { Id = DoctorJournal.NewId(), Checkup = checkup, Phase = "approval", Action = f.Action, Tier = 2,
                                               PlanItem = f.Id, From = f.Path, Fingerprint = f.Fingerprint, By = by });
        }
    }

    public Outcome Execute(TreatmentPlan plan, List<JournalRecord> approvals)
    {
        var o = new Outcome();
        var approved = new Dictionary<string, JournalRecord>();
        foreach (var r in approvals.Where(r => r.Phase == "approval" && r.PlanItem != null)) approved.TryAdd(r.PlanItem!, r);
        foreach (var item in plan.Groups.SelectMany(g => g.Items))
        {
            if (guardian.Refusal(item.Path) is string why) { Log(item, "refused", why); o.Refused++; continue; }
            if (!approved.TryGetValue(item.Id, out var a) || a.From != item.Path || a.Action != item.Action)
            { Log(item, "refused", "no recorded approval"); o.Refused++; continue; }
            var now = DoctorFS.FingerprintOf(item.Path);
            if (a.Fingerprint is not { } fp || now == null || fp.Size != now.Size || Math.Abs(fp.Mtime - now.Mtime) >= 0.001 || fp.Sha256 != now.Sha256)
            { Log(item, "skipped", "changed since the plan was made"); o.Skipped++; continue; }
            switch (item.Action)
            {
                case DoctorAction.MoveToTrash: Tally(o, MoveToTrash(item)); break;
                case DoctorAction.EmptyTrash: case DoctorAction.DisableStartupItem: case DoctorAction.DisableBrowserExtension:
                case DoctorAction.QuitProcess: case DoctorAction.UninstallApp: case DoctorAction.MoveFile:
                    Log(item, "skipped", "not implemented yet"); o.Skipped++; break;
                case DoctorAction.Report: case DoctorAction.Compress: case DoctorAction.EvictCloud: case DoctorAction.ArchiveFolder: case DoctorAction.Quarantine:
                    Log(item, "refused", "not a plan action"); o.Refused++; break;
                default: throw new InvalidOperationException($"no tier for {item.Action}");
            }
        }
        return o;
    }

    /// "Delete" = move to the Recycle Bin. In test mode the bin is `<root>/RecycleBinFixture`; on a real machine
    /// this stays disabled until the Recycle Bin move with undo (IFileOperation) lands.
    (string, long) MoveToTrash(Finding f)
    {
        if (!testRoot) { Log(f, "skipped", "Recycle Bin moves need the app"); return ("skipped", 0); }
        var trash = DoctorScanner.TrashDir(home);
        var name = Path.GetFileName(f.Path);
        var dest = Path.Combine(trash, name);
        for (int n = 2; DoctorFS.Exists(dest); n++) dest = Path.Combine(trash, $"{name} {n}");
        var rec = new JournalRecord { Id = DoctorJournal.NewId(), Checkup = checkup, Phase = "intent", Action = DoctorAction.MoveToTrash, Tier = 2,
                                      PlanItem = f.Id, From = f.Path, To = dest, Fingerprint = f.Fingerprint, Reason = f.Reason };
        journal.Append(rec);
        bool ok;
        try
        {
            Directory.CreateDirectory(trash);
            if (Directory.Exists(f.Path)) Directory.Move(f.Path, dest); else File.Move(f.Path, dest);
            ok = true;
        }
        catch { ok = false; }
        var e = rec.Copy(); e.Phase = ok ? "done" : "failed"; e.Ts = DoctorFS.Iso();
        journal.Append(e);
        return (ok ? "done" : "failed", 0);
    }

    // ---- Helpers

    /// Test hook: with --root only, LD_CRASH_AT=<point> kills the process there, like a power cut.
    void CrashPoint(string name)
    {
        if (testRoot && Environment.GetEnvironmentVariable("LD_CRASH_AT") == name) Environment.FailFast("crash point " + name);
    }

    public static string TempSibling(string path, string id) =>
        Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".ld-" + id[..8]);

    static void SetTimes(string path, double mtime, double birth)
    {
        File.SetCreationTimeUtc(path, DoctorFS.FromUnix(birth));
        File.SetLastWriteTimeUtc(path, DoctorFS.FromUnix(mtime));
    }

    /// Open in any process = can't get exclusive access.
    static bool IsOpen(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch { return false; }
    }

    const uint FSCTL_SET_COMPRESSION = 0x9C040;

    static void SetCompression(SafeFileHandle h, bool on)
    {
        if (!OperatingSystem.IsWindows()) return;
        short format = on ? (short)1 : (short)0;   // COMPRESSION_FORMAT_DEFAULT (LZNT1) / NONE
        if (!DeviceIoControl(h, FSCTL_SET_COMPRESSION, ref format, sizeof(short), IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException($"FSCTL_SET_COMPRESSION failed: {Marshal.GetLastWin32Error()}");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref short inBuf, int inSize, IntPtr outBuf, int outSize,
                                       out int returned, IntPtr overlapped);
}
