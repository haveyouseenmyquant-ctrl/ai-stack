import Foundation

/// "Always do this" rules, `<checkups dir>/rules.json`. A rule auto-approves one catalogue category in later plans.
/// Only categories the catalogue marks ruleable whose action is reversible (Trash moves, disabling a startup item)
/// can have a rule; that is checked when a rule is added and again every time the file is read.
struct DoctorRule: Codable {
    var id: String
    var category: String
    var created: String
}

struct DoctorRuleBook: Codable {
    var version = 1
    var rules: [DoctorRule] = []

    struct NotRuleable: Error, CustomStringConvertible { var category: String
        var description: String { "\(category) can't become a rule: only reversible Trash moves and disabling startup items can" } }

    static func allowed(_ category: String, _ catalogue: DoctorCatalogue) -> Bool {
        guard let c = catalogue.categories.first(where: { $0.id == category }) else { return false }
        return c.ruleable == true && c.action.ruleable && catalogue.tier(of: c) == .plan
    }

    static func load(_ path: String, catalogue: DoctorCatalogue) -> DoctorRuleBook {
        guard let data = FileManager.default.contents(atPath: path),
              var book = try? JSONDecoder().decode(DoctorRuleBook.self, from: data) else { return DoctorRuleBook() }
        book.rules = book.rules.filter { allowed($0.category, catalogue) }   // a hand-edited file can't widen the rules
        return book
    }

    mutating func add(_ category: String, catalogue: DoctorCatalogue) throws -> DoctorRule {
        guard Self.allowed(category, catalogue) else { throw NotRuleable(category: category) }
        if let r = rules.first(where: { $0.category == category }) { return r }
        let r = DoctorRule(id: "r-" + DoctorJournal.newId().prefix(8), category: category, created: DoctorFS.iso())
        rules.append(r)
        return r
    }

    /// Removing a rule only stops future auto-approvals; what it already did stays in History with its own Undo.
    @discardableResult
    mutating func remove(_ id: String) -> Bool {
        let n = rules.count
        rules.removeAll { $0.id == id }
        return rules.count < n
    }

    func rule(for f: Finding) -> DoctorRule? { rules.first { $0.category == f.category && f.action.ruleable } }
}

/// Undecided plan items, `<checkups dir>/pending.json`. Nothing runs without a decision, so an item the user
/// hasn't approved or declined stays on the next plan (one entry per item) until it ages out after 30 days.
struct DoctorPendingBook: Codable {
    struct Entry: Codable { var firstSeen: String; var item: Finding }
    var version = 1
    var items: [Entry] = []

    static let maxAgeDays = 30.0

    static func load(_ path: String) -> DoctorPendingBook {
        guard let data = FileManager.default.contents(atPath: path) else { return DoctorPendingBook() }
        return (try? JSONDecoder().decode(DoctorPendingBook.self, from: data)) ?? DoctorPendingBook()
    }

    struct Merge { var items: [Finding]; var book: DoctorPendingBook; var carried = 0, expired = 0, suppressed = 0 }

    /// Fresh findings plus still-valid carried items, minus items already decided for the same contents.
    /// A carried item that the fresh scan no longer finds is kept only while its target is unchanged and it is
    /// younger than 30 days. A declined item stays off later plans until its target changes.
    func merge(fresh: [Finding], records: [JournalRecord], now: String) -> Merge {
        var decided: [String: [Fingerprint?]] = [:]
        for r in records where r.phase == "approval" || r.phase == "decline" { if let p = r.planItem { decided[p, default: []].append(r.fingerprint) } }
        func isDecided(_ f: Finding) -> Bool {
            (decided[f.id] ?? []).contains { fp in fp.map { same($0, f.fingerprint) } ?? false }
        }
        let iso = ISO8601DateFormatter()
        iso.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let nowDate = iso.date(from: now) ?? Date()
        let old = Dictionary(items.map { ($0.item.id, $0) }, uniquingKeysWith: { a, _ in a })
        var m = Merge(items: [], book: DoctorPendingBook())
        var seen = Set<String>()
        for f in fresh where f.tier == .plan && seen.insert(f.id).inserted {
            if isDecided(f) { m.suppressed += 1; continue }
            m.items.append(f)
            m.book.items.append(Entry(firstSeen: old[f.id]?.firstSeen ?? now, item: f))
        }
        for e in items where !seen.contains(e.item.id) {
            seen.insert(e.item.id)
            let age = nowDate.timeIntervalSince(iso.date(from: e.firstSeen) ?? nowDate) / 86400
            if age > Self.maxAgeDays { m.expired += 1; continue }
            if isDecided(e.item) { m.suppressed += 1; continue }
            guard let now = DoctorFS.fingerprint(e.item.path), same(now, e.item.fingerprint, hash: false) else { m.expired += 1; continue }
            m.items.append(e.item)
            m.book.items.append(e)
            m.carried += 1
        }
        return m
    }

    private func same(_ a: Fingerprint, _ b: Fingerprint?, hash: Bool = true) -> Bool {
        guard let b else { return false }
        return a.size == b.size && abs(a.mtime - b.mtime) < 0.001 && (!hash || a.sha256 == b.sha256)
    }
}
