import Foundation

/// Headless Laptop Doctor, used by the safety suite and CI:
///
///     AIStack --doctor scan    [--root <home>] [--dry-run] [--archive <folder>]… --out report.json
///     AIStack --doctor tier1   --root <home> --journal <file> [--archive <folder>]…
///     AIStack --doctor plan    [--root <home>] [--dry-run] --out plan.json
///     AIStack --doctor approve --plan plan.json --journal <file> [--items id,id | --all] [--by user|rule:<id>]
///     AIStack --doctor decline --plan plan.json --journal <file> [--items id,id | --all]
///     AIStack --doctor rule-add --root <home> --dir <checkups dir> --category <id>   ("always do this"; ruleable kinds only)
///     AIStack --doctor execute --root <home> --plan plan.json --journal <file>
///     AIStack --doctor undo    --root <home> --journal <file> [--entry <id>]
///     AIStack --doctor recover --root <home> --journal <file>
///     AIStack --doctor summary-prompt --report report.json   (exactly what the Claude summary would send)
///     AIStack --doctor summary --report report.json [--model haiku]
///     AIStack --doctor checkup --root <home> --journal <file> --dir <out> [--tier1 compress,evictCloud,archiveFolder,quarantine]
///                              [--threshold coldDays=90]… [--archive <folder>]…   (what the scheduler runs)
///     AIStack --doctor due     --now <iso> [--last <iso>] --weekday 1-7 --hour 0-23 [--notified <iso>]   (UTC calendar)
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
        let writing = ["tier1", "checkup", "approve", "decline", "rule-add", "execute", "undo", "recover"].contains(verb)
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
        case "checkup":
            guard let dir = o.one("dir") else { throw UsageError(description: "--dir <folder> required") }
            var opts = DoctorCheckupOptions(archiveFolders: archive)
            if let t = o.one("tier1") { opts.tier1 = Set(t.split(separator: ",").compactMap { DoctorAction(rawValue: String($0)) }) }
            if o.flags.contains("tier1") { opts.tier1 = [] }   // `--tier1` with nothing after it: every switch off
            for kv in o.values["threshold"] ?? [] {
                let p = kv.split(separator: "=")
                if p.count == 2, let v = Int(p[1]) { opts.thresholds[String(p[0])] = v }
            }
            try emit(DoctorCheckup.run(home: home, catalogue: catalogue, options: opts, journal: try journal(), dir: dir,
                                       testRoot: true, checkup: checkup), nil)
        case "due":
            var cal = Calendar(identifier: .gregorian)
            cal.timeZone = TimeZone(identifier: "UTC")!
            let f = ISO8601DateFormatter()
            func date(_ k: String) -> Date? { o.one(k).flatMap { f.date(from: $0) } }
            guard let now = date("now"), let wd = o.one("weekday").flatMap(Int.init), let h = o.one("hour").flatMap(Int.init)
            else { throw UsageError(description: "--now, --weekday and --hour required") }
            let due = DoctorSchedule.isDue(now: now, lastRun: date("last"), weekday: wd, hour: h, calendar: cal)
            let notify = DoctorSchedule.mayNotify(now: now, lastNotified: date("notified"), calendar: cal)
            print("{\"due\": \(due), \"mayNotify\": \(notify)}")
        case "approve":
            let plan = try loadPlan()
            let ids = Set((o.one("items") ?? "").split(separator: ",").map(String.init))
            let items = plan.groups.flatMap(\.items).filter { o.flags.contains("all") || ids.contains($0.id) }
            executor(try journal()).approve(items, by: o.one("by") ?? "user")
            print("approved \(items.count)")
        case "decline":
            let plan = try loadPlan()
            let ids = Set((o.one("items") ?? "").split(separator: ",").map(String.init))
            let items = plan.groups.flatMap(\.items).filter { o.flags.contains("all") || ids.contains($0.id) }
            executor(try journal()).decline(items, by: "user")
            print("declined \(items.count)")
        case "rule-add":
            guard let dir = o.one("dir"), let cat = o.one("category") else { throw UsageError(description: "--dir and --category required") }
            var book = DoctorRuleBook.load(dir + "/rules.json", catalogue: catalogue)
            let r = try book.add(cat, catalogue: catalogue)
            try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
            try emit(book, dir + "/rules.json")
            try emit(r, nil)
        case "execute":
            let j = try journal()
            try emit(executor(j).execute(try loadPlan(), approvals: DoctorJournal.read(j.path)), nil)
        case "undo":
            let j = try journal()
            try emit(executor(j).undo(records: DoctorJournal.read(j.path), entry: o.one("entry")), nil)
        case "recover":
            let j = try journal()
            print("recovered \(executor(j).recover(records: DoctorJournal.read(j.path)))")
        case "summary-prompt", "summary":
            guard let p = o.one("report") else { throw UsageError(description: "--report <file> required") }
            let report = try JSONDecoder().decode(DoctorReport.self, from: Data(contentsOf: URL(fileURLWithPath: p)))
            guard verb == "summary" else { print(try DoctorSummary.prompt(report)); return 0 }
            let model = o.one("model") ?? "haiku"
            let done = DispatchSemaphore(value: 0)
            var result: Result<String, Error> = .failure(UsageError(description: "no reply"))
            Task.detached { do { result = .success(try await DoctorSummary.write(report, model: model)) } catch { result = .failure(error) }; done.signal() }
            done.wait()
            print(try result.get())
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
