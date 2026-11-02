import AppKit
import SwiftUI

/// Backs the Laptop Doctor window. Every button goes through DoctorDesk, the same code the safety suite drives.
@MainActor
final class DoctorDeskModel: ObservableObject {
    let home: String
    let dir: String
    let journalPath: String
    let testRoot: Bool

    @Published var folders: [String] = []
    @Published var folder: String? { didSet { if folder != oldValue { loadCheckup() } } }
    @Published var report: DoctorReport?
    @Published var result: DoctorCheckup.Result?
    @Published var plan: TreatmentPlan?
    @Published var decided: Set<String> = []
    @Published var unticked: Set<String> = []
    @Published var history: [DoctorDesk.HistoryCheckup] = []
    @Published var summary = ""
    @Published var claudeSummary = false
    @Published var message: String?
    @Published var busy = false

    init(home: String = NSHomeDirectory(), dir: String = DoctorScheduler.checkupsPath, journalPath: String = DoctorScheduler.journalPath,
         testRoot: Bool = false) {
        self.home = home; self.dir = dir; self.journalPath = journalPath; self.testRoot = testRoot
        reload()
    }

    func reload(select: String? = nil) {
        folders = DoctorDesk.checkupFolders(dir)
        let want = select ?? folder ?? folders.first
        if folder == want { loadCheckup() } else { folder = want }
        history = DoctorDesk.history(DoctorJournal.read(journalPath))
    }

    private func loadCheckup() {
        func read<T: Decodable>(_ name: String) -> T? {
            guard let folder, let d = FileManager.default.contents(atPath: folder + "/" + name) else { return nil }
            return try? JSONDecoder().decode(T.self, from: d)
        }
        report = read("report.json"); result = read("outcome.json"); plan = read("plan.json")
        decided = plan.map { DoctorDesk.decidedIds(plan: $0, records: DoctorJournal.read(journalPath)) } ?? []
        unticked = []
        claudeSummary = false
        if let folder, let saved = try? String(contentsOfFile: folder + "/summary.txt", encoding: .utf8) {
            summary = saved; claudeSummary = true
        } else if let report {
            let open = openItems
            summary = DoctorDesk.templateSummary(report: report, result: result, planItems: open.count, planBytes: open.reduce(0) { $0 + $1.bytes })
        } else {
            summary = folders.isEmpty ? "No checkup has run yet." : "This checkup's report can't be read."
        }
    }

    var openItems: [Finding] { (plan?.groups ?? []).flatMap(\.items).filter { !decided.contains($0.id) } }

    func openItems(_ g: PlanGroup) -> [Finding] { g.items.filter { !decided.contains($0.id) } }

    func ruleable(_ g: PlanGroup) -> Bool {
        guard let c = try? DoctorCatalogue.load(nil) else { return false }
        return DoctorRuleBook.allowed(g.category, c)
    }

    /// Ticked, undecided items of the given groups.
    func chosen(_ groups: [PlanGroup]) -> Set<String> {
        Set(groups.flatMap { openItems($0) }.map(\.id).filter { !unticked.contains($0) })
    }

    func decide(_ d: DoctorDesk.Decision, _ ids: Set<String>) {
        guard let plan, !ids.isEmpty, !busy else { return }
        busy = true
        let (home, dir, jp, test) = (home, dir, journalPath, testRoot)
        Task.detached(priority: .userInitiated) {
            let r = Result { try DoctorDesk.decide(d, ids: ids, plan: plan, home: home, catalogue: try DoctorCatalogue.load(nil),
                                                   journal: try DoctorJournal(path: jp), dir: dir, testRoot: test) }
            await MainActor.run {
                self.busy = false
                switch r {
                case .failure(let e): self.message = "\(e)"
                case .success(let res): self.message = Self.describe(d, res)
                }
                self.reload()
            }
        }
    }

    private static func describe(_ d: DoctorDesk.Decision, _ r: DoctorDesk.DecideResult) -> String {
        guard let o = r.outcome else { return "Declined \(r.decided). They won't be suggested again unless they change." }
        var s = "Done: \(o.done)"
        if o.freedBytes > 0 { s += ", \(ByteCountFormatter.string(fromByteCount: o.freedBytes, countStyle: .file)) moved to the Trash" }
        if o.skipped > 0 { s += ". Skipped \(o.skipped) (changed since the checkup, or not supported yet)" }
        if o.refused > 0 { s += ". Refused \(o.refused)" }
        if !r.rules.isEmpty { s += ". From now on this kind is done automatically; remove the rule in Settings" }
        return s + "."
    }

    func undo(entry: String? = nil, checkup: String? = nil) {
        guard !busy else { return }
        busy = true
        let (home, jp, test) = (home, journalPath, testRoot)
        Task.detached(priority: .userInitiated) {
            let r = Result { () throws -> DoctorExecutor.Outcome in
                let cat = try DoctorCatalogue.load(nil), j = try DoctorJournal(path: jp)
                if let checkup { return DoctorDesk.undo(checkup: checkup, home: home, catalogue: cat, journal: j, testRoot: test) }
                let ex = DoctorExecutor(home: home, catalogue: cat, journal: j, checkup: DoctorFS.iso(), testRoot: test)
                return ex.undo(records: DoctorJournal.read(jp), entry: entry)
            }
            await MainActor.run {
                self.busy = false
                switch r {
                case .failure(let e): self.message = "\(e)"
                case .success(let o): self.message = "Undone: \(o.done)" + (o.failed > 0 ? ", \(o.failed) couldn't be undone" : "") + "."
                }
                self.reload()
            }
        }
    }

    /// The optional Claude summary: sends only DoctorSummary.facts (counts, sizes, settings, program names).
    func writeClaudeSummary(model: String) {
        guard let report, let folder, !busy else { return }
        busy = true
        Task {
            do {
                let text = try await DoctorSummary.write(report, model: model)
                try? text.write(toFile: folder + "/summary.txt", atomically: true, encoding: .utf8)
                summary = text; claudeSummary = true
            } catch { message = "Claude summary failed: \(error)" }
            busy = false
        }
    }
}

struct DoctorWindowView: View {
    @ObservedObject var model: DoctorDeskModel
    @EnvironmentObject var settings: AppSettings
    @State private var tab: Int
    private let expandFirst: Bool

    init(model: DoctorDeskModel, initialTab: Int = 0, expandFirst: Bool = false) {
        self.model = model
        _tab = State(initialValue: initialTab)
        self.expandFirst = expandFirst
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Picker("", selection: $tab) { Text("Checkup").tag(0); Text("History").tag(1) }
                    .pickerStyle(.segmented).frame(width: 220)
                Spacer()
                if model.folders.count > 1 && tab == 0 {
                    Picker("Checkup", selection: $model.folder) {
                        ForEach(model.folders, id: \.self) { Text(Self.when($0)).tag(Optional($0)) }
                    }.frame(width: 280)
                }
                if model.busy { ProgressView().controlSize(.small) }
            }
            if let m = model.message {
                HStack { Text(m).font(.callout); Spacer(); Button("OK") { model.message = nil }.buttonStyle(.borderless) }
                    .padding(8).background(.quaternary, in: RoundedRectangle(cornerRadius: 6))
            }
            if tab == 0 { checkup } else { DoctorHistoryView(model: model) }
        }
        .padding(16)
        .frame(minWidth: 640, minHeight: 480)
    }

    private var checkup: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                GroupBox {
                    VStack(alignment: .leading, spacing: 8) {
                        Text(model.summary).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                        if settings.doctorClaudeSummary && !model.claudeSummary && model.report != nil {
                            Button("Write a plain-English summary with Claude") { model.writeClaudeSummary(model: "haiku") }
                                .disabled(model.busy)
                            Text("Sends counts, sizes, security settings and program names only, never the names of your files or folders.")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                } label: { Label(model.claudeSummary ? "Summary (by Claude)" : "Summary", systemImage: "stethoscope") }

                if let plan = model.plan, !model.openItems.isEmpty {
                    HStack {
                        Text("Treatment plan").font(.headline)
                        Spacer()
                        Button("Approve all") { model.decide(.approve, model.chosen(plan.groups)) }
                            .disabled(model.busy)   // no Return shortcut: approving everything takes a deliberate click
                    }
                    Text("Nothing here happens until you approve it. Deleted items go to the Trash, and you can undo them in History.")
                        .font(.caption).foregroundStyle(.secondary)
                    let groups = plan.groups.filter { !model.openItems($0).isEmpty }
                    ForEach(groups, id: \.category) { g in
                        DoctorGroupView(model: model, group: g, expanded: expandFirst && g.category == groups.first?.category)
                    }
                } else if model.plan != nil {
                    Text("Nothing waits for your decision.").foregroundStyle(.secondary)
                }
            }
        }
    }

    static func when(_ folder: String) -> String {
        let name = (folder as NSString).lastPathComponent
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let iso = name.count > 13 ? String(name.prefix(10)) + name.dropFirst(10).replacingOccurrences(of: "-", with: ":") : name
        guard let d = f.date(from: iso) else { return name }
        return d.formatted(date: .abbreviated, time: .shortened)
    }
}

struct DoctorGroupView: View {
    @ObservedObject var model: DoctorDeskModel
    let group: PlanGroup
    @State private var expanded: Bool

    init(model: DoctorDeskModel, group: PlanGroup, expanded: Bool = false) {
        self.model = model; self.group = group
        _expanded = State(initialValue: expanded)
    }

    var body: some View {
        let items = model.openItems(group)
        let chosen = model.chosen([group])
        GroupBox {
            DisclosureGroup(isExpanded: $expanded) {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(items, id: \.id) { f in
                        Toggle(isOn: Binding(get: { !model.unticked.contains(f.id) },
                                             set: { on in if on { model.unticked.remove(f.id) } else { model.unticked.insert(f.id) } })) {
                            HStack {
                                Text(Self.display(f, home: model.home)).lineLimit(1).truncationMode(.middle)
                                Spacer()
                                if f.bytes > 0 { Text(Self.size(f.bytes)).foregroundStyle(.secondary).monospacedDigit() }
                            }
                        }
                        .help(f.reason)
                    }
                }.padding(.top, 4)
            } label: {
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(group.label).font(.body.weight(.medium))
                        Text(items.first?.reason ?? "").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                    Text(Self.size(items.reduce(0) { $0 + $1.bytes })).monospacedDigit()
                }
            }
            HStack {
                Spacer()
                Button("Decline") { model.decide(.decline, chosen) }
                if model.ruleable(group) {
                    Button("Always do this") { model.decide(.always, chosen) }
                        .help("Approve this kind of item automatically in later checkups. Only for things that can be undone.")
                }
                Button(chosen.count == items.count ? "Approve" : "Approve \(chosen.count)") { model.decide(.approve, chosen) }
            }
            .disabled(model.busy || chosen.isEmpty)
        }
    }

    static func size(_ b: Int64) -> String { ByteCountFormatter.string(fromByteCount: b, countStyle: .file) }

    static func display(_ f: Finding, home: String) -> String {
        if let p = f.processName { return p }
        return f.path.hasPrefix(home + "/") ? "~" + f.path.dropFirst(home.count) : f.path
    }
}

struct DoctorHistoryView: View {
    @ObservedObject var model: DoctorDeskModel

    var body: some View {
        if model.history.isEmpty {
            Text("Laptop Doctor hasn't changed anything yet.").foregroundStyle(.secondary)
            Spacer()
        } else {
            List {
                ForEach(model.history) { hc in
                    Section {
                        ForEach(hc.entries) { e in
                            HStack {
                                Image(systemName: Self.icon(e.action)).frame(width: 18)
                                VStack(alignment: .leading, spacing: 1) {
                                    Text(DoctorGroupView.display(Finding(id: e.id, category: "", action: e.action, tier: e.action.tier, path: e.path,
                                                                         bytes: 0, reason: ""), home: model.home))
                                        .lineLimit(1).truncationMode(.middle).strikethrough(e.undone)
                                    Text(Self.verb(e.action) + (e.by.map { $0.hasPrefix("rule:") ? " · by a rule" : " · you approved" } ?? "")
                                         + (e.note.map { " · " + $0 } ?? ""))
                                        .font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer()
                                if e.freedBytes > 0 { Text(DoctorGroupView.size(e.freedBytes)).monospacedDigit().foregroundStyle(.secondary) }
                                if e.undone { Text("Undone").font(.caption).foregroundStyle(.secondary) }
                                else if e.canUndo { Button("Undo") { model.undo(entry: e.id) }.disabled(model.busy) }
                            }
                        }
                    } header: {
                        HStack {
                            Text(DoctorWindowView.when(hc.checkup.replacingOccurrences(of: ":", with: "-")))
                            Spacer()
                            if hc.entries.contains(where: \.canUndo) {
                                Button("Undo this checkup") { model.undo(checkup: hc.checkup) }.disabled(model.busy)
                            }
                        }
                    }
                }
            }
        }
    }

    static func verb(_ a: DoctorAction) -> String {
        switch a {
        case .compress: return "Compressed in place"
        case .evictCloud: return "Removed the local copy (still in your cloud)"
        case .archiveFolder: return "Archived to a zip"
        case .quarantine: return "Quarantined"
        case .moveToTrash: return "Moved to the Trash"
        case .emptyTrash: return "Emptied the Trash"
        case .disableStartupItem: return "Startup item turned off"
        case .disableBrowserExtension: return "Browser extension turned off"
        case .quitProcess: return "Quit"
        case .uninstallApp: return "Uninstalled"
        case .moveFile: return "Moved"
        case .report: return "Reported"
        }
    }

    static func icon(_ a: DoctorAction) -> String {
        switch a {
        case .compress: return "arrow.down.right.and.arrow.up.left"
        case .evictCloud: return "icloud.and.arrow.up"
        case .archiveFolder: return "archivebox"
        case .quarantine: return "shield.lefthalf.filled"
        case .moveToTrash, .emptyTrash: return "trash"
        case .disableStartupItem, .disableBrowserExtension, .quitProcess, .uninstallApp, .moveFile, .report: return "gearshape"
        }
    }
}

/// Renders the window offscreen to a PNG (README screenshots, from fixtures only): `--doctor render`.
enum DoctorSnapshot {
    @MainActor
    static func render(home: String, dir: String, journal: String, tab: Int, expandFirst: Bool, out: String) throws {
        _ = NSApplication.shared
        let model = DoctorDeskModel(home: home, dir: dir, journalPath: journal, testRoot: true)
        let view = DoctorWindowView(model: model, initialTab: tab, expandFirst: expandFirst).environmentObject(AppSettings.shared)
            .background(Color(nsColor: .windowBackgroundColor))
        let size = NSSize(width: 760, height: 640)
        let host = NSHostingView(rootView: view)
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: [.titled], backing: .buffered, defer: false)
        window.title = "Laptop Doctor"
        window.contentView = host
        window.appearance = NSAppearance(named: .aqua)
        window.layoutIfNeeded()
        RunLoop.main.run(until: Date().addingTimeInterval(0.5))
        host.layoutSubtreeIfNeeded()
        guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { throw DoctorCLI.UsageError(description: "no bitmap") }
        host.cacheDisplay(in: host.bounds, to: rep)
        guard let png = rep.representation(using: .png, properties: [:]) else { throw DoctorCLI.UsageError(description: "png") }
        try png.write(to: URL(fileURLWithPath: out))
    }
}
