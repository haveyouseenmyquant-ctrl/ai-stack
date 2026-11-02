import Foundation

/// `shared/doctor-catalogue.json`, shared with the Windows helper.
struct DoctorCatalogue: Decodable {
    struct Match: Decodable {
        var kind: String
        var extensions: [String]?
        var olderThanDays: String?
        var name: String?
    }
    struct Category: Decodable {
        var id: String
        var action: DoctorAction
        var tier: Int
        var ruleable: Bool?
        var label: String
        var match: Match
        var paths: [String: [String]]
    }

    var version: Int
    var defaults: [String: Double]
    var categories: [Category]
    var neverPaths: [String: [String]]
    var neverNameSuffixes: [String]
    var incompressibleExtensions: [String]

    enum LoadError: Error, CustomStringConvertible {
        case tierLowered(String), ruleNotAllowed(String), missing(String)
        var description: String {
            switch self {
            case .tierLowered(let id): return "catalogue category \(id) lowers its action's tier"
            case .ruleNotAllowed(let id): return "catalogue category \(id) marks a non-ruleable action ruleable"
            case .missing(let p): return "catalogue not found: \(p)"
            }
        }
    }

    /// The JSON can raise a tier but never lower it, and can't make a lossy action ruleable.
    static func load(_ path: String?) throws -> DoctorCatalogue {
        let candidates = [path,
                          Bundle.main.path(forResource: "doctor-catalogue", ofType: "json"),
                          FileManager.default.currentDirectoryPath + "/shared/doctor-catalogue.json"].compactMap { $0 }
        guard let found = candidates.first(where: { FileManager.default.fileExists(atPath: $0) }) else {
            throw LoadError.missing(candidates.joined(separator: ", "))
        }
        let cat = try JSONDecoder().decode(DoctorCatalogue.self, from: Data(contentsOf: URL(fileURLWithPath: found)))
        for c in cat.categories {
            if c.tier < c.action.tier.rawValue { throw LoadError.tierLowered(c.id) }
            if c.ruleable == true && !c.action.ruleable { throw LoadError.ruleNotAllowed(c.id) }
        }
        return cat
    }

    func value(_ key: String) -> Double { defaults[key] ?? 0 }

    func tier(of c: Category) -> DoctorTier { DoctorTier(rawValue: max(c.tier, c.action.tier.rawValue)) ?? .plan }

    static func expand(_ p: String, home: String) -> String {
        if p == "~" { return home }
        if p.hasPrefix("~/") { return home + String(p.dropFirst(1)) }
        return p
    }
}

/// The never-list. Runs on every path for every tier, before anything else.
/// Hard-coded rules here apply even if the catalogue JSON is edited.
struct DoctorGuard {
    let home: String          // the real home, or --root in test mode
    let catalogue: DoctorCatalogue
    var now = Date()

    private static let hardNever = ["Library/Keychains", "Library/Mail", "Library/Messages", "Library/Safari",
                                    "Library/Application Support/Google/Chrome", "Library/Application Support/Firefox",
                                    "Library/Application Support/AIStack/Doctor", "Library/Mobile Documents/com~apple~CloudDocs/.Trash"]
    private static let hardSuffixes = [".photoslibrary", ".keychain", ".keychain-db"]

    var realHome: String { (home as NSString).resolvingSymlinksInPath }

    /// nil = allowed; otherwise the reason it is refused.
    func refusal(_ path: String, forWrite: Bool = true) -> String? {
        let real = ((path as NSString).standardizingPath as NSString).resolvingSymlinksInPath
        guard real.hasPrefix(realHome + "/") else { return "outside the home folder" }
        let rel = String(real.dropFirst(realHome.count + 1))
        let never = Self.hardNever + (catalogue.neverPaths["macos"] ?? []).map {
            DoctorCatalogue.expand($0, home: realHome).replacingOccurrences(of: realHome + "/", with: "")
        }
        for n in never where rel == n || rel.hasPrefix(n + "/") { return "never-list location (\(n))" }
        // A folder that holds a never-list location (e.g. ~/Library/Application Support holds Chrome's profile).
        for n in never where n.hasPrefix(rel + "/") { return "contains a never-list location (\(n))" }
        for comp in rel.split(separator: "/") {
            let c = comp.lowercased()
            if (Self.hardSuffixes + catalogue.neverNameSuffixes).contains(where: { c.hasSuffix($0) }) {
                return "never-list item (\(comp))"
            }
        }
        if let repo = gitAncestor(real) { return "inside a git repository (\(repo))" }
        guard let st = DoctorFS.lstat(real) else { return "missing" }
        if st.st_uid != getuid() { return "owned by another user" }
        if forWrite {
            let days = catalogue.value("neverModifiedWithinDays")
            let newest = DoctorFS.treeStats(real).newest
            if now.timeIntervalSince1970 - newest < max(days, 7) * 86400 { return "modified in the last \(Int(max(days, 7))) days" }
        }
        return nil
    }

    func gitAncestor(_ real: String) -> String? {
        var dir = real
        while dir.count > realHome.count {
            if FileManager.default.fileExists(atPath: dir + "/.git") { return dir }
            dir = (dir as NSString).deletingLastPathComponent
        }
        return nil
    }
}
