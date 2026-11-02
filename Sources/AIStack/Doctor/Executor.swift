import Foundation

/// The only code that writes user files. Every action passes Guard → (Tier 2: approval) → journal intent.
final class DoctorExecutor {
    let home: String
    let guardian: DoctorGuard
    let journal: DoctorJournal
    let checkup: String
    let testRoot: Bool

    init(home: String, catalogue: DoctorCatalogue, journal: DoctorJournal, checkup: String, testRoot: Bool) {
        self.home = (home as NSString).resolvingSymlinksInPath
        self.guardian = DoctorGuard(home: self.home, catalogue: catalogue)
        self.journal = journal
        self.checkup = checkup
        self.testRoot = testRoot
    }

    struct Outcome: Codable { var done = 0, skipped = 0, refused = 0, failed = 0, freedBytes: Int64 = 0 }

    // MARK: Tier 1

    func runTier1(_ findings: [Finding]) -> Outcome {
        var o = Outcome()
        for f in findings where f.action.tier == .automatic {
            switch f.action {
            case .compress: tally(&o, compress(f))
            case .archiveFolder: tally(&o, archiveFolder(f))
            case .quarantine: tally(&o, quarantine(f))
            case .evictCloud:
                // Not implemented yet: journalled as skipped so nothing happens silently.
                log(f, phase: "skipped", reason: "not implemented yet"); o.skipped += 1
            case .report, .moveToTrash, .emptyTrash, .disableStartupItem, .disableBrowserExtension,
                 .quitProcess, .uninstallApp, .moveFile:
                o.refused += 1
            }
        }
        return o
    }

    private func tally(_ o: inout Outcome, _ r: (String, Int64)) {
        switch r.0 {
        case "done": o.done += 1; o.freedBytes += r.1
        case "skipped": o.skipped += 1
        case "refused": o.refused += 1
        default: o.failed += 1
        }
    }

    private func log(_ f: Finding, phase: String, reason: String?, id: String = DoctorJournal.newId()) {
        try? journal.append(JournalRecord(id: id, checkup: checkup, phase: phase, action: f.action, tier: f.action.tier.rawValue,
                                          planItem: f.id, from: f.path, reason: reason))
    }

    static func state(_ path: String, hash: Bool = true) -> FileState? {
        guard let st = DoctorFS.lstat(path) else { return nil }
        return FileState(size: Int64(st.st_size), blocks: Int64(st.st_blocks), mtime: DoctorFS.mtime(st), birth: DoctorFS.birth(st),
                         mode: st.st_mode & 0o7777, sha256: hash ? DoctorFS.sha256(file: path) : nil)
    }

    /// Copy compressed → verify → atomic rename. The original is intact until the rename.
    func compress(_ f: Finding) -> (String, Int64) {
        let path = f.path
        if let why = guardian.refusal(path) { log(f, phase: "refused", reason: why); return ("refused", 0) }
        guard f.action.tier == .automatic, let before = Self.state(path) else { log(f, phase: "skipped", reason: "missing"); return ("skipped", 0) }
        if let fp = f.fingerprint, fp.size != before.size || abs(fp.mtime - before.mtime) > 0.001 {
            log(f, phase: "skipped", reason: "changed since scan"); return ("skipped", 0)
        }
        if Self.isOpen(path) { log(f, phase: "skipped", reason: "open in another process"); return ("skipped", 0) }
        let id = DoctorJournal.newId()
        let temp = Self.tempSibling(path, id)
        var rec = JournalRecord(id: id, checkup: checkup, phase: "intent", action: .compress, tier: 1, planItem: f.id,
                                from: path, to: path, temp: temp, before: before, reason: f.reason)
        do { try journal.append(rec) } catch { return ("failed", 0) }

        let fail: (String) -> (String, Int64) = { why in
            unlink(temp)
            rec.phase = "failed"; rec.reason = why; rec.ts = DoctorFS.iso()
            try? self.journal.append(rec)
            return ("failed", 0)
        }
        let r = DoctorScanner.run("/usr/bin/ditto", ["--hfsCompression", path, temp])
        guard r.status == 0 else { return fail("ditto: \(r.text)") }
        // ditto sets birthtime := mtime; restore both, plus mode.
        guard Self.setTimes(temp, mtime: before.mtime, birth: before.birth), chmod(temp, mode_t(before.mode)) == 0 else { return fail("restore metadata") }
        guard let after = Self.state(temp), after.sha256 == before.sha256, after.size == before.size else { return fail("verify: bytes differ") }
        guard let st = DoctorFS.lstat(temp), st.st_flags & UInt32(UF_COMPRESSED) != 0, after.blocks < before.blocks else {
            return fail("did not shrink")
        }
        // Re-check right before the swap: the original must not have changed meanwhile.
        guard let now = Self.state(path, hash: false), now.size == before.size, now.mtime == before.mtime else { return fail("changed during compression") }
        guard rename(temp, path) == 0 else { return fail("rename errno \(errno)") }
        let freed = (before.blocks - after.blocks) * 512
        rec.phase = "done"; rec.after = after; rec.freedBytes = freed; rec.ts = DoctorFS.iso(); rec.temp = nil
        try? journal.append(rec)
        return ("done", freed)
    }

    // MARK: Quarantine a file ClamAV flagged

    /// `~/Library/Application Support/AIStack/Doctor/Quarantine/<id>/<name>`: same volume as the home, so the move is one
    /// atomic rename (the file is always at exactly one of the two paths). Then the execute bits are removed.
    func quarantineDir(_ id: String) -> String { home + "/Library/Application Support/AIStack/Doctor/Quarantine/" + id }

    func quarantine(_ f: Finding) -> (String, Int64) {
        let path = f.path
        if let why = guardian.refusal(path) { log(f, phase: "refused", reason: why); return ("refused", 0) }
        guard let before = Self.state(path), DoctorFS.lstat(path).map({ $0.st_mode & S_IFMT == S_IFREG }) == true else {
            log(f, phase: "skipped", reason: "missing or not a file"); return ("skipped", 0)
        }
        if let fp = f.fingerprint, fp.size != before.size || (fp.sha256 != nil && fp.sha256 != before.sha256) {
            log(f, phase: "skipped", reason: "changed since scan"); return ("skipped", 0)
        }
        if Self.isOpen(path) { log(f, phase: "skipped", reason: "open in another process"); return ("skipped", 0) }
        let id = DoctorJournal.newId()
        let dir = quarantineDir(id), dest = dir + "/" + (path as NSString).lastPathComponent
        var rec = JournalRecord(id: id, checkup: checkup, phase: "intent", action: .quarantine, tier: 1, planItem: f.id,
                                from: path, to: dest, temp: dest, before: before, reason: f.reason)
        do {
            try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try journal.append(rec)
        } catch { return ("failed", 0) }
        crashPoint("quarantine-intent")
        guard rename(path, dest) == 0 else {
            rec.phase = "failed"; rec.reason = "rename errno \(errno)"; rec.ts = DoctorFS.iso(); try? journal.append(rec)
            return ("failed", 0)
        }
        crashPoint("quarantine-moved")
        chmod(dest, mode_t(before.mode & ~0o111))
        rec.phase = "done"; rec.after = Self.state(dest, hash: false); rec.freedBytes = 0; rec.temp = nil; rec.ts = DoctorFS.iso()
        try? journal.append(rec)
        return ("done", 0)
    }

    private func unquarantine(_ r: JournalRecord) -> Bool {
        guard let path = r.from, let dest = r.to, let before = r.before else { return false }
        if let why = guardian.refusal(path, forWrite: false), why != "missing" { appendUndo(r, "undo-failed", why); return false }
        guard DoctorFS.lstat(path) == nil else { appendUndo(r, "undo-failed", "something else is at the original path now"); return false }
        guard Self.state(dest)?.sha256 == before.sha256 else { appendUndo(r, "undo-failed", "quarantined copy missing or changed"); return false }
        appendUndo(r, "undo-intent", nil, temp: dest)
        try? FileManager.default.createDirectory(atPath: (path as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
        guard rename(dest, path) == 0 else { appendUndo(r, "undo-failed", "rename errno \(errno)"); return false }
        crashPoint("unquarantine-moved")
        chmod(path, mode_t(before.mode))
        rmdir((dest as NSString).deletingLastPathComponent)
        appendUndo(r, "undo-done", nil)
        return true
    }

    /// Rename is atomic, so the file is at `from` or at `to`. An interrupted quarantine is rolled back (the file goes
    /// back with its mode); an interrupted restore is finished.
    private func recoverQuarantine(_ r: JournalRecord) {
        guard let path = r.from, let dest = r.to, let before = r.before else { return }
        var e = r
        e.ts = DoctorFS.iso(); e.temp = nil
        if r.phase == "intent" {
            if DoctorFS.lstat(path) == nil, DoctorFS.lstat(dest) != nil { rename(dest, path) }
            chmod(path, mode_t(before.mode))
            rmdir((dest as NSString).deletingLastPathComponent)
            e.phase = "failed"; e.reason = "interrupted; file put back"
        } else {
            if DoctorFS.lstat(path) != nil, DoctorFS.lstat(dest) == nil {
                chmod(path, mode_t(before.mode))
                rmdir((dest as NSString).deletingLastPathComponent)
                e.phase = "undo-done"; e.reason = "interrupted; restore finished"
            } else {
                e.phase = "undo-failed"; e.reason = "interrupted; still in quarantine"
            }
        }
        try? journal.append(e)
    }

    // MARK: Archive an opted-in folder

    /// zip into a temp sibling → extract it again and compare every file's SHA-256 and the entry list →
    /// rename to `<folder>.zip` → rename the folder aside → remove it. Until the zip is verified and in place the
    /// folder is untouched; after that every byte is in the zip, and the manifest in the journal restores times and modes.
    func archiveFolder(_ f: Finding) -> (String, Int64) {
        let path = (f.path as NSString).standardizingPath
        if let why = guardian.refusal(path) { log(f, phase: "refused", reason: why); return ("refused", 0) }
        guard DoctorFS.isDir(path) else { log(f, phase: "skipped", reason: "not a folder"); return ("skipped", 0) }
        let zip = path + ".zip"
        if DoctorFS.lstat(zip) != nil { log(f, phase: "skipped", reason: "\((zip as NSString).lastPathComponent) already exists"); return ("skipped", 0) }
        guard let manifest = Self.manifest(path) else { log(f, phase: "skipped", reason: "contains links, special or unreadable files"); return ("skipped", 0) }
        if let open = manifest.first(where: { !$0.dir && Self.isOpen(path + "/" + $0.rel) }) {
            log(f, phase: "skipped", reason: "\(open.rel) is open in another process"); return ("skipped", 0)
        }
        let id = DoctorJournal.newId()
        let tag = (path as NSString).deletingLastPathComponent + "/." + (path as NSString).lastPathComponent + ".ld-" + id.prefix(8)
        let temp = tag + ".zip", check = tag + "-v", aside = tag + "-d"
        var rec = JournalRecord(id: id, checkup: checkup, phase: "intent", action: .archiveFolder, tier: 1, planItem: f.id,
                                from: path, to: zip, temp: temp, before: manifest.first?.state, reason: f.reason, manifest: manifest)
        do { try journal.append(rec) } catch { return ("failed", 0) }
        let fail: (String) -> (String, Int64) = { why in
            unlink(temp); try? FileManager.default.removeItem(atPath: check)
            rec.phase = "failed"; rec.reason = why; rec.ts = DoctorFS.iso(); rec.manifest = nil
            try? self.journal.append(rec)
            return ("failed", 0)
        }
        let z = DoctorScanner.run("/usr/bin/ditto", ["-c", "-k", "--sequesterRsrc", path, temp])
        guard z.status == 0 else { return fail("ditto -c: \(z.text)") }
        crashPoint("archive-zipped")
        let x = DoctorScanner.run("/usr/bin/ditto", ["-x", "-k", temp, check])
        guard x.status == 0 else { return fail("ditto -x: \(x.text)") }
        guard let back = Self.manifest(check), Self.sameContents(manifest, back) else { return fail("verify: extracted zip differs") }
        try? FileManager.default.removeItem(atPath: check)
        guard let zstate = Self.state(temp) else { return fail("zip vanished") }
        // The folder must not have changed while we zipped it.
        guard let again = Self.manifest(path, hash: false), Self.sameShape(manifest, again) else { return fail("changed during archiving") }
        var progress = rec
        progress.phase = "verified"; progress.after = zstate; progress.manifest = nil; progress.ts = DoctorFS.iso()
        do { try journal.append(progress) } catch { return fail("journal") }
        crashPoint("archive-verified")
        guard DoctorFS.lstat(zip) == nil, rename(temp, zip) == 0 else { return fail("rename zip errno \(errno)") }
        crashPoint("archive-zip-in-place")
        guard rename(path, aside) == 0 else {
            unlink(zip); return fail("rename folder errno \(errno)")
        }
        crashPoint("archive-aside")
        try? FileManager.default.removeItem(atPath: aside)
        crashPoint("archive-removed")
        let freed = max(0, manifest.reduce(Int64(0)) { $0 + $1.state.blocks } - zstate.blocks) * 512
        rec.phase = "done"; rec.after = zstate; rec.temp = nil; rec.manifest = manifest; rec.freedBytes = freed; rec.ts = DoctorFS.iso()
        try? journal.append(rec)
        return ("done", freed)
    }

    /// Extract next to the zip, verify against the manifest, restore modes and times, rename into place, remove the zip.
    private func unarchive(_ r: JournalRecord) -> Bool {
        guard let path = r.from, let zip = r.to, let manifest = r.manifest else { return false }
        guard DoctorFS.lstat(path) == nil, DoctorFS.lstat(zip) != nil else { appendUndo(r, "undo-failed", "folder exists or zip missing"); return false }
        if let why = guardian.refusal(zip, forWrite: false) { appendUndo(r, "undo-failed", why); return false }
        let out = (path as NSString).deletingLastPathComponent + "/." + (path as NSString).lastPathComponent + ".ld-" + r.id.prefix(8) + "-u"
        try? FileManager.default.removeItem(atPath: out)
        appendUndo(r, "undo-intent", nil, temp: out)
        let fail: (String) -> Bool = { why in
            try? FileManager.default.removeItem(atPath: out); self.appendUndo(r, "undo-failed", why); return false
        }
        let x = DoctorScanner.run("/usr/bin/ditto", ["-x", "-k", zip, out])
        guard x.status == 0 else { return fail("ditto -x: \(x.text)") }
        crashPoint("unarchive-extracted")
        guard let back = Self.manifest(out), Self.sameContents(manifest, back) else { return fail("verify: zip no longer matches") }
        // Deepest first so restoring a folder's times isn't undone by touching its children.
        for e in manifest.sorted(by: { $0.rel.count > $1.rel.count }) {
            let p = e.rel.isEmpty ? out : out + "/" + e.rel
            guard chmod(p, mode_t(e.state.mode)) == 0, Self.setTimes(p, mtime: e.state.mtime, birth: e.state.birth) else { return fail("restore metadata") }
        }
        guard rename(out, path) == 0 else { return fail("rename errno \(errno)") }
        crashPoint("unarchive-in-place")
        unlink(zip)
        appendUndo(r, "undo-done", nil)
        return true
    }

    /// Every entry under `root` (root itself as ""), or nil if it holds anything but plain files and folders.
    static func manifest(_ root: String, hash: Bool = true) -> [ManifestEntry]? {
        var out: [ManifestEntry] = []
        func walk(_ p: String, _ rel: String) -> Bool {
            guard let st = DoctorFS.lstat(p) else { return false }
            let kind = st.st_mode & S_IFMT
            if kind == S_IFREG {
                guard st.st_nlink == 1, let s = state(p, hash: hash), !hash || s.sha256 != nil else { return false }
                out.append(ManifestEntry(rel: rel, dir: false, state: s)); return true
            }
            guard kind == S_IFDIR, let s = state(p, hash: false),
                  let names = try? FileManager.default.contentsOfDirectory(atPath: p) else { return false }
            out.append(ManifestEntry(rel: rel, dir: true, state: s))
            for n in names.sorted() where !walk(p + "/" + n, rel.isEmpty ? n : rel + "/" + n) { return false }
            return true
        }
        return walk(root, "") ? out : nil
    }

    /// Same entry list, kinds and file bytes (times are restored from the manifest, so they are not compared).
    static func sameContents(_ a: [ManifestEntry], _ b: [ManifestEntry]) -> Bool {
        let bm = Dictionary(b.map { ($0.rel, $0) }, uniquingKeysWith: { x, _ in x })
        return a.count == b.count && a.allSatisfy { e in
            guard let o = bm[e.rel], o.dir == e.dir else { return false }
            return e.dir || (o.state.sha256 == e.state.sha256 && o.state.size == e.state.size)
        }
    }

    /// Same entry list, sizes and file mtimes: a cheap "did anything change" check.
    static func sameShape(_ a: [ManifestEntry], _ b: [ManifestEntry]) -> Bool {
        let bm = Dictionary(b.map { ($0.rel, $0) }, uniquingKeysWith: { x, _ in x })
        return a.count == b.count && a.allSatisfy { e in
            guard let o = bm[e.rel], o.dir == e.dir else { return false }
            return e.dir || (o.state.size == e.state.size && o.state.mtime == e.state.mtime)
        }
    }

    // MARK: Undo

    /// Undoes every `done` record in the journal (optionally one entry), newest first.
    func undo(records: [JournalRecord], entry: String?) -> Outcome {
        var o = Outcome()
        let undone = Set(records.filter { $0.phase == "undo-done" }.map(\.id))
        for r in records.reversed() where r.phase == "done" && !undone.contains(r.id) && (entry == nil || r.id == entry) {
            switch r.action {
            case .compress: decompress(r) ? (o.done += 1) : (o.failed += 1)
            case .moveToTrash: restoreFromTrash(r) ? (o.done += 1) : (o.failed += 1)
            case .archiveFolder: unarchive(r) ? (o.done += 1) : (o.failed += 1)
            case .quarantine: unquarantine(r) ? (o.done += 1) : (o.failed += 1)
            case .report, .evictCloud, .emptyTrash, .disableStartupItem,
                 .disableBrowserExtension, .quitProcess, .uninstallApp, .moveFile: o.skipped += 1
            }
        }
        return o
    }

    /// Streams the bytes into a fresh file (no clonefile, which would keep the compression), then renames.
    private func decompress(_ r: JournalRecord) -> Bool {
        guard let path = r.from, let before = r.before else { return false }
        if let why = guardian.refusal(path, forWrite: false) { appendUndo(r, "undo-failed", why); return false }
        let temp = Self.tempSibling(path, r.id + "-u")
        appendUndo(r, "undo-intent", nil, temp: temp)
        guard FileManager.default.createFile(atPath: temp, contents: nil, attributes: [.posixPermissions: Int(before.mode)]),
              let src = FileHandle(forReadingAtPath: path), let dst = FileHandle(forWritingAtPath: temp) else {
            unlink(temp); appendUndo(r, "undo-failed", "open"); return false
        }
        while let chunk = try? src.read(upToCount: 1 << 20), !chunk.isEmpty { try? dst.write(contentsOf: chunk) }
        try? dst.synchronize(); try? src.close(); try? dst.close()
        guard Self.setTimes(temp, mtime: before.mtime, birth: before.birth),
              let s = Self.state(temp), s.sha256 == before.sha256,
              (DoctorFS.lstat(temp)?.st_flags ?? 1) & UInt32(UF_COMPRESSED) == 0,
              rename(temp, path) == 0 else {
            unlink(temp); appendUndo(r, "undo-failed", "verify"); return false
        }
        appendUndo(r, "undo-done", nil)
        return true
    }

    private func restoreFromTrash(_ r: JournalRecord) -> Bool {
        guard let from = r.from, let to = r.to, FileManager.default.fileExists(atPath: to),
              !FileManager.default.fileExists(atPath: from) else { appendUndo(r, "undo-failed", "not in Trash any more"); return false }
        appendUndo(r, "undo-intent", nil)
        try? FileManager.default.createDirectory(atPath: (from as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
        guard rename(to, from) == 0 else { appendUndo(r, "undo-failed", "rename errno \(errno)"); return false }
        appendUndo(r, "undo-done", nil)
        return true
    }

    private func appendUndo(_ r: JournalRecord, _ phase: String, _ reason: String?, temp: String? = nil) {
        var u = r
        u.phase = phase; u.reason = reason; u.ts = DoctorFS.iso(); u.temp = temp
        try? journal.append(u)
    }

    // MARK: Crash recovery

    /// An `intent` without a matching end record: remove the temp sibling (the original is intact until the rename).
    func recover(records: [JournalRecord]) -> Int {
        let ended = Set(records.filter { ["done", "failed", "skipped", "refused", "undo-done", "undo-failed"].contains($0.phase) }.map { "\($0.id)|\($0.phase.hasPrefix("undo") ? "u" : "")" })
        var n = 0
        for r in records where r.phase == "intent" || r.phase == "undo-intent" {
            let key = "\(r.id)|\(r.phase == "undo-intent" ? "u" : "")"
            guard !ended.contains(key), let temp = r.temp else { continue }
            if r.action == .archiveFolder { recoverArchive(r, records); n += 1; continue }
            if r.action == .quarantine { recoverQuarantine(r); n += 1; continue }
            if let orig = r.from, FileManager.default.fileExists(atPath: orig) {
                unlink(temp)
            } else if let orig = r.from, FileManager.default.fileExists(atPath: temp) {
                rename(temp, orig)   // never expected (rename is atomic), but never leave the file missing
            }
            var e = r
            e.phase = r.phase == "intent" ? "failed" : "undo-failed"; e.reason = "interrupted; recovered"; e.ts = DoctorFS.iso()
            try? journal.append(e)
            n += 1
        }
        return n
    }

    /// An interrupted archive or un-archive. Every state on the way keeps the folder or a verified zip (or both).
    private func recoverArchive(_ r: JournalRecord, _ records: [JournalRecord]) {
        guard let path = r.from, let zip = r.to, let temp = r.temp else { return }
        let fm = FileManager.default
        let tag = (path as NSString).deletingLastPathComponent + "/." + (path as NSString).lastPathComponent + ".ld-" + r.id.prefix(8)
        var e = r
        e.ts = DoctorFS.iso(); e.temp = nil
        if r.phase == "undo-intent" {
            if DoctorFS.lstat(path) != nil {
                // The folder is only renamed into place after it verified, so the restore is complete.
                unlink(zip); e.phase = "undo-done"; e.reason = "interrupted after restore; finished"
            } else {
                try? fm.removeItem(atPath: temp); e.phase = "undo-failed"; e.reason = "interrupted; recovered"
            }
            try? journal.append(e); return
        }
        unlink(temp); try? fm.removeItem(atPath: tag + "-v")
        let verified = records.last { $0.id == r.id && $0.phase == "verified" }?.after
        if DoctorFS.lstat(path) != nil {
            // Folder intact: drop our zip if it got into place, and leave things as they were.
            if let v = verified, DoctorFS.lstat(zip) != nil, DoctorFS.sha256(file: zip) == v.sha256 { unlink(zip) }
            e.phase = "failed"; e.reason = "interrupted; recovered"; e.manifest = nil
        } else if let v = verified, DoctorFS.lstat(zip) != nil {
            // The verified zip is in place and the folder was moved aside: finish removing it.
            try? fm.removeItem(atPath: tag + "-d")
            e.phase = "done"; e.after = v; e.reason = "interrupted while removing the folder; finished"
            e.freedBytes = max(0, (r.manifest ?? []).reduce(Int64(0)) { $0 + $1.state.blocks } - v.blocks) * 512
        } else {
            // Never expected: put the aside copy back rather than lose anything.
            if DoctorFS.lstat(tag + "-d") != nil { rename(tag + "-d", path) }
            e.phase = "failed"; e.reason = "interrupted; folder restored"; e.manifest = nil
        }
        try? journal.append(e)
    }

    // MARK: Tier 2

    /// Records the user's decision. Rules can only approve ruleable action types.
    func approve(_ items: [Finding], by: String) {
        for f in items where f.action.tier == .plan {
            if by.hasPrefix("rule:") && !f.action.ruleable { continue }
            try? journal.append(JournalRecord(id: DoctorJournal.newId(), checkup: checkup, phase: "approval", action: f.action,
                                              tier: 2, planItem: f.id, from: f.path, fingerprint: f.fingerprint, by: by))
        }
    }

    /// "Decline": the item stays off later plans until its target changes. Nothing on disk is touched.
    func decline(_ items: [Finding], by: String) {
        for f in items where f.action.tier == .plan {
            try? journal.append(JournalRecord(id: DoctorJournal.newId(), checkup: checkup, phase: "decline", action: f.action,
                                              tier: 2, planItem: f.id, from: f.path, fingerprint: f.fingerprint, by: by))
        }
    }

    func execute(_ plan: TreatmentPlan, approvals: [JournalRecord]) -> Outcome {
        var o = Outcome()
        let approved = Dictionary(approvals.filter { $0.phase == "approval" }.compactMap { r in r.planItem.map { ($0, r) } },
                                  uniquingKeysWith: { a, _ in a })
        for item in plan.groups.flatMap(\.items) {
            if let why = guardian.refusal(item.path) { log(item, phase: "refused", reason: why); o.refused += 1; continue }
            guard let a = approved[item.id], a.from == item.path, a.action == item.action else {
                log(item, phase: "refused", reason: "no recorded approval"); o.refused += 1; continue
            }
            let now = DoctorFS.fingerprint(item.path)
            guard let fp = a.fingerprint, let now, fp.size == now.size, abs(fp.mtime - now.mtime) < 0.001, fp.sha256 == now.sha256 else {
                log(item, phase: "skipped", reason: "changed since the plan was made"); o.skipped += 1; continue
            }
            switch item.action {
            case .moveToTrash: tally(&o, moveToTrash(item))
            case .emptyTrash, .disableStartupItem, .disableBrowserExtension, .quitProcess, .uninstallApp, .moveFile:
                log(item, phase: "skipped", reason: "not implemented yet"); o.skipped += 1
            case .report, .compress, .evictCloud, .archiveFolder, .quarantine:
                log(item, phase: "refused", reason: "not a plan action"); o.refused += 1
            }
        }
        return o
    }

    /// "Delete" = move to the Trash. In test mode the Trash is `<root>/.Trash`.
    private func moveToTrash(_ f: Finding) -> (String, Int64) {
        let id = DoctorJournal.newId()
        let trash = home + "/.Trash"
        var dest = trash + "/" + (f.path as NSString).lastPathComponent
        var n = 2
        while FileManager.default.fileExists(atPath: dest) { dest = trash + "/\((f.path as NSString).lastPathComponent) \(n)"; n += 1 }
        var rec = JournalRecord(id: id, checkup: checkup, phase: "intent", action: .moveToTrash, tier: 2, planItem: f.id,
                                from: f.path, to: dest, fingerprint: f.fingerprint, reason: f.reason)
        do { try journal.append(rec) } catch { return ("failed", 0) }
        var ok = false
        if testRoot {
            try? FileManager.default.createDirectory(atPath: trash, withIntermediateDirectories: true)
            ok = rename(f.path, dest) == 0
        } else {
            var result: NSURL?
            ok = (try? FileManager.default.trashItem(at: URL(fileURLWithPath: f.path), resultingItemURL: &result)) != nil
            if let p = result?.path { rec.to = p }
        }
        rec.phase = ok ? "done" : "failed"; rec.ts = DoctorFS.iso()
        try? journal.append(rec)
        return (ok ? "done" : "failed", 0)
    }

    // MARK: Helpers

    /// Test hook: with --root only, LD_CRASH_AT=<point> kills the process there, like a power cut.
    private func crashPoint(_ name: String) {
        guard testRoot, ProcessInfo.processInfo.environment["LD_CRASH_AT"] == name else { return }
        kill(getpid(), SIGKILL)
    }

    static func tempSibling(_ path: String, _ id: String) -> String {
        (path as NSString).deletingLastPathComponent + "/." + (path as NSString).lastPathComponent + ".ld-" + id.prefix(8)
    }

    static func setTimes(_ path: String, mtime: Double, birth: Double) -> Bool {
        let url = URL(fileURLWithPath: path) as NSURL
        do {
            try url.setResourceValues([.contentModificationDateKey: Date(timeIntervalSince1970: mtime),
                                       .creationDateKey: Date(timeIntervalSince1970: birth)])
            return true
        } catch { return false }
    }

    /// Is any process holding this file open? (proc_listpidspath, no lsof shell-out)
    static func isOpen(_ path: String) -> Bool {
        var pids = [Int32](repeating: 0, count: 4096)
        let n = proc_listpidspath(UInt32(PROC_ALL_PIDS), 0, path, 0, &pids, Int32(pids.count * MemoryLayout<Int32>.size))
        return n > 0
    }
}
