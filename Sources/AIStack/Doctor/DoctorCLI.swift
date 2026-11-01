import Foundation

/// Headless Laptop Doctor, used by the safety suite and CI:
///
///     AIStack --doctor scan    [--root <home>] [--dry-run] [--archive <folder>]… --out report.json
///     AIStack --doctor tier1   --root <home> --journal <file> [--archive <folder>]…
///     AIStack --doctor plan    [--root <home>] [--dry-run] --out plan.json
///     AIStack --doctor approve --plan plan.json --journal <file> [--items id,id | --all] [--by user|rule:<id>]
///     AIStack --doctor execute --root <home> --plan plan.json --journal <file>
///     AIStack --doctor undo    --root <home> --journal <file> [--entry <id>]
///     AIStack --doctor recover --root <home> --journal <file>
///     AIStack --doctor verify-journal <file>…
///
/// Without --root the real home folder is used, and only `scan`, `plan` and `verify-journal` are allowed
/// (with --dry-run implied). Writing commands require --root until the app UI ships.
enum DoctorCLI {
    static func runIfRequested() {
        let args = CommandLine.arguments
        guard let i = args.firstIndex(of: "--doctor"), i + 1 < args.count else { return }
        let verb = args[i + 1]
        let rest = Array(args.dropFirst(i + 2))
        do {
            exit(try run(verb, rest))
        } catch {
            FileHandle.standardError.write(Data("doctor: \(error)\n".utf8))
            exit(2)
        }
    }

    struct Opts {
        var values: [String: [String]] = [:]
        var flags = Set<String>()
        var positional: [String] = []
        init(_ a: [String]) {
            var k = 0
            while k < a.count {
                if a[k].hasPrefix("--") {
                    let key = String(a[k].dropFirst(2))
                    if k + 1 < a.count, !a[k + 1].hasPrefix("--") { values[key, default: []].append(a[k + 1]); k += 2 } else { flags.insert(key); k += 1 }
                } else { positional.append(a[k]); k += 1 }
            }
        }
        func one(_ k: String) -> String? { values[k]?.last }
    }

    struct UsageError: Error, CustomStringConvertible { var description: String }

    static func run(_ verb: String, _ a: [String]) throws -> Int32 {
        let o = Opts(a)
        let root = o.one("root").map { ($0 as NSString).standardizingPath }
        let home = root ?? NSHomeDirectory()
        let catalogue = try DoctorCatalogue.load(o.one("catalogue"))
        let checkup = o.one("checkup") ?? DoctorFS.iso()
        let archive = (o.values["archive"] ?? []).map { $0.hasPrefix("/") ? $0 : home + "/" + $0 }
        let writing = ["tier1", "approve", "execute", "undo", "recover"].contains(verb)
        if writing && root == nil { throw UsageError(description: "\(verb) needs --root <fixture home>; real-home writes are not enabled") }
        if root != nil && !FileManager.default.fileExists(atPath: home) { throw UsageError(description: "no such root \(home)") }

        func scan() -> ([Finding], [String]) {
            var s = DoctorScanner(home: home, catalogue: catalogue, optedInFolders: archive, testRoot: root != nil)
            let f = s.scan()
            return (f, s.skipped)
        }
        func journal() throws -> DoctorJournal {
            guard let j = o.one("journal") else { throw UsageError(description: "--journal <file> required") }
            return try DoctorJournal(path: j)
        }
        func executor(_ j: DoctorJournal) -> DoctorExecutor {
            DoctorExecutor(home: home, catalogue: catalogue, journal: j, checkup: checkup, testRoot: root != nil)
        }
        func loadPlan() throws -> TreatmentPlan {
            guard let p = o.one("plan") else { throw UsageError(description: "--plan <file> required") }
            return try JSONDecoder().decode(TreatmentPlan.self, from: Data(contentsOf: URL(fileURLWithPath: p)))
        }

        switch verb {
        case "scan":
            let (findings, skipped) = scan()
            let report = DoctorReport(generatedAt: DoctorFS.iso(), root: home, dryRun: true, findings: findings, skipped: skipped,
                                      posture: root == nil ? DoctorScanner.posture() : [:])
            try emit(report, o.one("out"))
        case "plan":
            try emit(makePlan(scan().0, catalogue: catalogue, checkup: checkup, root: home), o.one("out"))
        case "tier1":
            let j = try journal()
            let ex = executor(j)
            _ = ex.recover(records: DoctorJournal.read(j.path))
            try emit(ex.runTier1(scan().0), nil)
        case "approve":
            let plan = try loadPlan()
            let ids = Set((o.one("items") ?? "").split(separator: ",").map(String.init))
            let items = plan.groups.flatMap(\.items).filter { o.flags.contains("all") || ids.contains($0.id) }
            executor(try journal()).approve(items, by: o.one("by") ?? "user")
            print("approved \(items.count)")
        case "execute":
            let j = try journal()
            try emit(executor(j).execute(try loadPlan(), approvals: DoctorJournal.read(j.path)), nil)
        case "undo":
            let j = try journal()
            try emit(executor(j).undo(records: DoctorJournal.read(j.path), entry: o.one("entry")), nil)
        case "recover":
            let j = try journal()
            print("recovered \(executor(j).recover(records: DoctorJournal.read(j.path)))")
        case "verify-journal":
            let files = o.positional + (o.values["journal"] ?? [])
            let bad = files.flatMap { DoctorJournal.unapprovedDestructive(DoctorJournal.read($0)) }
            print("{\"unapprovedDestructive\": \(bad.count)}")
            return bad.isEmpty ? 0 : 1
        default:
            throw UsageError(description: "unknown verb \(verb)")
        }
        return 0
    }

    static func makePlan(_ findings: [Finding], catalogue: DoctorCatalogue, checkup: String, root: String) -> TreatmentPlan {
        let t2 = findings.filter { $0.tier == .plan }
        var groups: [PlanGroup] = []
        for c in catalogue.categories {
            let items = t2.filter { $0.category == c.id }
            guard !items.isEmpty else { continue }
            var label = c.label.replacingOccurrences(of: "{count}", with: String(items.count))
            for (k, v) in catalogue.defaults { label = label.replacingOccurrences(of: "{\(k)}", with: String(Int(v))) }
            groups.append(PlanGroup(category: c.id, action: c.action, label: label, totalBytes: items.reduce(0) { $0 + $1.bytes }, items: items))
        }
        return TreatmentPlan(checkup: checkup, root: root, groups: groups)
    }

    static func emit<T: Encodable>(_ v: T, _ out: String?) throws {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        let data = try enc.encode(v)
        if let out { try data.write(to: URL(fileURLWithPath: out)) } else { FileHandle.standardOutput.write(data + Data("\n".utf8)) }
    }
}
