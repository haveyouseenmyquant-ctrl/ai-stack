import Foundation

/// Runs the weekly checkup inside the app. Every 10 minutes it asks whether a slot is due; a due checkup waits for
/// mains power and 5 idle minutes. Tier 1 runs with the user's switches; Tier 2 only goes into the plan.
/// One notification a day at most, and only when the plan has something to decide.
@MainActor
final class DoctorScheduler {
    nonisolated static let folder = AppPaths.support.appendingPathComponent("Doctor", isDirectory: true).path
    nonisolated static var journalPath: String { folder + "/journal.jsonl" }
    nonisolated static var checkupsPath: String { folder + "/Checkups" }

    private let settings: AppSettings
    private let defaults = UserDefaults.standard
    private var timer: Timer?
    private(set) var running = false

    init(settings: AppSettings) { self.settings = settings }

    func start() {
        timer = Timer.scheduledTimer(withTimeInterval: 600, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
        tick()
    }

    func tick() {
        guard settings.doctorEnabled, !running else { return }
        let now = Date()
        guard let last = defaults.object(forKey: "doctor.lastRun") as? Date else {
            defaults.set(now, forKey: "doctor.lastRun")   // switched on before this key existed: wait for the next slot
            return
        }
        guard DoctorSchedule.isDue(now: now, lastRun: last, weekday: settings.doctorWeekday, hour: settings.doctorHour),
              DoctorSchedule.onMainsPower(), DoctorSchedule.idleSeconds() >= 300 else { return }
        runNow()
    }

    func runNow() {
        guard !running else { return }
        running = true
        defaults.set(Date(), forKey: "doctor.lastRun")
        var opts = DoctorCheckupOptions(thresholds: settings.doctorThresholds,
                                        archiveFolders: settings.doctorArchive ? settings.doctorArchiveFolders.map { ($0 as NSString).expandingTildeInPath } : [])
        if !settings.doctorEvictCloud { opts.tier1.remove(.evictCloud) }
        if !settings.doctorCompress { opts.tier1.remove(.compress) }
        if !settings.doctorArchive { opts.tier1.remove(.archiveFolder) }
        Log.info("Laptop Doctor checkup starting", "doctor")
        Task.detached(priority: .background) { [opts] in
            let result: Result<DoctorCheckup.Result, Error> = Result {
                try FileManager.default.createDirectory(atPath: Self.folder, withIntermediateDirectories: true)
                return try DoctorCheckup.run(home: NSHomeDirectory(), catalogue: try DoctorCatalogue.load(nil), options: opts,
                                             journal: try DoctorJournal(path: Self.journalPath), dir: Self.checkupsPath, testRoot: false)
            }
            await MainActor.run { self.finished(result) }
        }
    }

    private func finished(_ result: Result<DoctorCheckup.Result, Error>) {
        running = false
        switch result {
        case .failure(let e):
            Log.error("Laptop Doctor checkup failed: \(e)", "doctor")
        case .success(let r):
            Log.info("Laptop Doctor checkup done: Tier 1 \(r.tier1.done) done, \(r.tier1.freedBytes) bytes; plan \(r.planItems) items", "doctor")
            let last = defaults.object(forKey: "doctor.lastNotified") as? Date
            guard r.planItems > 0, DoctorSchedule.mayNotify(now: Date(), lastNotified: last) else { return }
            defaults.set(Date(), forKey: "doctor.lastNotified")
            Notifier.doctorPlanReady(r)
        }
    }
}
