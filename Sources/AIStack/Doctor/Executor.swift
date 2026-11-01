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
            case .archiveFolder, .evictCloud, .quarantine:
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

    // MARK: Undo

    /// Undoes every `done` record in the journal (optionally one entry), newest first.
    func undo(records: [JournalRecord], entry: String?) -> Outcome {
        var o = Outcome()
        let undone = Set(records.filter { $0.phase == "undo-done" }.map(\.id))
        for r in records.reversed() where r.phase == "done" && !undone.contains(r.id) && (entry == nil || r.id == entry) {
            switch r.action {
            case .compress: decompress(r) ? (o.done += 1) : (o.failed += 1)
            case .moveToTrash: restoreFromTrash(r) ? (o.done += 1) : (o.failed += 1)
            case .report, .evictCloud, .archiveFolder, .quarantine, .emptyTrash, .disableStartupItem,
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

    // MARK: Tier 2

    /// Records the user's decision. Rules can only approve ruleable action types.
    func approve(_ items: [Finding], by: String) {
        for f in items where f.action.tier == .plan {
            if by.hasPrefix("rule:") && !f.action.ruleable { continue }
            try? journal.append(JournalRecord(id: DoctorJournal.newId(), checkup: checkup, phase: "approval", action: f.action,
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
