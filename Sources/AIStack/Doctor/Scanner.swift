import Compression
import Foundation

/// Tier 0 only: reads metadata and samples, never writes user files.
struct DoctorScanner {
    let home: String
    let catalogue: DoctorCatalogue
    let guardian: DoctorGuard
    var optedInFolders: [String] = []   // absolute paths
    var testRoot = false                // --root: only report processes that run from inside it
    var now = Date()
    private(set) var skipped: [String] = []

    init(home: String, catalogue: DoctorCatalogue, optedInFolders: [String], testRoot: Bool) {
        self.home = (home as NSString).resolvingSymlinksInPath
        self.catalogue = catalogue
        self.guardian = DoctorGuard(home: self.home, catalogue: catalogue)
        self.optedInFolders = optedInFolders.map { ($0 as NSString).resolvingSymlinksInPath }
        self.testRoot = testRoot
    }

    mutating func scan() -> [Finding] {
        var out: [Finding] = []
        // Tier 1 candidates first, destructive options after.
        for c in catalogue.categories.sorted(by: { catalogue.tier(of: $0) < catalogue.tier(of: $1) }) {
            let roots = (c.paths["macos"] ?? []).map { DoctorCatalogue.expand($0, home: home) }
            var found: [(String, Int64, String, String?)] = []   // path, bytes, reason, flag
            switch c.match.kind {
            case "coldCompressible": found = coldCompressible()
            case "optedInFolders": found = optedInFolders.compactMap { f in
                    guard DoctorFS.isDir(f) else { return nil }
                    return (f, DoctorFS.treeStats(f).size, "you marked this folder for archiving", nil) }
            case "children": found = roots.flatMap { children($0, olderThan: days(c.match.olderThanDays)) }
            case "extensions": found = roots.flatMap { withExtensions($0, c.match.extensions ?? [], olderThan: days(c.match.olderThanDays)) }
            case "duplicates": found = duplicates(roots)
            case "namedDirs": found = roots.flatMap { namedDirs($0, c.match.name ?? "", olderThan: days(c.match.olderThanDays)) }
            case "self": found = roots.compactMap { r in
                    let t = DoctorFS.treeStats(r)
                    return t.count > 0 ? (r, t.size, "\(t.count) items", nil) : nil }
            case "startupItems": found = roots.flatMap(launchAgents)
            case "clamav": found = clamav(roots)
            case "cloudFiles": skipped.append("cloud files: eviction is not implemented on macOS yet")
            case "busyProcesses":
                out += busyProcesses().map {
                    Finding(id: Finding.itemId(c.id, "pid:\($0.pid):\($0.name)"), category: c.id, action: c.action,
                            tier: catalogue.tier(of: c), path: $0.path, bytes: 0,
                            reason: String(format: "%.0f%% CPU", $0.cpu), processName: $0.name, executable: $0.path, pid: $0.pid)
                }
            default: skipped.append("unknown matcher \(c.match.kind) in \(c.id)")
            }
            for (path, bytes, reason, flag) in found {
                if c.action != .report, let why = guardian.refusal(path) {
                    skipped.append("\(c.id): \(rel(path)): \(why)")
                    // A malware hit is still reported, just not acted on.
                    if c.action == .quarantine {
                        out.append(Finding(id: Finding.itemId(c.id, path), category: c.id, action: .report, tier: .observe,
                                           path: path, bytes: bytes, reason: reason + "; not quarantined: " + why, flag: flag))
                    }
                    continue
                }
                var f = Finding(id: Finding.itemId(c.id, path), category: c.id, action: c.action, tier: catalogue.tier(of: c),
                                path: path, bytes: bytes, reason: reason, flag: flag, fingerprint: DoctorFS.fingerprint(path))
                if c.match.kind == "startupItems" { (f.processName, f.executable) = Self.launchAgentJob(path) }
                out.append(f)
            }
        }
        return out
    }

    private func days(_ key: String?) -> Double { key.map { catalogue.value($0) } ?? 0 }
    private func rel(_ p: String) -> String { p.hasPrefix(home + "/") ? "~/" + p.dropFirst(home.count + 1) : p }
    private func age(_ st: stat) -> Double { (now.timeIntervalSince1970 - DoctorFS.mtime(st)) / 86400 }

    private func list(_ dir: String) -> [String] {
        ((try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? []).sorted()
            .filter { $0 != ".DS_Store" && $0 != ".localized" }.map { (dir as NSString).appendingPathComponent($0) }
    }

    // MARK: Tier 1: cold, compressible files

    private func coldCompressible() -> [(String, Int64, String, String?)] {
        let cold = catalogue.value("coldDays"), minBytes = Int64(catalogue.value("minCompressBytes"))
        let gainNeeded = catalogue.value("minCompressionGain")
        let noExt = Set(catalogue.incompressibleExtensions)
        var out: [(String, Int64, String, String?)] = []
        func walk(_ dir: String) {
            for p in list(dir) {
                let name = (p as NSString).lastPathComponent
                guard let st = DoctorFS.lstat(p) else { continue }
                if st.st_mode & S_IFMT == S_IFDIR {
                    // Hidden folders, Library (system/app data), bundles, git trees and opted-in folders are not walked.
                    if name.hasPrefix(".") || p == home + "/Library" || name.contains(".") && ["app", "framework", "bundle", "photoslibrary", "plugin", "kext"].contains((name as NSString).pathExtension.lowercased()) { continue }
                    if FileManager.default.fileExists(atPath: p + "/.git") || optedInFolders.contains(p) { continue }
                    walk(p)
                    continue
                }
                guard st.st_mode & S_IFMT == S_IFREG, Int64(st.st_size) >= minBytes, st.st_nlink == 1,
                      st.st_flags & UInt32(UF_COMPRESSED) == 0, st.st_mode & 0o111 == 0,
                      !noExt.contains("." + (name as NSString).pathExtension.lowercased()),
                      age(st) >= cold, lastUsedAge(p).map({ $0 >= cold }) ?? true else { continue }
                guard let gain = sampleGain(p), gain >= gainNeeded else { continue }
                out.append((p, Int64(st.st_size), String(format: "cold %.0f days, sample shrinks %.0f%%", age(st), gain * 100), nil))
            }
        }
        walk(home)
        return out
    }

    /// Spotlight's "last opened" date, when it has one.
    private func lastUsedAge(_ p: String) -> Double? {
        guard let item = MDItemCreate(nil, p as CFString),
              let d = MDItemCopyAttribute(item, kMDItemLastUsedDate) as? Date else { return nil }
        return now.timeIntervalSince(d) / 86400
    }

    /// LZFSE trial on the first 64 KiB.
    private func sampleGain(_ p: String) -> Double? {
        guard let h = FileHandle(forReadingAtPath: p), let data = try? h.read(upToCount: 64 << 10), !data.isEmpty else { return nil }
        try? h.close()
        let dst = UnsafeMutablePointer<UInt8>.allocate(capacity: data.count)
        defer { dst.deallocate() }
        let n = data.withUnsafeBytes { compression_encode_buffer(dst, data.count, $0.bindMemory(to: UInt8.self).baseAddress!, data.count, nil, COMPRESSION_LZFSE) }
        return n == 0 ? 0 : 1 - Double(n) / Double(data.count)
    }

    // MARK: Tier 2 matchers

    private func children(_ dir: String, olderThan d: Double) -> [(String, Int64, String, String?)] {
        list(dir).compactMap { p in
            let t = DoctorFS.treeStats(p)
            let a = (now.timeIntervalSince1970 - t.newest) / 86400
            return t.size > 0 && a >= d ? (p, t.size, String(format: "%d files, untouched %.0f days", t.count, a), nil) : nil
        }
    }

    private func withExtensions(_ dir: String, _ exts: [String], olderThan d: Double) -> [(String, Int64, String, String?)] {
        list(dir).compactMap { p in
            guard let st = DoctorFS.lstat(p), exts.contains("." + (p as NSString).pathExtension.lowercased()), age(st) >= d else { return nil }
            return (p, DoctorFS.treeStats(p).size, String(format: "downloaded %.0f days ago", age(st)), nil)
        }
    }

    private func duplicates(_ roots: [String]) -> [(String, Int64, String, String?)] {
        let minBytes = Int64(catalogue.value("duplicateMinBytes"))
        var bySize: [Int64: [String]] = [:]
        for r in roots {
            for f in DoctorFS.files(under: r) where !f.split(separator: "/").contains(where: { $0.hasPrefix(".") }) {
                if let st = DoctorFS.lstat(f), Int64(st.st_size) >= minBytes { bySize[Int64(st.st_size), default: []].append(f) }
            }
        }
        var out: [(String, Int64, String, String?)] = []
        for (size, files) in bySize where files.count > 1 {
            var byHash: [String: [String]] = [:]
            for f in Set(files) { if let h = DoctorFS.sha256(file: f) { byHash[h, default: []].append(f) } }
            for (_, same) in byHash where same.count > 1 {
                // Keep the oldest copy; propose the rest.
                let sorted = same.sorted { (DoctorFS.lstat($0).map(DoctorFS.mtime) ?? 0, $0) < (DoctorFS.lstat($1).map(DoctorFS.mtime) ?? 0, $1) }
                for f in sorted.dropFirst() { out.append((f, size, "identical to \(rel(sorted[0]))", nil)) }
            }
        }
        return out
    }

    private func namedDirs(_ root: String, _ name: String, olderThan d: Double) -> [(String, Int64, String, String?)] {
        var out: [(String, Int64, String, String?)] = []
        func walk(_ dir: String) {
            for p in list(dir) where DoctorFS.isDir(p) {
                if (p as NSString).lastPathComponent == name {
                    let project = DoctorFS.treeStats(dir)
                    let a = (now.timeIntervalSince1970 - project.newest) / 86400
                    if a >= d { out.append((p, DoctorFS.treeStats(p).size, String(format: "project untouched %.0f days", a), nil)) }
                } else if !(p as NSString).lastPathComponent.hasPrefix(".") {
                    walk(p)
                }
            }
        }
        walk(root)
        return out
    }

    private func launchAgents(_ dir: String) -> [(String, Int64, String, String?)] {
        list(dir).filter { $0.hasSuffix(".plist") }.compactMap { p in
            guard let dict = NSDictionary(contentsOfFile: p) else { return (p, 0, "unreadable launch agent", "unreadable") }
            let program = (dict["Program"] as? String) ?? (dict["ProgramArguments"] as? [String])?.first
            let label = dict["Label"] as? String ?? (p as NSString).lastPathComponent
            guard let program else { return (p, 0, "\(label) has no program", "orphaned") }
            guard FileManager.default.fileExists(atPath: program) else { return (p, 0, "\(label) points to a missing program", "orphaned") }
            let sig = Self.codeSignature(program)
            guard let sig else { return (p, 0, "\(label) runs an unsigned program", "unsigned") }
            if sig == "adhoc" { return (p, 0, "\(label) runs an ad-hoc-signed program", "adhoc") }
            return nil
        }
    }

    /// A LaunchAgent's label and program, for the report and the summary.
    static func launchAgentJob(_ plist: String) -> (String?, String?) {
        guard let dict = NSDictionary(contentsOfFile: plist) else { return (nil, nil) }
        return (dict["Label"] as? String ?? ((plist as NSString).lastPathComponent as NSString).deletingPathExtension,
                (dict["Program"] as? String) ?? (dict["ProgramArguments"] as? [String])?.first)
    }

    // MARK: Malware: an optional, user-installed ClamAV (never bundled)

    /// The user's own clamscan. With --root, LD_CLAMSCAN=<path>|off overrides it (test hook).
    private func clamscanPath() -> String? {
        if testRoot, let o = ProcessInfo.processInfo.environment["LD_CLAMSCAN"] { return o == "off" ? nil : o }
        return ["/opt/homebrew/bin/clamscan", "/usr/local/bin/clamscan", "/opt/local/bin/clamscan"]
            .first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    private mutating func clamav(_ roots: [String]) -> [(String, Int64, String, String?)] {
        let dirs = roots.filter(DoctorFS.isDir)
        guard !dirs.isEmpty else { return [] }
        guard let tool = clamscanPath() else { skipped.append("malware: ClamAV is not installed, so no file scan (XProtect still protects)"); return [] }
        let r = Self.run(tool, ["-r", "--infected", "--no-summary", "--stdout"] + dirs)
        // 0 = clean, 1 = found, anything else = error (e.g. no signature database).
        guard r.status == 0 || r.status == 1 else {
            skipped.append("malware: clamscan failed (exit \(r.status)): \(r.text.prefix(200))"); return []
        }
        return r.text.split(separator: "\n").compactMap { line in
            guard line.hasSuffix(" FOUND"), let sep = line.range(of: ": ", options: .backwards) else { return nil }
            let path = String(line[..<sep.lowerBound])
            let sig = line[sep.upperBound...].dropLast(6)
            guard let st = DoctorFS.lstat(path), st.st_mode & S_IFMT == S_IFREG else { return nil }
            return (path, Int64(st.st_size), "ClamAV: \(sig)", "malware")
        }
    }

    /// nil = unsigned, "adhoc", or the team identifier.
    static func codeSignature(_ path: String) -> String? {
        let out = run("/usr/bin/codesign", ["-dv", "--verbose=2", path])
        guard out.status == 0 else { return nil }
        if out.text.contains("Signature=adhoc") { return "adhoc" }
        return out.text.split(separator: "\n").first { $0.hasPrefix("TeamIdentifier=") }.map { String($0.dropFirst(15)) } ?? "apple"
    }

    // MARK: Processes

    struct Busy { var pid: Int32; var cpu: Double; var name: String; var path: String }

    private func busyProcesses() -> [Busy] {
        let threshold = catalogue.value("busyCpuPercent")
        func sample() -> [Int32: (Double, String)] {
            var r: [Int32: (Double, String)] = [:]
            for line in Self.run("/bin/ps", ["-U", String(getuid()), "-o", "pid=,pcpu=,args="]).text.split(separator: "\n") {
                let parts = line.split(separator: " ", maxSplits: 2, omittingEmptySubsequences: true)
                if parts.count == 3, let pid = Int32(parts[0]), let cpu = Double(parts[1]) { r[pid] = (cpu, String(parts[2])) }
            }
            return r
        }
        let a = sample()
        Thread.sleep(forTimeInterval: 1)
        let b = sample()
        return b.compactMap { pid, v in
            guard pid != getpid(), let first = a[pid], first.0 >= threshold, v.0 >= threshold else { return nil }
            if testRoot && !v.1.contains(home) { return nil }
            let words = v.1.split(separator: " ").map(String.init)
            let script = words.dropFirst().first { [".py", ".js", ".sh", ".rb"].contains("." + ($0 as NSString).pathExtension) }
            // `ps` joins argv with spaces, so a script path with spaces is rebuilt from the last " /" before it.
            var exe = script ?? words[0]
            if let s = script, let end = v.1.range(of: s), let start = v.1[..<end.lowerBound].range(of: " /", options: .backwards) {
                exe = String(v.1[v.1.index(after: start.lowerBound)..<end.upperBound])
            }
            return Busy(pid: pid, cpu: (first.0 + v.0) / 2, name: (exe as NSString).lastPathComponent, path: exe)
        }
    }

    // MARK: Security posture (read-only system queries)

    static func posture() -> [String: String] {
        func has(_ tool: String, _ args: [String], _ needle: String) -> String {
            let r = run(tool, args)
            return r.status == 0 || !r.text.isEmpty ? (r.text.contains(needle) ? "on" : "off") : "unknown"
        }
        return ["gatekeeper": has("/usr/sbin/spctl", ["--status"], "assessments enabled"),
                "sip": has("/usr/bin/csrutil", ["status"], "enabled"),
                "filevault": has("/usr/bin/fdesetup", ["status"], "FileVault is On"),
                "firewall": has("/usr/libexec/ApplicationFirewall/socketfilterfw", ["--getglobalstate"], "enabled"),
                "xprotect": FileManager.default.fileExists(atPath: "/Library/Apple/System/Library/CoreServices/XProtect.bundle") ? "present" : "missing"]
    }

    static func run(_ tool: String, _ args: [String]) -> (status: Int32, text: String) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = pipe
        do { try p.run() } catch { return (-1, "") }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return (p.terminationStatus, String(decoding: data, as: UTF8.self))
    }
}
