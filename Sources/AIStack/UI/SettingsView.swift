import AppKit
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var settings: AppSettings
    @State private var claudePathDisplay = ""
    @State private var showDoctorIntro = false
    @State private var doctorRules: [(rule: DoctorRule, label: String)] = []
    @State private var doctorRunning = false

    private let models = [("haiku", "Haiku — fastest"), ("sonnet", "Sonnet — balanced"), ("opus", "Opus — best quality")]

    var body: some View {
        Form {
            Section("Writing assistant") {
                Toggle("Check my writing in every app", isOn: $settings.writingEnabled)
                Toggle("Suggest better phrasing (blue underlines)", isOn: $settings.styleSuggestions)
                Picker("Model", selection: $settings.writingModel) {
                    ForEach(models, id: \.0) { Text($0.1).tag($0.0) }
                }
                VStack(alignment: .leading, spacing: 6) {
                    Text("Turned off in").font(.callout)
                    ForEach(settings.disabledApps, id: \.self) { bundleID in
                        HStack {
                            Text(appName(bundleID))
                            Text(bundleID).font(.caption).foregroundStyle(.secondary)
                            Spacer()
                            Button {
                                settings.disabledApps.removeAll { $0 == bundleID }
                            } label: { Image(systemName: "minus.circle") }
                                .buttonStyle(.borderless)
                        }
                    }
                    Text("Tip: use the menu bar icon → “Turn Off in <App>” while that app is in front.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            Section("Meeting notes") {
                Toggle("Detect calls", isOn: $settings.notesEnabled)
                Picker("When a call starts", selection: $settings.notesMode) {
                    Text("Take notes automatically").tag("auto")
                    Text("Ask me with a notification").tag("ask")
                    Text("Do nothing (start manually)").tag("manual")
                }
                Picker("Call language", selection: $settings.notesLanguage) {
                    ForEach(AppSettings.notesLanguages, id: \.id) { Text($0.name).tag($0.id) }
                }
                Picker("Notes model", selection: $settings.notesModel) {
                    ForEach(models, id: \.0) { Text($0.1).tag($0.0) }
                }
                HStack {
                    Text("Markdown folder")
                    Spacer()
                    Text(settings.notesFolder).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                    Button("Choose…") { chooseFolder() }
                }
            }

            Section("Laptop Doctor") {
                Toggle("Run a checkup on a schedule", isOn: Binding(
                    get: { settings.doctorEnabled },
                    set: { on in if on && !settings.doctorEnabled { showDoctorIntro = true } else { settings.doctorEnabled = on } }))
                Picker("Day", selection: $settings.doctorWeekday) {
                    ForEach(1...7, id: \.self) { Text(Calendar.current.weekdaySymbols[$0 - 1]).tag($0) }
                }
                Picker("Time", selection: $settings.doctorHour) {
                    ForEach(0..<24, id: \.self) { Text(String(format: "%02d:00", $0)).tag($0) }
                }
                Text("Only on mains power and when the Mac is idle. At most one approval request a day.")
                    .font(.caption).foregroundStyle(.secondary)
                if settings.doctorEnabled {
                    HStack {
                        Button("Check Up Now") { checkUpNow() }.disabled(doctorRunning)
                        if doctorRunning { ProgressView().controlSize(.small); Text("Checking…").foregroundStyle(.secondary) }
                    }
                }
                Toggle("Free up local copies of synced cloud files", isOn: $settings.doctorEvictCloud)
                Toggle("Compress cold files in place (they still open normally)", isOn: $settings.doctorCompress)
                Toggle("Zip folders I mark for archiving", isOn: $settings.doctorArchive)
                if settings.doctorArchive {
                    ForEach(settings.doctorArchiveFolders, id: \.self) { folder in
                        HStack {
                            Text(folder).lineLimit(1).truncationMode(.middle)
                            Spacer()
                            Button { settings.doctorArchiveFolders.removeAll { $0 == folder } } label: { Image(systemName: "minus.circle") }
                                .buttonStyle(.borderless)
                        }
                    }
                    Button("Add Folder to Archive When Unused…") { chooseArchiveFolder() }
                }
                ForEach(Self.doctorThresholdKeys, id: \.key) { t in
                    Stepper(value: thresholdBinding(t.key, t.fallback), in: 7...365, step: t.key == "installerAgeDays" ? 1 : 15) {
                        Text("\(t.label): \(thresholdBinding(t.key, t.fallback).wrappedValue) days")
                    }
                }
                LabeledContent("Always do this (rules)") {
                    if doctorRules.isEmpty { Text("None").foregroundStyle(.secondary) }
                }
                ForEach(doctorRules, id: \.rule.id) { r in
                    HStack {
                        Text(r.label)
                        Text("since " + (DoctorFS.date(r.rule.created)?.formatted(date: .abbreviated, time: .omitted) ?? r.rule.created))
                            .font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        Button("Remove") { removeDoctorRule(r.rule.id) }
                    }
                }
                Toggle("Summarise each checkup with Claude", isOn: $settings.doctorClaudeSummary)
                Text("The summary gets totals per category, your security settings and the names of startup items and busy processes. It never gets the names of your files or folders.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Claude") {
                HStack {
                    Text("Claude Code CLI")
                    Spacer()
                    Text(claudePathDisplay).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                }
                TextField("Custom path (optional)", text: $settings.claudePath)
                Text("All AI requests run through the `claude` CLI and its login, so they use your Claude subscription. No API key needed.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Data") {
                HStack {
                    Button("Open Meeting Data") { NSWorkspace.shared.open(AppPaths.meetings) }
                    Button("Open Log") { NSWorkspace.shared.open(Log.fileURL) }
                }
            }
        }
        .formStyle(.grouped)
        .sheet(isPresented: $showDoctorIntro) { DoctorIntroView(settings: settings, isPresented: $showDoctorIntro) }
        .frame(minWidth: 520, minHeight: 560)
        .onAppear {
            loadDoctorRules()
            DispatchQueue.global().async {
                let path = ClaudeCLI.shared.binaryPath() ?? "Not found"
                DispatchQueue.main.async { claudePathDisplay = path }
            }
        }
    }

    private func loadDoctorRules() {
        guard let catalogue = try? DoctorCatalogue.load(nil) else { return }
        let book = DoctorRuleBook.load(DoctorScheduler.checkupsPath + "/rules.json", catalogue: catalogue)
        doctorRules = book.rules.map { r in (r, catalogue.categories.first { $0.id == r.category }?.label ?? r.category) }
    }

    /// Removing a rule only stops future auto-approvals; History keeps Undo for what it already did.
    private func removeDoctorRule(_ id: String) {
        guard let catalogue = try? DoctorCatalogue.load(nil) else { return }
        let path = DoctorScheduler.checkupsPath + "/rules.json"
        var book = DoctorRuleBook.load(path, catalogue: catalogue)
        if book.remove(id) {
            do { try DoctorCLI.emit(book, path) } catch { Log.error("Couldn't save Laptop Doctor rules: \(error)", "doctor") }
        }
        loadDoctorRules()
    }

    private func checkUpNow() {
        guard let doctor = (NSApp.delegate as? AppDelegate)?.doctor else { return }
        doctor.runNow()
        doctorRunning = true
        Task { @MainActor in
            while doctor.running { try? await Task.sleep(for: .seconds(1)) }
            doctorRunning = false
            (NSApp.delegate as? AppDelegate)?.showDoctor()
        }
    }

    static let doctorThresholdKeys: [(key: String, label: String, fallback: Int)] = [
        ("coldDays", "Compress files unused for", 60),
        ("cloudColdDays", "Free cloud copies unopened for", 60),
        ("archiveDays", "Archive marked folders unused for", 90),
        ("installerAgeDays", "Suggest removing installers older than", 30),
        ("inactiveProjectDays", "Suggest cleaning projects untouched for", 90),
    ]

    private func thresholdBinding(_ key: String, _ fallback: Int) -> Binding<Int> {
        Binding(get: { settings.doctorThresholds[key] ?? fallback }, set: { settings.doctorThresholds[key] = $0 })
    }

    private func chooseArchiveFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.directoryURL = FileManager.default.homeDirectoryForCurrentUser
        if panel.runModal() == .OK, let url = panel.url {
            let path = (url.path as NSString).abbreviatingWithTildeInPath
            if !settings.doctorArchiveFolders.contains(path) { settings.doctorArchiveFolders.append(path) }
        }
    }

    private func appName(_ bundleID: String) -> String {
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) else { return bundleID }
        return FileManager.default.displayName(atPath: url.path).replacingOccurrences(of: ".app", with: "")
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.directoryURL = settings.notesFolderURL
        if panel.runModal() == .OK, let url = panel.url {
            settings.notesFolder = (url.path as NSString).abbreviatingWithTildeInPath
        }
    }
}

/// Shown once before Laptop Doctor is turned on: a plain list of what it will and won't ever do.
struct DoctorIntroView: View {
    @ObservedObject var settings: AppSettings
    @Binding var isPresented: Bool

    static let will = [
        "Scan for malware (with ClamAV, if you installed it), weak security settings, slow startup items and busy processes.",
        "Free disk space without losing anything: remove local copies of files already in your cloud storage, compress cold files in place, and zip folders you marked.",
        "Record every change before making it, so each one can be undone from History.",
        "Put everything else, such as deleting caches or old installers, into one plan that waits for your approval.",
    ]
    static let wont = [
        "Delete anything without your approval. \u{201C}Delete\u{201D} always means moving to the Trash.",
        "Touch system files, other users' files, passwords and keychains, Mail, Messages, Photos or browser profiles.",
        "Change the inside of git repositories, or anything you changed in the last 7 days.",
        "Turn off Gatekeeper, SIP, FileVault or the firewall.",
        "Send the names of your files or folders to Claude.",
    ]

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Before you turn on Laptop Doctor").font(.title2.bold())
            Text("It will").font(.headline)
            ForEach(Self.will, id: \.self) { Label($0, systemImage: "checkmark.circle").foregroundStyle(.primary) }
            Text("It will never").font(.headline)
            ForEach(Self.wont, id: \.self) { Label($0, systemImage: "xmark.circle") }
            Text("A full scan needs Full Disk Access. Without it, Laptop Doctor scans what it can and lists what it skipped.")
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Not Now") { isPresented = false }.keyboardShortcut(.cancelAction)
                Button("Turn On") { settings.doctorEnabled = true; isPresented = false }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(24)
        .frame(width: 520)
    }
}
