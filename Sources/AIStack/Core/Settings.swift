import Foundation
import Combine

enum AppPaths {
    static let support: URL = {
        let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("AIStack", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }()

    static let meetings: URL = {
        let url = support.appendingPathComponent("Meetings", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }()
}

/// User preferences, persisted in UserDefaults. Mutate from the main thread.
final class AppSettings: ObservableObject {
    static let shared = AppSettings()

    static let defaultDisabledApps: [String] = [
        "com.apple.Terminal", "com.googlecode.iterm2", "dev.warp.Warp-Stable", "com.mitchellh.ghostty",
        "com.microsoft.VSCode", "com.todesktop.230313mzl4w4u92", "com.apple.dt.Xcode",
        "com.1password.1password", "com.apple.keychainaccess", "com.apple.Passwords",
        "com.apple.loginwindow", "com.apple.ScreenSharing",
    ]

    static let notesLanguages: [(id: String, name: String)] = [
        ("en-GB", "English (UK)"), ("en-US", "English (US)"),
        ("fr-FR", "Français (France)"), ("fr-BE", "Français (Belgique)"), ("fr-CH", "Français (Suisse)"),
        ("es-ES", "Español"), ("de-DE", "Deutsch"), ("it-IT", "Italiano"),
        ("pt-PT", "Português"), ("nl-NL", "Nederlands"),
    ]

    private let defaults = UserDefaults.standard

    @Published var writingEnabled: Bool { didSet { defaults.set(writingEnabled, forKey: "writing.enabled") } }
    @Published var writingModel: String {
        didSet {
            defaults.set(writingModel, forKey: "writing.model")
            ClaudeCLI.shared.invalidateWarm()
        }
    }
    @Published var styleSuggestions: Bool { didSet { defaults.set(styleSuggestions, forKey: "writing.style") } }
    @Published var disabledApps: [String] { didSet { defaults.set(disabledApps, forKey: "writing.disabledApps") } }

    @Published var notesEnabled: Bool { didSet { defaults.set(notesEnabled, forKey: "notes.enabled") } }
    /// "auto" = start automatically, "ask" = notification first, "manual" = never start by itself.
    @Published var notesMode: String { didSet { defaults.set(notesMode, forKey: "notes.mode") } }
    @Published var notesModel: String { didSet { defaults.set(notesModel, forKey: "notes.model") } }
    @Published var notesLanguage: String { didSet { defaults.set(notesLanguage, forKey: "notes.language") } }
    @Published var notesFolder: String { didSet { defaults.set(notesFolder, forKey: "notes.folder") } }

    @Published var claudePath: String { didSet { defaults.set(claudePath, forKey: "claude.path") } }

    // Laptop Doctor: off until the user has read what it will and won't do (spec: BRIEF §3).
    @Published var doctorEnabled: Bool { didSet { defaults.set(doctorEnabled, forKey: "doctor.enabled") } }
    /// 1 = Sunday … 7 = Saturday (Calendar weekday), hour 0–23. Runs only on mains power and when idle.
    @Published var doctorWeekday: Int { didSet { defaults.set(doctorWeekday, forKey: "doctor.weekday") } }
    @Published var doctorHour: Int { didSet { defaults.set(doctorHour, forKey: "doctor.hour") } }
    @Published var doctorEvictCloud: Bool { didSet { defaults.set(doctorEvictCloud, forKey: "doctor.tier1.evictCloud") } }
    @Published var doctorCompress: Bool { didSet { defaults.set(doctorCompress, forKey: "doctor.tier1.compress") } }
    @Published var doctorArchive: Bool { didSet { defaults.set(doctorArchive, forKey: "doctor.tier1.archive") } }
    /// Folders the user marked "archive when unused for N days" (paths with ~).
    @Published var doctorArchiveFolders: [String] { didSet { defaults.set(doctorArchiveFolders, forKey: "doctor.archiveFolders") } }
    /// Overrides of the catalogue's N-day thresholds (coldDays, cloudColdDays, installerAgeDays, inactiveProjectDays).
    @Published var doctorThresholds: [String: Int] { didSet { defaults.set(doctorThresholds, forKey: "doctor.thresholds") } }
    @Published var doctorClaudeSummary: Bool { didSet { defaults.set(doctorClaudeSummary, forKey: "doctor.claudeSummary") } }

    var onboardingDone: Bool {
        get { defaults.bool(forKey: "onboarding.done") }
        set { defaults.set(newValue, forKey: "onboarding.done") }
    }
    var loginItemConfigured: Bool {
        get { defaults.bool(forKey: "loginItem.configured") }
        set { defaults.set(newValue, forKey: "loginItem.configured") }
    }

    var notesFolderURL: URL { URL(fileURLWithPath: (notesFolder as NSString).expandingTildeInPath, isDirectory: true) }

    private init() {
        defaults.register(defaults: [
            "writing.enabled": true,
            "writing.model": "haiku",
            "writing.style": true,
            "writing.disabledApps": Self.defaultDisabledApps,
            "notes.enabled": true,
            "notes.mode": "auto",
            "notes.model": "opus",
            "notes.language": "en-GB",
            "notes.folder": "~/Documents/Meeting Notes",
            "claude.path": "",
            "doctor.enabled": false,
            "doctor.weekday": 1,
            "doctor.hour": 11,
            "doctor.tier1.evictCloud": true,
            "doctor.tier1.compress": true,
            "doctor.tier1.archive": true,
            "doctor.claudeSummary": true,
        ])
        writingEnabled = defaults.bool(forKey: "writing.enabled")
        writingModel = defaults.string(forKey: "writing.model") ?? "haiku"
        styleSuggestions = defaults.bool(forKey: "writing.style")
        disabledApps = defaults.stringArray(forKey: "writing.disabledApps") ?? Self.defaultDisabledApps
        notesEnabled = defaults.bool(forKey: "notes.enabled")
        notesMode = defaults.string(forKey: "notes.mode") ?? "auto"
        notesModel = defaults.string(forKey: "notes.model") ?? "opus"
        notesLanguage = defaults.string(forKey: "notes.language") ?? "en-GB"
        notesFolder = defaults.string(forKey: "notes.folder") ?? "~/Documents/Meeting Notes"
        claudePath = defaults.string(forKey: "claude.path") ?? ""
        doctorEnabled = defaults.bool(forKey: "doctor.enabled")
        doctorWeekday = defaults.integer(forKey: "doctor.weekday")
        doctorHour = defaults.integer(forKey: "doctor.hour")
        doctorEvictCloud = defaults.bool(forKey: "doctor.tier1.evictCloud")
        doctorCompress = defaults.bool(forKey: "doctor.tier1.compress")
        doctorArchive = defaults.bool(forKey: "doctor.tier1.archive")
        doctorArchiveFolders = defaults.stringArray(forKey: "doctor.archiveFolders") ?? []
        doctorThresholds = defaults.dictionary(forKey: "doctor.thresholds") as? [String: Int] ?? [:]
        doctorClaudeSummary = defaults.bool(forKey: "doctor.claudeSummary")
    }

    static func languageName(_ id: String) -> String {
        notesLanguages.first { $0.id == id }?.name ?? Locale.current.localizedString(forIdentifier: id) ?? id
    }
}
