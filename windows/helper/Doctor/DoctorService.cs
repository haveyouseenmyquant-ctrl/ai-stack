using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIStack.Doctor;

/// The user's Laptop Doctor settings on Windows (same keys as the macOS Settings section), kept in settings.json.
public class DoctorSettings
{
    public bool Enabled { get; set; }
    public int Weekday { get; set; } = 1;          // 1 = Sunday … 7 = Saturday
    public int Hour { get; set; } = 10;
    public bool EvictCloud { get; set; } = true;
    public bool Compress { get; set; } = true;
    public bool Archive { get; set; }
    public List<string> ArchiveFolders { get; set; } = new();
    public Dictionary<string, int> Thresholds { get; set; } = new();
    public bool ClaudeSummary { get; set; }
    public DateTime? LastRun { get; set; }
    public DateTime? LastNotified { get; set; }
}

/// Laptop Doctor inside the native host: the weekly checkup and what the extension's doctor.html page asks for.
/// The host runs while Chrome does, so a missed slot runs at the next chance (as after sleep on macOS).
///
/// `AISTACK_DOCTOR_HOME` points every write at a fixture home instead of the profile (the safety suite drives the
/// host this way); `AISTACK_DOCTOR_DATA` moves the support folder. Off Windows, without a fixture home, nothing
/// that writes is allowed.
public sealed class DoctorService
{
    readonly Func<JsonObject, Task> emit;
    readonly object gate = new();
    // A fixture home that is the real profile (or above it) is ignored: test-mode rules must never touch real data.
    readonly string? fixtureHome = Environment.GetEnvironmentVariable("AISTACK_DOCTOR_HOME") is { Length: > 0 } h
                                   && !DoctorFS.CoversRealHome(h) ? Path.GetFullPath(h) : null;
    Timer? timer;
    bool running;

    public string Folder { get; }
    string Home => fixtureHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    bool TestRoot => fixtureHome != null;
    string JournalPath => Path.Combine(Folder, "journal.jsonl");
    string CheckupsPath => Path.Combine(Folder, "Checkups");
    string SettingsPath => Path.Combine(Folder, "settings.json");

    public DoctorService(Func<JsonObject, Task> emit)
    {
        this.emit = emit;
        Folder = Environment.GetEnvironmentVariable("AISTACK_DOCTOR_DATA") is { Length: > 0 } d ? Path.GetFullPath(d)
                 : Path.Combine(Paths.Root, "data", "Doctor");
    }

    public void Start() => timer = new Timer(_ => Tick(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));

    public DoctorSettings Settings()
    {
        try { return JsonSerializer.Deserialize<DoctorSettings>(File.ReadAllText(SettingsPath), DoctorFS.Json) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    void Save(DoctorSettings s)
    {
        Directory.CreateDirectory(Folder);
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, DoctorFS.Json));
        File.Move(tmp, SettingsPath, true);
    }

    void RequireWrites()
    {
        if (!OperatingSystem.IsWindows() && fixtureHome == null)
            throw new InvalidOperationException("Laptop Doctor only changes files on Windows (or in a fixture home)");
    }

    // ---- Schedule

    void Tick()
    {
        try
        {
            var s = Settings();
            if (!s.Enabled) return;
            var now = DateTime.Now;
            if (s.LastRun is not DateTime last) { s.LastRun = now; Save(s); return; }   // wait for the next slot
            if (!DoctorSchedule.IsDue(now, last.ToLocalTime(), s.Weekday, s.Hour) || !OnMainsPower() || IdleSeconds() < 300) return;
            _ = RunAsync(scheduled: true);
        }
        catch (Exception e) { Log.Error($"Doctor tick failed: {e.Message}"); }
    }

    public Task<DoctorCheckup.Result?> RunAsync(bool scheduled)
    {
        RequireWrites();
        lock (gate)
        {
            if (running) return Task.FromResult<DoctorCheckup.Result?>(null);
            running = true;
        }
        var s = Settings();
        s.LastRun = DateTime.Now;
        Save(s);
        return Task.Run(async () =>
        {
            try
            {
                var opts = new DoctorCheckupOptions
                {
                    Thresholds = new(s.Thresholds),
                    ArchiveFolders = s.Archive ? s.ArchiveFolders.Select(p => Path.IsPathRooted(p) ? p : Path.Combine(Home, p)).ToList() : new(),
                };
                if (!s.EvictCloud) opts.Tier1.Remove(DoctorAction.EvictCloud);
                if (!s.Compress) opts.Tier1.Remove(DoctorAction.Compress);
                if (!s.Archive) opts.Tier1.Remove(DoctorAction.ArchiveFolder);
                Directory.CreateDirectory(Folder);
                Log.Info("Laptop Doctor checkup starting");
                var r = DoctorCheckup.Run(Home, DoctorCatalogue.Load(null), opts, new DoctorJournal(JournalPath), CheckupsPath, TestRoot, DoctorFS.Iso());
                Log.Info($"Laptop Doctor checkup done: Tier 1 {r.Tier1.Done} done, {r.Tier1.FreedBytes} bytes; plan {r.PlanItems} items");
                var notify = false;
                var latest = Settings();
                if (r.PlanItems > 0 && DoctorSchedule.MayNotify(DateTime.Now, latest.LastNotified?.ToLocalTime()))
                {
                    latest.LastNotified = DateTime.Now;
                    Save(latest);
                    notify = scheduled;
                }
                await emit(new JsonObject
                {
                    ["event"] = "doctor-checkup", ["checkup"] = r.Checkup, ["planItems"] = r.PlanItems, ["planBytes"] = r.PlanBytes,
                    ["freedBytes"] = r.Tier1.FreedBytes, ["notify"] = notify,
                });
                return (DoctorCheckup.Result?)r;
            }
            catch (Exception e)
            {
                Log.Error($"Laptop Doctor checkup failed: {e}");
                await emit(new JsonObject { ["event"] = "doctor-checkup", ["error"] = e.Message });
                return null;
            }
            finally { lock (gate) running = false; }
        });
    }

    [StructLayout(LayoutKind.Sequential)] struct PowerStatus { public byte AC, Battery, Percent, Saver; public int Life, Full; }
    [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size, Time; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus s);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput i);

    /// AC line status 1 = on mains; 255 (unknown, e.g. a desktop) counts as mains.
    static bool OnMainsPower() => !OperatingSystem.IsWindows() || !GetSystemPowerStatus(out var s) || s.AC != 0;

    static double IdleSeconds()
    {
        if (!OperatingSystem.IsWindows()) return double.MaxValue;
        var i = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref i) ? unchecked((uint)Environment.TickCount - i.Time) / 1000.0 : double.MaxValue;
    }

    // ---- Requests from doctor.html

    static string Str(JsonObject m, string key) => m[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, DoctorFS.Json);

    string CheckupFolder(string id)
    {
        var name = id.Replace(":", "-");
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..")) throw new InvalidOperationException("bad checkup id");
        return Path.Combine(CheckupsPath, name);
    }

    T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), DoctorFS.Json)!;

    public async Task<JsonObject> DispatchAsync(string type, JsonObject m)
    {
        switch (type)
        {
            case "doctor.settings":
                return new JsonObject { ["settings"] = Node(Settings()), ["running"] = running, ["writable"] = OperatingSystem.IsWindows() || TestRoot };

            case "doctor.settings.set":
            {
                var s = Settings();
                var change = JsonSerializer.Deserialize<DoctorSettings>(m["settings"]?.ToJsonString() ?? "{}", DoctorFS.Json) ?? new();
                if (change.Enabled && !s.Enabled) s.LastRun = DateTime.Now;   // first checkup waits for the next slot
                s.Enabled = change.Enabled;
                s.Weekday = Math.Clamp(change.Weekday, 1, 7);
                s.Hour = Math.Clamp(change.Hour, 0, 23);
                s.EvictCloud = change.EvictCloud; s.Compress = change.Compress; s.Archive = change.Archive;
                s.ArchiveFolders = change.ArchiveFolders.Where(f => f.Trim().Length > 0).Distinct().ToList();
                s.Thresholds = change.Thresholds.Where(kv => DoctorCheckup.UserDayKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => Math.Max(kv.Value, 7));
                s.ClaudeSummary = change.ClaudeSummary;
                Save(s);
                return new JsonObject { ["settings"] = Node(s) };
            }

            case "doctor.checkNow":
                var started = RunAsync(scheduled: false);
                if (m["wait"] is JsonValue w && w.GetValue<bool>()) return new JsonObject { ["result"] = Node(await started) };
                return new JsonObject();

            case "doctor.checkups":
            {
                var list = new JsonArray();
                if (Directory.Exists(CheckupsPath))
                    foreach (var dir in Directory.GetDirectories(CheckupsPath).OrderByDescending(d => d, StringComparer.Ordinal))
                        if (File.Exists(Path.Combine(dir, "outcome.json")))
                            list.Add(JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "outcome.json"))));
                return new JsonObject { ["checkups"] = list };
            }

            case "doctor.get":
            {
                var folder = CheckupFolder(Str(m, "checkup"));
                var plan = Read<TreatmentPlan>(Path.Combine(folder, "plan.json"));
                var report = Read<DoctorReport>(Path.Combine(folder, "report.json"));
                var decided = DoctorDesk.DecidedIds(plan, DoctorJournal.Read(JournalPath));
                var catalogue = DoctorCatalogue.Load(null);
                var summaryPath = Path.Combine(folder, "summary.txt");
                return new JsonObject
                {
                    ["plan"] = Node(plan),
                    ["outcome"] = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "outcome.json"))),
                    ["decided"] = Node(decided.ToList()),
                    ["posture"] = Node(report.Posture),
                    ["skipped"] = report.Skipped.Count,
                    ["malware"] = report.Findings.Count(f => f.Flag == "malware" || f.Category == "malware"),
                    ["startup"] = report.Findings.Count(f => f.Category == "persistence"),
                    ["summary"] = File.Exists(summaryPath) ? File.ReadAllText(summaryPath) : null,
                    ["ruleable"] = Node(catalogue.Categories.Where(c => DoctorRuleBook.Allowed(c.Id, catalogue)).Select(c => c.Id).ToList()),
                };
            }

            case "doctor.summary":
            {
                if (!Settings().ClaudeSummary) throw new InvalidOperationException("Turn on the Claude summary in Laptop Doctor settings first");
                var folder = CheckupFolder(Str(m, "checkup"));
                var text = await DoctorSummary.WriteAsync(Read<DoctorReport>(Path.Combine(folder, "report.json")));
                File.WriteAllText(Path.Combine(folder, "summary.txt"), text);
                return new JsonObject { ["summary"] = text };
            }

            case "doctor.decide":
            {
                RequireWrites();
                var plan = Read<TreatmentPlan>(Path.Combine(CheckupFolder(Str(m, "checkup")), "plan.json"));
                // The page always names the items; a request without them must not mean "everything".
                if (m["items"] is not JsonArray a) throw new InvalidOperationException("doctor.decide needs the items");
                var ids = a.Select(x => x!.GetValue<string>()).ToHashSet();
                var res = DoctorDesk.Decide(Str(m, "decision"), ids, plan, Home, DoctorCatalogue.Load(null), new DoctorJournal(JournalPath), CheckupsPath, TestRoot);
                return new JsonObject { ["result"] = Node(res) };
            }

            case "doctor.rules":
            {
                var catalogue = DoctorCatalogue.Load(null);
                var rules = new JsonArray();
                foreach (var r in DoctorRuleBook.Load(Path.Combine(CheckupsPath, "rules.json"), catalogue).Rules)
                    rules.Add(new JsonObject { ["id"] = r.Id, ["category"] = r.Category, ["created"] = r.Created,
                                               ["label"] = catalogue.Categories.FirstOrDefault(c => c.Id == r.Category)?.Label ?? r.Category });
                return new JsonObject { ["rules"] = rules };
            }

            case "doctor.ruleRemove":
            {
                RequireWrites();
                var path = Path.Combine(CheckupsPath, "rules.json");
                var book = DoctorRuleBook.Load(path, DoctorCatalogue.Load(null));
                if (!book.Remove(Str(m, "rule"))) throw new InvalidOperationException("no such rule");
                Directory.CreateDirectory(CheckupsPath);
                File.WriteAllText(path, JsonSerializer.Serialize(book, DoctorFS.Json));
                return new JsonObject { ["removed"] = Str(m, "rule") };
            }

            case "doctor.history":
                return new JsonObject { ["history"] = Node(DoctorDesk.History(DoctorJournal.Read(JournalPath))) };

            case "doctor.undo":
            {
                RequireWrites();
                var journal = new DoctorJournal(JournalPath);
                var records = DoctorJournal.Read(JournalPath);
                var entry = Str(m, "entry");
                var checkup = records.FirstOrDefault(r => r.Id == entry)?.Checkup ?? throw new InvalidOperationException("no such entry");
                var o = new DoctorExecutor(Home, DoctorCatalogue.Load(null), journal, checkup, TestRoot).Undo(records, entry);
                return new JsonObject { ["result"] = Node(o) };
            }

            case "doctor.undoCheckup":
                RequireWrites();
                return new JsonObject { ["result"] = Node(DoctorDesk.Undo(Str(m, "checkup"), Home, DoctorCatalogue.Load(null), new DoctorJournal(JournalPath), TestRoot)) };

            case "doctor.open":
            {
                var path = Str(m, "checkup") is { Length: > 0 } c ? CheckupFolder(c) : Folder;
                if (OperatingSystem.IsWindows() && Directory.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return new JsonObject();
            }

            default:
                throw new Exception($"Unknown request '{type}'");
        }
    }
}
