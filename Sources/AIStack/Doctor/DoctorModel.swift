import CryptoKit
import Foundation

/// Laptop Doctor tiers. The tier is a property of the action type, never decided at run time.
enum DoctorTier: Int, Codable, Comparable {
    case observe = 0, automatic = 1, plan = 2
    static func < (a: DoctorTier, b: DoctorTier) -> Bool { a.rawValue < b.rawValue }
}

/// Every action the Executor can perform. No `default:` branches: a new case must be given a tier.
enum DoctorAction: String, Codable, CaseIterable {
    case report
    case compress, evictCloud, archiveFolder, quarantine
    case moveToTrash, emptyTrash, disableStartupItem, disableBrowserExtension, quitProcess, uninstallApp, moveFile

    var tier: DoctorTier {
        switch self {
        case .report: return .observe
        case .compress, .evictCloud, .archiveFolder, .quarantine: return .automatic
        case .moveToTrash, .emptyTrash, .disableStartupItem, .disableBrowserExtension,
             .quitProcess, .uninstallApp, .moveFile: return .plan
        }
    }

    /// Only reversible Tier 2 kinds may be auto-approved by an "always do this" rule.
    var ruleable: Bool {
        switch self {
        case .moveToTrash, .disableStartupItem, .disableBrowserExtension: return true
        case .report, .compress, .evictCloud, .archiveFolder, .quarantine,
             .emptyTrash, .quitProcess, .uninstallApp, .moveFile: return false
        }
    }
}

/// What a target looked like when it was planned; execution re-checks it.
struct Fingerprint: Codable, Equatable {
    var size: Int64
    var mtime: Double
    var sha256: String?
}

struct Finding: Codable {
    var id: String            // planItemId = hash(category, path)
    var category: String
    var action: DoctorAction
    var tier: DoctorTier
    var path: String
    var bytes: Int64
    var reason: String
    var flag: String? = nil
    /// Busy processes: the process name; startup items: the launchd label / Run value name.
    var processName: String? = nil
    /// The program a startup item or process runs.
    var executable: String? = nil
    var pid: Int32? = nil
    var fingerprint: Fingerprint? = nil

    static func itemId(_ category: String, _ path: String) -> String {
        "p-" + DoctorFS.sha256(Data("\(category)\u{0}\(path)".utf8)).prefix(16)
    }
}

struct DoctorReport: Codable {
    var version = 1
    var generatedAt: String
    var root: String
    var dryRun: Bool
    var findings: [Finding]
    var skipped: [String]
    var posture: [String: String]
}

struct PlanGroup: Codable {
    var category: String
    var action: DoctorAction
    var label: String
    var totalBytes: Int64
    var items: [Finding]
}

struct TreatmentPlan: Codable {
    var version = 1
    var checkup: String
    var root: String
    var groups: [PlanGroup]
}

enum DoctorFS {
    static func sha256(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    static func sha256(file path: String) -> String? {
        guard let h = FileHandle(forReadingAtPath: path) else { return nil }
        defer { try? h.close() }
        var hasher = SHA256()
        while let chunk = try? h.read(upToCount: 1 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    static func lstat(_ path: String) -> stat? {
        var st = stat()
        return Darwin.lstat(path, &st) == 0 ? st : nil
    }

    static func isDir(_ path: String) -> Bool {
        guard let st = lstat(path) else { return false }
        return st.st_mode & S_IFMT == S_IFDIR
    }

    static func mtime(_ st: stat) -> Double { Double(st.st_mtimespec.tv_sec) + Double(st.st_mtimespec.tv_nsec) / 1e9 }
    static func birth(_ st: stat) -> Double { Double(st.st_birthtimespec.tv_sec) + Double(st.st_birthtimespec.tv_nsec) / 1e9 }

    /// Every regular file under `path` (or `path` itself), never following symlinks.
    static func files(under path: String) -> [String] {
        guard isDir(path) else { return lstat(path).map { $0.st_mode & S_IFMT == S_IFREG } == true ? [path] : [] }
        var out: [String] = []
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: path) else { return out }
        for n in names.sorted() { out += files(under: (path as NSString).appendingPathComponent(n)) }
        return out
    }

    /// Logical size and newest mtime of a file or tree. A folder's own mtime only changes when entries are
    /// added or removed, so the newest file counts; an empty folder falls back to its own mtime.
    static func treeStats(_ path: String) -> (size: Int64, newest: Double, count: Int) {
        var size: Int64 = 0, newest = 0.0, count = 0
        func walk(_ p: String) {
            guard let st = lstat(p) else { return }
            if st.st_mode & S_IFMT != S_IFDIR { newest = max(newest, mtime(st)) }
            if st.st_mode & S_IFMT == S_IFDIR {
                for n in (try? FileManager.default.contentsOfDirectory(atPath: p)) ?? [] { walk((p as NSString).appendingPathComponent(n)) }
            } else if st.st_mode & S_IFMT == S_IFREG {
                size += Int64(st.st_size); count += 1
            }
        }
        walk(path)
        if count == 0 { newest = lstat(path).map(mtime) ?? 0 }
        return (size, newest, count)
    }

    static func fingerprint(_ path: String) -> Fingerprint? {
        guard let st = lstat(path) else { return nil }
        if st.st_mode & S_IFMT == S_IFDIR {
            let t = treeStats(path)
            return Fingerprint(size: t.size, mtime: t.newest, sha256: nil)
        }
        let hash = st.st_size <= 1 << 30 ? sha256(file: path) : nil
        return Fingerprint(size: Int64(st.st_size), mtime: mtime(st), sha256: hash)
    }

    static func iso(_ d: Date = Date()) -> String {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f.string(from: d)
    }

    static func date(_ iso: String) -> Date? {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f.date(from: iso)
    }
}
