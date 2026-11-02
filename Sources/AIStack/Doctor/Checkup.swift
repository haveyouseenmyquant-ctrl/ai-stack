import Foundation
import CoreGraphics
import IOKit.ps

/// The user's Laptop Doctor settings, as one checkup sees them.
struct DoctorCheckupOptions {
    /// Tier 1 kinds the user switched on. Quarantine has no switch: it only acts on files the user's own ClamAV flags.
    var tier1: Set<DoctorAction> = [.compress, .evictCloud, .archiveFolder, .quarantine]
    var thresholds: [String: Int] = [:]
    var archiveFolders: [String] = []   // absolute paths
}

extension DoctorCatalogue {
    /// The N-day thresholds a user may change in Settings. Other defaults (compression gain, the 7-day rule) stay fixed.
    static let userDayKeys: Set<String> = ["coldDays", "cloudColdDays", "archiveDays", "installerAgeDays", "inactiveProjectDays"]

    func withUserThresholds(_ t: [String: Int]) -> DoctorCatalogue {
        var c = self
        // Nothing modified in the last 7 days is ever touched, so a lower value would only mislead.
        for (k, v) in t where Self.userDayKeys.contains(k) { c.defaults[k] = Double(max(v, 7)) }
        return c
    }
}

/// One checkup: recover an interrupted run → scan → Tier 1 (only the switched-on kinds) → treatment plan.
/// Writes report.json, plan.json and outcome.json to `<dir>/<checkup id>/`. Used by the scheduler and `--doctor checkup`.
enum DoctorCheckup {
    struct Result: Codable {
        var checkup: String
        var folder: String
        var tier1: DoctorExecutor.Outcome
        var tier1Disabled: Int      // Tier 1 findings left alone because their switch is off
        var planItems: Int
        var planBytes: Int64
    }

    static func run(home: String, catalogue base: DoctorCatalogue, options: DoctorCheckupOptions, journal: DoctorJournal,
                    dir: String, testRoot: Bool, checkup: String = DoctorFS.iso()) throws -> Result {
        let catalogue = base.withUserThresholds(options.thresholds)
        let ex = DoctorExecutor(home: home, catalogue: catalogue, journal: journal, checkup: checkup, testRoot: testRoot)
        _ = ex.recover(records: DoctorJournal.read(journal.path))
        var scanner = DoctorScanner(home: home, catalogue: catalogue, optedInFolders: options.archiveFolders, testRoot: testRoot)
        let findings = scanner.scan()
        let enabled = findings.filter { $0.action.tier == .automatic && options.tier1.contains($0.action) }
        let disabled = findings.filter { $0.action.tier == .automatic && !options.tier1.contains($0.action) }.count
        let outcome = ex.runTier1(enabled)
        let plan = DoctorCLI.makePlan(findings, catalogue: catalogue, checkup: checkup, root: home)
        let report = DoctorReport(generatedAt: checkup, root: home, dryRun: false, findings: findings, skipped: scanner.skipped,
                                  posture: testRoot ? [:] : DoctorScanner.posture())

        let folder = dir + "/" + checkup.replacingOccurrences(of: ":", with: "-")
        try FileManager.default.createDirectory(atPath: folder, withIntermediateDirectories: true)
        let items = plan.groups.flatMap(\.items)
        let result = Result(checkup: checkup, folder: folder, tier1: outcome, tier1Disabled: disabled,
                            planItems: items.count, planBytes: items.reduce(0) { $0 + $1.bytes })
        try DoctorCLI.emit(report, folder + "/report.json")
        try DoctorCLI.emit(plan, folder + "/plan.json")
        try DoctorCLI.emit(result, folder + "/outcome.json")
        return result
    }
}

/// When a scheduled checkup is due. Pure, so the safety suite can check it through `--doctor due`.
enum DoctorSchedule {
    /// The latest weekly slot (Calendar weekday, 1 = Sunday; hour 0–23) at or before `now`.
    static func lastSlot(_ now: Date, weekday: Int, hour: Int, calendar: Calendar) -> Date? {
        let match = DateComponents(hour: hour, minute: 0, second: 0, weekday: weekday)
        if calendar.date(now, matchesComponents: match) { return now }
        return calendar.nextDate(after: now, matching: match, matchingPolicy: .nextTime, direction: .backward)
    }

    /// Due once per slot: the slot has passed and the last checkup started before it. A slot missed while the Mac
    /// was asleep or busy runs at the next chance. No last run (just switched on) waits for the next slot.
    static func isDue(now: Date, lastRun: Date?, weekday: Int, hour: Int, calendar: Calendar = .current) -> Bool {
        guard let lastRun, let slot = lastSlot(now, weekday: weekday, hour: hour, calendar: calendar) else { return false }
        return lastRun < slot
    }

    /// At most one approval request a day, however often checkups run.
    static func mayNotify(now: Date, lastNotified: Date?, calendar: Calendar = .current) -> Bool {
        guard let lastNotified else { return true }
        return !calendar.isDate(lastNotified, inSameDayAs: now) && lastNotified < now
    }

    static func onMainsPower() -> Bool {
        guard let info = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let type = IOPSGetProvidingPowerSourceType(info)?.takeUnretainedValue() else { return true }   // desktop Macs
        return (type as String) == kIOPMACPowerKey
    }

    static func idleSeconds() -> Double {
        CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: CGEventType(rawValue: ~0)!)
    }
}
