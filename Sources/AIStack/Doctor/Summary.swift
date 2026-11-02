import Foundation

/// The one optional Claude call per checkup: a plain-English summary that also explains unknown startup items
/// and busy processes. Only `facts(_:)` decides what leaves the machine, and it sends:
/// category totals, security settings, and for startup items and processes their name, signature flag and
/// executable path. Never the user's file or folder names: findings are sent as counts and bytes, malware as a
/// count, skipped notes as a count, and an executable inside the home folder as `~/…/<name>`.
enum DoctorSummary {
    static let systemPrompt = """
        You write the summary of a laptop checkup for a non-technical user. Use plain English and at most \
        120 words, then one short line per startup item or process listed, saying what it most likely is and \
        whether it looks legitimate. Say "unknown" rather than guess. Do not invent findings, sizes or settings.
        """

    struct Facts: Encodable {
        struct Group: Encodable { var category: String; var action: String; var tier: Int; var count: Int; var bytes: Int64 }
        struct Item: Encodable { var name: String; var flag: String?; var executable: String?; var cpuPercent: String? }
        var security: [String: String]
        var findings: [Group]
        var startupItems: [Item]
        var processes: [Item]
        var malwareDetections: Int
        var areasNotScanned: Int
    }

    static func facts(_ r: DoctorReport) -> Facts {
        var groups: [String: Facts.Group] = [:]
        var startup: [Facts.Item] = [], procs: [Facts.Item] = [], malware = 0
        for f in r.findings {
            if f.flag == "malware" { malware += 1; continue }
            if f.category == "persistence" {
                startup.append(.init(name: f.processName ?? "unnamed", flag: f.flag, executable: f.executable.map { redact($0, r.root) }))
                continue
            }
            if let name = f.processName {
                procs.append(.init(name: name, flag: nil, executable: f.executable.map { redact($0, r.root) },
                                   cpuPercent: f.reason.components(separatedBy: "%").first))
                continue
            }
            var g = groups[f.category] ?? .init(category: f.category, action: f.action.rawValue, tier: f.tier.rawValue, count: 0, bytes: 0)
            g.count += 1; g.bytes += f.bytes
            groups[f.category] = g
        }
        return Facts(security: r.posture, findings: groups.values.sorted { $0.bytes > $1.bytes }, startupItems: startup,
                     processes: procs, malwareDetections: malware, areasNotScanned: r.skipped.count)
    }

    /// System and app paths are kept; under the home folder only the executable's own name is.
    static func redact(_ path: String, _ home: String) -> String {
        let h = home.hasSuffix("/") ? String(home.dropLast()) : home
        guard path.hasPrefix("/"), path != h, !path.hasPrefix(h + "/") else { return "~/…/" + (path as NSString).lastPathComponent }
        return path
    }

    static func prompt(_ r: DoctorReport) throws -> String {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        return "Checkup facts (bytes are exact; tier 1 = done automatically and reversibly, tier 2 = waiting for approval):\n"
            + String(decoding: try enc.encode(facts(r)), as: UTF8.self)
    }

    static func write(_ r: DoctorReport, model: String = "haiku") async throws -> String {
        try await ClaudeCLI.shared.complete(try prompt(r), config: ClaudeRequestConfig(model: model, systemPrompt: systemPrompt, effort: "low"),
                                            timeout: 120)
    }
}
