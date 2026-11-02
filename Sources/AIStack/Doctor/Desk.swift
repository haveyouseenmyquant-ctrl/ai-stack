import Foundation

/// What the report window does, without any UI, so the safety suite can drive the exact same path through
/// `--doctor decide`, `--doctor history` and `--doctor undo-checkup`.
///
/// A decision is always about items of one checkup's plan:
/// - approve: an approval record per item (by the user), then those items, and only those, run through execute()
///   (never-list, approval match, unchanged re-check).
/// - decline: a decline record per item; nothing on disk is touched.
/// - always: first a rule for every category involved (refused as a whole if any category isn't ruleable), then approve.
/// Decided items leave pending.json, so they don't carry over.
enum DoctorDesk {
    enum Decision: String { case approve, decline, always }

    struct DecideResult: Codable {
        var decided = 0
        var rules: [String] = []
        var outcome: DoctorExecutor.Outcome? = nil
    }

    static func decide(_ decision: Decision, ids: Set<String>?, plan: TreatmentPlan, home: String, catalogue: DoctorCatalogue,
                       journal: DoctorJournal, dir: String, testRoot: Bool) throws -> DecideResult {
        let decided = decidedIds(plan: plan, records: DoctorJournal.read(journal.path))
        let items = plan.groups.flatMap(\.items).filter { (ids?.contains($0.id) ?? true) && !decided.contains($0.id) }
        var res = DecideResult(decided: items.count)
        guard !items.isEmpty else { return res }
        let ex = DoctorExecutor(home: home, catalogue: catalogue, journal: journal, checkup: plan.checkup, testRoot: testRoot)
        if decision == .always {
            var book = DoctorRuleBook.load(dir + "/rules.json", catalogue: catalogue)
            let cats = Array(Set(items.map(\.category))).sorted()
            if let bad = cats.first(where: { !DoctorRuleBook.allowed($0, catalogue) }) { throw DoctorRuleBook.NotRuleable(category: bad) }
            for c in cats { res.rules.append(try book.add(c, catalogue: catalogue).id) }
            try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
            try DoctorCLI.emit(book, dir + "/rules.json")
        }
        if decision == .decline {
            ex.decline(items, by: "user")
        } else {
            ex.approve(items, by: "user")
            // Newest approval per item first: only the decision just made counts.
            let approvals = DoctorJournal.read(journal.path).reversed().filter { $0.checkup == plan.checkup }
            let sub = TreatmentPlan(checkup: plan.checkup, root: plan.root,
                                    groups: plan.groups.map { var g = $0; g.items = g.items.filter { f in items.contains { $0.id == f.id } }; return g }
                                        .filter { !$0.items.isEmpty })
            res.outcome = ex.execute(sub, approvals: Array(approvals))
        }
        var pending = DoctorPendingBook.load(dir + "/pending.json")
        pending.items.removeAll { e in items.contains { $0.id == e.item.id } }
        try DoctorCLI.emit(pending, dir + "/pending.json")
        return res
    }

    /// Plan items that already have an approval or a decline for the same contents.
    static func decidedIds(plan: TreatmentPlan, records: [JournalRecord]) -> Set<String> {
        let ids = Set(plan.groups.flatMap(\.items).map(\.id))
        return Set(records.filter { ($0.phase == "approval" || $0.phase == "decline") && ids.contains($0.planItem ?? "") }.compactMap(\.planItem))
    }

    // MARK: History

    struct HistoryEntry: Codable, Identifiable {
        var id: String
        var checkup: String
        var ts: String
        var action: DoctorAction
        var path: String
        var freedBytes: Int64
        var by: String?
        var undone: Bool
        var canUndo: Bool
        var note: String?
    }

    struct HistoryCheckup: Codable, Identifiable {
        var id: String { checkup }
        var checkup: String
        var entries: [HistoryEntry]
        var freedBytes: Int64 { entries.filter { !$0.undone }.reduce(0) { $0 + $1.freedBytes } }
    }

    /// Every completed action, newest checkup first. Undo works for compression, archives, quarantine and Trash
    /// moves while the item is still in the Trash; an evicted cloud file comes back by opening it.
    static func history(_ records: [JournalRecord]) -> [HistoryCheckup] {
        let undone = Set(records.filter { $0.phase == "undo-done" }.map(\.id))
        var approvedBy: [String: String] = [:]
        for r in records where r.phase == "approval" { if let p = r.planItem { approvedBy[p] = r.by } }
        var byCheckup: [String: [HistoryEntry]] = [:]
        for r in records where r.phase == "done" {
            let isUndone = undone.contains(r.id)
            var can = !isUndone
            var note: String? = nil
            switch r.action {
            case .compress, .archiveFolder, .quarantine: break
            case .moveToTrash:
                if !isUndone && !(r.to.map { FileManager.default.fileExists(atPath: $0) } ?? false) { can = false; note = "no longer in the Trash" }
            case .evictCloud: can = false; note = "opening the file downloads it again"
            case .report, .emptyTrash, .disableStartupItem, .disableBrowserExtension, .quitProcess, .uninstallApp, .moveFile: can = false
            }
            byCheckup[r.checkup, default: []].append(HistoryEntry(
                id: r.id, checkup: r.checkup, ts: r.ts, action: r.action, path: r.from ?? "", freedBytes: r.freedBytes ?? 0,
                by: r.planItem.flatMap { approvedBy[$0] }, undone: isUndone, canUndo: can, note: note))
        }
        return byCheckup.map { HistoryCheckup(checkup: $0.key, entries: $0.value.sorted { $0.ts > $1.ts }) }.sorted { $0.checkup > $1.checkup }
    }

    /// Undo of a whole checkup: every still-undoable entry of it, newest first.
    static func undo(checkup: String, home: String, catalogue: DoctorCatalogue, journal: DoctorJournal, testRoot: Bool) -> DoctorExecutor.Outcome {
        let records = DoctorJournal.read(journal.path)
        let ex = DoctorExecutor(home: home, catalogue: catalogue, journal: journal, checkup: checkup, testRoot: testRoot)
        var o = DoctorExecutor.Outcome()
        for e in history(records).first(where: { $0.checkup == checkup })?.entries ?? [] where e.canUndo {
            let r = ex.undo(records: DoctorJournal.read(journal.path), entry: e.id)
            o.done += r.done; o.failed += r.failed; o.skipped += r.skipped
        }
        return o
    }

    // MARK: Checkups on disk

    /// Checkup folders, newest first.
    static func checkupFolders(_ dir: String) -> [String] {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? []
        return names.filter { FileManager.default.fileExists(atPath: dir + "/" + $0 + "/plan.json") }.sorted(by: >).map { dir + "/" + $0 }
    }

    /// The summary without Claude: plain sentences built from the report and outcome alone.
    static func templateSummary(report: DoctorReport, result: DoctorCheckup.Result?, planItems: Int, planBytes: Int64) -> String {
        func size(_ b: Int64) -> String { ByteCountFormatter.string(fromByteCount: b, countStyle: .file) }
        var s: [String] = []
        if let t = result?.tier1, t.done > 0 {
            s.append("Freed \(size(t.freedBytes)) automatically without deleting anything (\(t.done) item\(t.done == 1 ? "" : "s"); each can be undone in History).")
        } else {
            s.append("Nothing needed doing automatically.")
        }
        let malware = report.findings.filter { $0.flag == "malware" }.count
        if malware > 0 { s.append("\(malware) file\(malware == 1 ? "" : "s") flagged as malware \(malware == 1 ? "was" : "were") quarantined.") }
        let weak = report.posture.filter { ["off", "disabled", "missing"].contains($0.value.lowercased()) }.map(\.key).sorted()
        if !weak.isEmpty { s.append("Security settings that are off: \(weak.joined(separator: ", ")).") }
        let startup = report.findings.filter { $0.category == "persistence" }.count
        if startup > 0 { s.append("\(startup) startup item\(startup == 1 ? "" : "s") worth a look.") }
        s.append(planItems > 0 ? "\(planItems) suggestion\(planItems == 1 ? "" : "s") (\(size(planBytes))) wait for your OK below. Nothing is removed until you approve, and deleted items go to the Trash."
                               : "There is nothing to decide.")
        if !report.skipped.isEmpty { s.append("\(report.skipped.count) area\(report.skipped.count == 1 ? " was" : "s were") not scanned.") }
        return s.joined(separator: " ")
    }
}
