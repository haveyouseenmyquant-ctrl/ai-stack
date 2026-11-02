using System.IO.Compression;
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
                case DoctorAction.ArchiveFolder: Tally(o, ArchiveFolder(f)); break;
                case DoctorAction.EvictCloud: case DoctorAction.Quarantine:
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
                             DoctorFS.Unix(fi.CreationTimeUtc), (int)fi.Attributes, hash ? DoctorFS.Sha256File(path) : null)
            { MtimeTicks = fi.LastWriteTimeUtc.Ticks, BirthTicks = fi.CreationTimeUtc.Ticks };
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
            SetTimes(temp, before);
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

    // ---- Archive an opted-in folder

    /// zip into a temp sibling → extract it again and compare every file's SHA-256 and the entry list →
    /// rename to `<folder>.zip` → rename the folder aside → remove it. Until the zip is verified and in place the
    /// folder is untouched; after that every byte is in the zip, and the manifest in the journal restores times and attributes.
    (string, long) ArchiveFolder(Finding f)
    {
        var path = Path.GetFullPath(f.Path).TrimEnd(Path.DirectorySeparatorChar);
        if (guardian.Refusal(path) is string why) { Log(f, "refused", why); return ("refused", 0); }
        if (!DoctorFS.IsDir(path)) { Log(f, "skipped", "not a folder"); return ("skipped", 0); }
        var zip = path + ".zip";
        if (DoctorFS.Exists(zip)) { Log(f, "skipped", Path.GetFileName(zip) + " already exists"); return ("skipped", 0); }
        if (Manifest(path) is not { } manifest) { Log(f, "skipped", "contains links or unreadable files"); return ("skipped", 0); }
        if (manifest.FirstOrDefault(e => !e.Dir && IsOpen(Join(path, e.Rel))) is { } open)
        { Log(f, "skipped", open.Rel + " is open in another process"); return ("skipped", 0); }
        var id = DoctorJournal.NewId();
        var tag = ArchiveTag(path, id);
        string temp = tag + ".zip", check = tag + "-v", aside = tag + "-d";
        var rec = new JournalRecord { Id = id, Checkup = checkup, Phase = "intent", Action = DoctorAction.ArchiveFolder, Tier = 1, PlanItem = f.Id,
                                      From = path, To = zip, Temp = temp, Before = manifest[0].State, Reason = f.Reason, Manifest = manifest };
        journal.Append(rec);
        (string, long) Fail(string reason)
        {
            try { File.Delete(temp); } catch { }
            RemoveTree(check);
            var e = rec.Copy(); e.Phase = "failed"; e.Reason = reason; e.Ts = DoctorFS.Iso(); e.Manifest = null;
            journal.Append(e);
            return ("failed", 0);
        }
        try { WriteZip(path, manifest, temp); } catch (Exception e) { return Fail("zip: " + e.Message); }
        CrashPoint("archive-zipped");
        try { ZipFile.ExtractToDirectory(temp, check); } catch (Exception e) { return Fail("extract: " + e.Message); }
        if (Manifest(check) is not { } back || !SameContents(manifest, back)) return Fail("verify: extracted zip differs");
        RemoveTree(check);
        if (State(temp) is not { } zstate) return Fail("zip vanished");
        // The folder must not have changed while we zipped it.
        if (Manifest(path, hash: false) is not { } again || !SameShape(manifest, again)) return Fail("changed during archiving");
        var progress = rec.Copy(); progress.Phase = "verified"; progress.After = zstate; progress.Manifest = null; progress.Ts = DoctorFS.Iso();
        journal.Append(progress);
        CrashPoint("archive-verified");
        try { File.Move(temp, zip, overwrite: false); } catch (Exception e) { return Fail("rename zip: " + e.Message); }
        CrashPoint("archive-zip-in-place");
        try { Directory.Move(path, aside); }
        catch (Exception e) { try { File.Delete(zip); } catch { } return Fail("rename folder: " + e.Message); }
        CrashPoint("archive-aside");
        RemoveTree(aside);
        CrashPoint("archive-removed");
        var freed = Math.Max(0, manifest.Sum(e => e.State.Blocks) - zstate.Blocks) * 512;
        var done = rec.Copy(); done.Phase = "done"; done.After = zstate; done.Temp = null; done.FreedBytes = freed; done.Ts = DoctorFS.Iso();
        journal.Append(done);
        return ("done", freed);
    }

    /// Extract next to the zip, verify against the manifest, restore times and attributes, rename into place, remove the zip.
    bool Unarchive(JournalRecord r)
    {
        if (r.From is not string path || r.To is not string zip || r.Manifest is not { } manifest) return false;
        if (DoctorFS.Exists(path) || !File.Exists(zip)) { AppendUndo(r, "undo-failed", "folder exists or zip missing"); return false; }
        if (guardian.Refusal(zip, forWrite: false) is string why) { AppendUndo(r, "undo-failed", why); return false; }
        var out_ = ArchiveTag(path, r.Id) + "-u";
        RemoveTree(out_);
        AppendUndo(r, "undo-intent", null, out_);
        bool Fail(string reason) { RemoveTree(out_); AppendUndo(r, "undo-failed", reason); return false; }
        try { Directory.CreateDirectory(out_); ZipFile.ExtractToDirectory(zip, out_); } catch (Exception e) { return Fail("extract: " + e.Message); }
        CrashPoint("unarchive-extracted");
        if (Manifest(out_) is not { } back || !SameContents(manifest, back)) return Fail("verify: zip no longer matches");
        try
        {
            // Deepest first so restoring a folder's times isn't undone by touching its children; attributes last (read-only).
            foreach (var e in manifest.OrderByDescending(e => e.Rel.Length))
            {
                var p = Join(out_, e.Rel);
                if (e.Dir) { Directory.SetCreationTimeUtc(p, e.State.BirthUtc); Directory.SetLastWriteTimeUtc(p, e.State.MtimeUtc); }
                else SetTimes(p, e.State);
                File.SetAttributes(p, (FileAttributes)e.State.Mode & ~FileAttributes.Compressed);
            }
            Directory.Move(out_, path);
        }
        catch (Exception e) { return Fail("restore: " + e.Message); }
        CrashPoint("unarchive-in-place");
        try { File.Delete(zip); } catch { }
        AppendUndo(r, "undo-done", null);
        return true;
    }

    /// An interrupted archive or un-archive. Every state on the way keeps the folder or a verified zip (or both).
    void RecoverArchive(JournalRecord r, List<JournalRecord> records)
    {
        if (r.From is not string path || r.To is not string zip || r.Temp is not string temp) return;
        var tag = ArchiveTag(path, r.Id);
        var e = r.Copy(); e.Ts = DoctorFS.Iso(); e.Temp = null;
        if (r.Phase == "undo-intent")
        {
            // The folder is only renamed into place after it verified, so the restore is complete.
            if (DoctorFS.Exists(path)) { try { File.Delete(zip); } catch { } e.Phase = "undo-done"; e.Reason = "interrupted after restore; finished"; }
            else { RemoveTree(temp); e.Phase = "undo-failed"; e.Reason = "interrupted; recovered"; }
            journal.Append(e); return;
        }
        try { File.Delete(temp); } catch { }
        RemoveTree(tag + "-v");
        var verified = records.LastOrDefault(x => x.Id == r.Id && x.Phase == "verified")?.After;
        if (Directory.Exists(path))
        {
            // Folder intact: drop our zip if it got into place, and leave things as they were.
            if (verified != null && File.Exists(zip) && DoctorFS.Sha256File(zip) == verified.Sha256) { try { File.Delete(zip); } catch { } }
            e.Phase = "failed"; e.Reason = "interrupted; recovered"; e.Manifest = null;
        }
        else if (verified != null && File.Exists(zip))
        {
            // The verified zip is in place and the folder was moved aside: finish removing it.
            RemoveTree(tag + "-d");
            e.Phase = "done"; e.After = verified; e.Reason = "interrupted while removing the folder; finished";
            e.FreedBytes = Math.Max(0, (r.Manifest ?? new()).Sum(m => m.State.Blocks) - verified.Blocks) * 512;
        }
        else
        {
            // Never expected: put the aside copy back rather than lose anything.
            if (Directory.Exists(tag + "-d")) Directory.Move(tag + "-d", path);
            e.Phase = "failed"; e.Reason = "interrupted; folder restored"; e.Manifest = null;
        }
        journal.Append(e);
    }

    static string ArchiveTag(string path, string id) =>
        Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".ld-" + id[..8]);

    static string Join(string root, string rel) => rel.Length == 0 ? root : Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// One zip entry per manifest entry ("/" separators, folders as "name/"), written to a new file and flushed.
    static void WriteZip(string root, List<ManifestEntry> manifest, string dest)
    {
        using var fs = new FileStream(dest, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var e in manifest.Where(e => e.Rel.Length > 0))
            {
                var entry = z.CreateEntry(e.Dir ? e.Rel + "/" : e.Rel, CompressionLevel.Optimal);
                entry.LastWriteTime = DoctorFS.FromUnix(e.State.Mtime).ToLocalTime();
                if (e.Dir) continue;
                using var o = entry.Open();
                using var i = new FileStream(Join(root, e.Rel), FileMode.Open, FileAccess.Read, FileShare.Read);
                i.CopyTo(o, 1 << 20);
            }
        }
        fs.Flush(flushToDisk: true);
    }

    /// Every entry under `root` (root itself as ""), or null if it holds a link or an unreadable file.
    /// Lists the folder directly (not DoctorFS.List, which hides desktop.ini and Thumbs.db) so nothing is left out of the zip.
    public static List<ManifestEntry>? Manifest(string root, bool hash = true)
    {
        var out_ = new List<ManifestEntry>();
        bool Walk(string p, string rel)
        {
            if (DoctorFS.IsLink(p)) return false;
            if (File.Exists(p))
            {
                if (State(p, hash) is not { } s || (hash && s.Sha256 == null)) return false;
                out_.Add(new ManifestEntry(rel, false, s)); return true;
            }
            if (!Directory.Exists(p)) return false;
            string[] names;
            try { names = Directory.GetFileSystemEntries(p); } catch { return false; }
            var di = new DirectoryInfo(p);
            out_.Add(new ManifestEntry(rel, true, new FileState(0, 0, DoctorFS.Unix(di.LastWriteTimeUtc), DoctorFS.Unix(di.CreationTimeUtc), (int)di.Attributes, null)
                { MtimeTicks = di.LastWriteTimeUtc.Ticks, BirthTicks = di.CreationTimeUtc.Ticks }));
            foreach (var c in names.OrderBy(x => x, StringComparer.Ordinal))
            {
                var n = Path.GetFileName(c);
                if (!Walk(c, rel.Length == 0 ? n : rel + "/" + n)) return false;
            }
            return true;
        }
        return Walk(root, "") ? out_ : null;
    }

    /// Same entry list, kinds and file bytes (times are restored from the manifest, so they are not compared).
    static bool SameContents(List<ManifestEntry> a, List<ManifestEntry> b)
    {
        var bm = b.GroupBy(e => e.Rel).ToDictionary(g => g.Key, g => g.First());
        return a.Count == b.Count && a.All(e => bm.TryGetValue(e.Rel, out var o) && o.Dir == e.Dir &&
                                               (e.Dir || (o.State.Sha256 == e.State.Sha256 && o.State.Size == e.State.Size)));
    }

    /// Same entry list, sizes and file mtimes: a cheap "did anything change" check.
    static bool SameShape(List<ManifestEntry> a, List<ManifestEntry> b)
    {
        var bm = b.GroupBy(e => e.Rel).ToDictionary(g => g.Key, g => g.First());
        return a.Count == b.Count && a.All(e => bm.TryGetValue(e.Rel, out var o) && o.Dir == e.Dir &&
                                               (e.Dir || (o.State.Size == e.State.Size && o.State.Mtime == e.State.Mtime)));
    }

    /// Deletes one of our own temp or aside trees; clears read-only first, which Directory.Delete refuses.
    static void RemoveTree(string p)
    {
        if (!Directory.Exists(p)) return;
        try
        {
            foreach (var f in Directory.EnumerateFileSystemEntries(p, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(p, recursive: true);
        }
        catch { }
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
                case DoctorAction.ArchiveFolder: if (Unarchive(r)) o.Done++; else o.Failed++; break;
                case DoctorAction.Report: case DoctorAction.EvictCloud: case DoctorAction.Quarantine:
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
            SetTimes(temp, before);
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
            if (r.Action == DoctorAction.ArchiveFolder) { RecoverArchive(r, records); n++; continue; }
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

    static void SetTimes(string path, FileState s)
    {
        File.SetCreationTimeUtc(path, s.BirthUtc);
        File.SetLastWriteTimeUtc(path, s.MtimeUtc);
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
