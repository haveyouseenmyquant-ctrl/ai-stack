import AppKit
import Carbon
import Combine
import ServiceManagement
import SwiftUI
import UserNotifications

/// Shared selection for the notes window, so menus and notifications can open a given meeting.
@MainActor
final class NotesNavigation: ObservableObject {
    @Published var selection: UUID?
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate, UNUserNotificationCenterDelegate {
    static private(set) var shared: AppDelegate!

    let settings = AppSettings.shared
    private(set) var store: MeetingStore!
    let navigation = NotesNavigation()
    private(set) var recorder: MeetingRecorder!
    private(set) var detector: MeetingDetector!
    private(set) var writing: WritingAssistant!
    private(set) var doctor: DoctorScheduler!
    private var statusItem: NSStatusItem!
    private var cancellables = Set<AnyCancellable>()
    private var acceptAllHotKey: HotKey?

    func applicationDidFinishLaunching(_ notification: Notification) {
        AppDelegate.shared = self
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "")
            .filter { $0 != NSRunningApplication.current }
        if !others.isEmpty {
            Log.info("Another instance is running; exiting", "app")
            NSApp.terminate(nil)
            return
        }
        Log.info("AI Stack launched (\(Bundle.main.bundlePath))", "app")

        store = MeetingStore()
        recorder = MeetingRecorder(store: store)
        detector = MeetingDetector(recorder: recorder)
        writing = WritingAssistant()

        buildMainMenu()
        setupStatusItem()
        Notifier.setup(delegate: self)

        writing.start()
        detector.start()
        doctor = DoctorScheduler(settings: settings)
        doctor.start()
        // ⌃⌥A: accept every suggestion in the focused field, from any app.
        acceptAllHotKey = HotKey(keyCode: UInt32(kVK_ANSI_A), modifiers: UInt32(controlKey | optionKey), id: 1) { [weak self] in
            self?.writing.acceptAll()
        }

        recorder.$isRecording.combineLatest(recorder.$processing)
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.updateStatusIcon() }
            .store(in: &cancellables)
        recorder.$isRecording
            .removeDuplicates()
            .filter { $0 }
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.navigation.selection = self?.recorder.live?.id }
            .store(in: &cancellables)

        if !settings.loginItemConfigured || SMAppService.mainApp.status == .enabled {
            settings.loginItemConfigured = true
            LoginItem.setEnabled(true)
        }
        if !settings.onboardingDone { showSetup() }
        Task { _ = await Notifier.requestPermission() }
    }

    func applicationWillTerminate(_ notification: Notification) {
        if recorder?.isRecording == true, let live = recorder.live { store.upsert(live) }
        ClaudeCLI.shared.invalidateWarm()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { showNotes() }
        return true
    }

    // MARK: - Windows

    func showNotes(select id: UUID? = nil) {
        if let id { navigation.selection = id }
        WindowManager.shared.show(id: "notes", title: "Meeting Notes", size: CGSize(width: 1080, height: 720),
                                  minSize: CGSize(width: 760, height: 480)) {
            NotesRootView()
                .environmentObject(store)
                .environmentObject(recorder)
                .environmentObject(navigation)
                .environmentObject(settings)
        }
    }

    func showSettings() {
        WindowManager.shared.show(id: "settings", title: "AI Stack Settings", size: CGSize(width: 560, height: 640)) {
            SettingsView().environmentObject(settings)
        }
    }

    func showSetup() {
        WindowManager.shared.show(id: "setup", title: "Welcome to AI Stack", size: CGSize(width: 600, height: 700)) {
            SetupView().environmentObject(settings)
        }
    }

    // MARK: - Status item & menu

    private func setupStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu
        updateStatusIcon()
    }

    private func updateStatusIcon() {
        guard let button = statusItem?.button else { return }
        if recorder.isRecording {
            let image = NSImage(systemSymbolName: "record.circle.fill", accessibilityDescription: "Recording")?
                .withSymbolConfiguration(.init(paletteColors: [.white, .systemRed]))
            image?.isTemplate = false
            button.image = image
            button.toolTip = "AI Stack — taking notes"
        } else if !recorder.processing.isEmpty {
            button.image = NSImage(systemSymbolName: "ellipsis.circle", accessibilityDescription: "Writing notes")
            button.image?.isTemplate = true
            button.toolTip = "AI Stack — writing notes with Claude"
        } else {
            button.image = NSImage(systemSymbolName: "sparkles", accessibilityDescription: "AI Stack")
            button.image?.isTemplate = true
            button.toolTip = "AI Stack"
        }
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()

        menu.addItem(.sectionHeader(title: "Meeting Notes"))
        if recorder.isRecording, let live = recorder.live {
            let elapsed = Meeting.clock(Date().timeIntervalSince(live.startedAt))
            menu.addItem(disabled("● Recording — \(live.title) (\(elapsed))"))
            menu.addItem(item("Stop & Write Notes", #selector(stopRecording), key: "s"))
            menu.addItem(item("Open Live Notes", #selector(openLiveNotes)))
        } else {
            if !recorder.processing.isEmpty { menu.addItem(disabled("Writing notes with Claude…")) }
            let modeText: String
            switch settings.notesMode {
            case "auto": modeText = "Starts automatically when a call begins"
            case "ask": modeText = "Asks when a call begins"
            default: modeText = "Manual start only"
            }
            menu.addItem(disabled(settings.notesEnabled ? modeText : "Call detection is off"))
            menu.addItem(item("Start Notes Now", #selector(startManual)))
        }
        menu.addItem(item("Open Meeting Notes…", #selector(openNotesWindow), key: "n"))

        let language = NSMenuItem(title: "Call Language: \(AppSettings.languageName(settings.notesLanguage))", action: nil, keyEquivalent: "")
        let languageMenu = NSMenu()
        for entry in AppSettings.notesLanguages {
            let option = item(entry.name, #selector(chooseLanguage(_:)))
            option.representedObject = entry.id
            option.state = entry.id == settings.notesLanguage ? .on : .off
            languageMenu.addItem(option)
        }
        language.submenu = languageMenu
        menu.addItem(language)

        menu.addItem(.separator())
        menu.addItem(.sectionHeader(title: "Writing Assistant"))
        let acceptAll = item("Accept All Suggestions", #selector(acceptAllSuggestions))
        acceptAll.keyEquivalent = "a"
        acceptAll.keyEquivalentModifierMask = [.control, .option]
        menu.addItem(acceptAll)
        let toggle = item("Check My Writing", #selector(toggleWriting))
        toggle.state = settings.writingEnabled ? .on : .off
        menu.addItem(toggle)
        if let app = writing.lastExternalApp, let bundleID = app.bundleIdentifier, settings.writingEnabled {
            let off = item("Turn Off in \(app.localizedName ?? bundleID)", #selector(toggleAppDisabled(_:)))
            off.representedObject = bundleID
            off.state = settings.disabledApps.contains(bundleID) ? .on : .off
            menu.addItem(off)
        }
        if settings.writingEnabled {
            switch writing.status {
            case .needsPermission:
                menu.addItem(item("⚠︎ Grant Accessibility Permission…", #selector(openSetup)))
            case .failed(let message):
                menu.addItem(disabled("⚠︎ \(message.prefix(70))"))
            default:
                break
            }
        }

        menu.addItem(.separator())
        menu.addItem(item("Setup & Permissions…", #selector(openSetup)))
        menu.addItem(item("Settings…", #selector(openSettings), key: ","))
        menu.addItem(item("Open Notes Folder", #selector(openNotesFolder)))
        menu.addItem(.separator())
        menu.addItem(item("Quit AI Stack", #selector(quit), key: "q"))
    }

    private func item(_ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self
        return item
    }

    private func disabled(_ title: String) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        item.isEnabled = false
        return item
    }

    @objc private func stopRecording() {
        if recorder.startedAutomatically { detector.suppressCurrentCall() }
        recorder.stop()
    }

    @objc private func startManual() {
        recorder.start(app: detector.activeCallApp, suggestedTitle: nil, automatic: false)
        showNotes(select: recorder.live?.id)
    }

    @objc private func openLiveNotes() { showNotes(select: recorder.live?.id) }
    @objc private func openNotesWindow() { showNotes() }
    @objc private func openSettings() { showSettings() }
    @objc private func openSetup() { showSetup() }
    @objc private func quit() { NSApp.terminate(nil) }

    @objc private func chooseLanguage(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String else { return }
        settings.notesLanguage = id
        Task.detached { _ = try? await LiveTranscriber.prepareModel(for: id) }
    }

    @objc private func toggleWriting() { settings.writingEnabled.toggle() }
    @objc private func acceptAllSuggestions() { writing.acceptAll() }

    @objc private func toggleAppDisabled(_ sender: NSMenuItem) {
        guard let bundleID = sender.representedObject as? String else { return }
        if let index = settings.disabledApps.firstIndex(of: bundleID) {
            settings.disabledApps.remove(at: index)
        } else {
            settings.disabledApps.append(bundleID)
        }
    }

    @objc private func openNotesFolder() {
        let folder = settings.notesFolderURL
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        NSWorkspace.shared.open(folder)
    }

    // MARK: - Main menu (needed for ⌘C/⌘V/⌘Z in windows)

    private func buildMainMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Settings…", action: #selector(openSettings), keyEquivalent: ",").target = self
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Hide AI Stack", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: "Quit AI Stack", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "Z")
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        main.addItem(editItem)

        let windowItem = NSMenuItem()
        let window = NSMenu(title: "Window")
        window.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        window.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        windowItem.submenu = window
        main.addItem(windowItem)
        NSApp.mainMenu = main
        NSApp.windowsMenu = window
    }

    // MARK: - Notifications

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .sound]
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        let category = response.notification.request.content.categoryIdentifier
        let action = response.actionIdentifier
        let meetingID = (response.notification.request.content.userInfo["meeting"] as? String).flatMap(UUID.init(uuidString:))
        let doctorFolder = response.notification.request.content.userInfo["folder"] as? String
        await MainActor.run {
            switch (category, action) {
            case (Notifier.Category.doctorPlan, _):
                // Until the report window lands: show the checkup's report and plan files.
                if let doctorFolder { NSWorkspace.shared.open(URL(fileURLWithPath: doctorFolder, isDirectory: true)) }
            case (Notifier.Category.detected, Notifier.Action.start),
                 (Notifier.Category.detected, UNNotificationDefaultActionIdentifier):
                self.detector.startPending()
                self.showNotes(select: self.recorder.live?.id)
            case (Notifier.Category.recording, Notifier.Action.stop):
                self.stopRecording()
            default:
                self.showNotes(select: meetingID)
            }
        }
    }
}
