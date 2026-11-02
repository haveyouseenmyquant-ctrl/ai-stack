import Foundation

/// Append-only JSONL action journal. Every record is fsynced before the step it describes starts.
struct JournalRecord: Codable {
    var v = 1
    var id: String
    var checkup: String
    var ts: String = DoctorFS.iso()
    var phase: String   // intent|done|failed|skipped|refused|approval|decline|undo-intent|undo-done|undo-failed
    var action: DoctorAction
    var tier: Int
    var planItem: String? = nil
    var from: String? = nil
    var to: String? = nil
    var temp: String? = nil
    var before: FileState? = nil
    var after: FileState? = nil
    var fingerprint: Fingerprint? = nil
    var by: String? = nil
    var reason: String? = nil
    var freedBytes: Int64? = nil
    var manifest: [ManifestEntry]? = nil   // archiveFolder: every entry of the folder, to verify and restore
}

struct ManifestEntry: Codable {
    var rel: String     // "" is the folder itself
    var dir: Bool
    var state: FileState
    var meta: String? = nil     // SHA-256 of the extended attributes and ACL that a zip round-trip must keep
    var flags: UInt32? = nil    // hidden / nodump, which a zip drops: restored on undo
    var qtn: String? = nil      // quarantine attribute, which ditto rewrites on extraction: restored on undo
}

struct FileState: Codable {
    var size: Int64
    var blocks: Int64
    var mtime: Double
    var birth: Double
    var mode: UInt16
    var sha256: String?
}

final class DoctorJournal {
    let path: String
    private let fd: Int32

    init(path: String) throws {
        self.path = path
        try FileManager.default.createDirectory(atPath: (path as NSString).deletingLastPathComponent,
                                                withIntermediateDirectories: true)
        fd = open(path, O_WRONLY | O_APPEND | O_CREAT, 0o600)
        if fd < 0 { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
    }

    deinit { close(fd) }

    func append(_ r: JournalRecord) throws {
        let enc = JSONEncoder()
        enc.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        var line = try enc.encode(r)
        line.append(0x0A)
        let ok = line.withUnsafeBytes { write(fd, $0.baseAddress, $0.count) == $0.count }
        guard ok, fsync(fd) == 0 else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
    }

    static func read(_ path: String) -> [JournalRecord] {
        guard let text = try? String(contentsOfFile: path, encoding: .utf8) else { return [] }
        let dec = JSONDecoder()
        // A torn last line (power loss mid-write) is ignored.
        return text.split(separator: "\n").compactMap { try? dec.decode(JournalRecord.self, from: Data($0.utf8)) }
    }

    /// Tier 2 actions that completed without an earlier approval record for the same plan item.
    static func unapprovedDestructive(_ records: [JournalRecord]) -> [JournalRecord] {
        var approved = Set<String>()
        var bad: [JournalRecord] = []
        for r in records {
            if r.phase == "approval", let p = r.planItem { approved.insert(p) }
            if r.phase == "done", r.tier >= DoctorTier.plan.rawValue || r.action.tier == .plan {
                if r.planItem.map({ !approved.contains($0) }) ?? true { bad.append(r) }
            }
        }
        return bad
    }

    static func newId() -> String { UUID().uuidString.lowercased() }
}
