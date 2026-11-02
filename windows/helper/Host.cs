using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIStack;

/// Chrome native messaging: 4-byte little-endian length + UTF-8 JSON, both directions.
sealed class NativeHost
{
    readonly Stream input = Console.OpenStandardInput();
    readonly Stream output = Console.OpenStandardOutput();
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly MeetingStore store = new();
    readonly MeetingRecorder recorder;
    readonly Doctor.DoctorService doctor;

    public NativeHost()
    {
        recorder = new MeetingRecorder(store, Emit);
        doctor = new Doctor.DoctorService(Emit);
    }

    public async Task RunAsync()
    {
        Log.Info($"Host started (pid {Environment.ProcessId})");
        _ = Task.Run(recorder.RecoverInterruptedAsync);
        doctor.Start();
        while (true)
        {
            var message = await ReadAsync();
            if (message == null) break;
            _ = Task.Run(() => HandleAsync(message));
        }
        Log.Info("Chrome closed the connection");
        recorder.SaveLive();
        ClaudeCli.Shared.Shutdown();
    }

    async Task<JsonObject?> ReadAsync()
    {
        var header = new byte[4];
        if (!await ReadExactAsync(header)) return null;
        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > 64 * 1024 * 1024) return null;
        var body = new byte[length];
        if (!await ReadExactAsync(body)) return null;
        try { return JsonNode.Parse(body) as JsonObject; }
        catch (JsonException ex) { Log.Error($"Bad message: {ex.Message}"); return new JsonObject(); }
    }

    async Task<bool> ReadExactAsync(byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(offset));
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    public async Task Emit(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        if (bytes.Length > 1_000_000)
        {
            Log.Error($"Dropping {bytes.Length}-byte message (Chrome limit is 1 MB)");
            return;
        }
        await writeLock.WaitAsync();
        try
        {
            await output.WriteAsync(BitConverter.GetBytes(bytes.Length));
            await output.WriteAsync(bytes);
            await output.FlushAsync();
        }
        catch (IOException) { }
        finally { writeLock.Release(); }
    }

    async Task HandleAsync(JsonObject message)
    {
        var id = message["id"]?.DeepClone();
        var type = message["type"]?.GetValue<string>() ?? "";
        JsonObject reply;
        try
        {
            reply = await DispatchAsync(type, message);
            reply["ok"] = true;
        }
        catch (Exception ex)
        {
            if (ex is not ClaudeException) Log.Error($"{type} failed: {ex}");
            reply = new JsonObject { ["ok"] = false, ["error"] = ex.Message };
        }
        reply["id"] = id;
        await Emit(reply);
    }

    static string Str(JsonObject m, string key, string fallback = "") =>
        m[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    static bool Bool(JsonObject m, string key) => m[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    static ClaudeConfig ConfigFrom(JsonObject m) => new(
        Str(m, "model", "haiku"), Str(m, "system"),
        m["effort"] is JsonValue e && e.TryGetValue<string>(out var effort) ? effort : null,
        Bool(m, "noThinking"), Bool(m, "keepWarm"));

    async Task<JsonObject> DispatchAsync(string type, JsonObject m)
    {
        if (type.StartsWith("doctor.")) return await doctor.DispatchAsync(type, m);
        switch (type)
        {
            case "ping":
                return new JsonObject
                {
                    ["version"] = "1.0.0",
                    ["platform"] = OperatingSystem.IsWindows() ? "windows" : "other",
                    ["claudePath"] = ClaudeCli.Shared.FindBinary(),
                    ["modelReady"] = WhisperEngine.ModelReady,
                    ["live"] = recorder.LiveSummary(),
                };

            case "auth":
                var (ok, summary) = await ClaudeCli.Shared.AuthStatusAsync();
                return new JsonObject { ["loggedIn"] = ok, ["summary"] = summary };

            case "claude":
                var timeout = TimeSpan.FromSeconds(m["timeoutSec"]?.GetValue<double>() ?? 90);
                var text = await ClaudeCli.Shared.CompleteAsync(Str(m, "prompt"), ConfigFrom(m), timeout);
                return new JsonObject { ["text"] = text };

            case "prewarm":
                ClaudeCli.Shared.Prewarm(ConfigFrom(m) with { KeepWarm = true });
                return new JsonObject();

            case "meeting.start":
                var started = recorder.Start(Str(m, "title", "Meeting"), Bool(m, "titleIsAuto"), Str(m, "app", "") is var app && app.Length > 0 ? app : null,
                    Str(m, "url"), Str(m, "language", "auto"), Str(m, "model", "opus"));
                return new JsonObject { ["meeting"] = Summary(started) };

            case "meeting.stop":
                _ = recorder.StopAsync();
                return new JsonObject();

            case "meeting.live":
                return new JsonObject { ["meeting"] = recorder.Live is { } live ? Full(live) : null };

            case "meeting.list":
                var list = new JsonArray();
                foreach (var meeting in store.All()) list.Add(Summary(meeting));
                return new JsonObject { ["meetings"] = list };

            case "meeting.get":
                var found = recorder.Live?.Id == Str(m, "meetingId") ? recorder.Live : store.Get(Str(m, "meetingId"));
                return new JsonObject { ["meeting"] = found == null ? null : Full(found) };

            case "meeting.update":
                recorder.Update(Str(m, "meetingId"), meeting =>
                {
                    if (m["title"] is JsonValue t) { meeting.Title = t.GetValue<string>(); meeting.TitleIsAuto = false; }
                    if (m["userNotes"] is JsonValue u) meeting.UserNotes = u.GetValue<string>();
                    if (m["notes"] is JsonValue n) meeting.Notes = n.GetValue<string>();
                });
                return new JsonObject();

            case "meeting.delete":
                store.Delete(Str(m, "meetingId"));
                return new JsonObject();

            case "meeting.regenerate":
                _ = recorder.GenerateNotesAsync(Str(m, "meetingId"), m["model"] is JsonValue mv ? mv.GetValue<string>() : null);
                return new JsonObject();

            case "meeting.ask":
                var target = store.Get(Str(m, "meetingId")) ?? throw new Exception("Meeting not found");
                var answer = await NotesGenerator.AnswerAsync(target, Str(m, "question"));
                return new JsonObject { ["answer"] = answer };

            case "open":
                var what = Str(m, "what");
                var path = what == "folder" ? Paths.NotesFolder : store.Get(Str(m, "meetingId"))?.ExportPath;
                if (path != null) OpenInFileManager(path, select: what != "folder");
                return new JsonObject();

            default:
                throw new Exception($"Unknown request '{type}'");
        }
    }

    static void OpenInFileManager(string path, bool select)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(select ? Path.GetDirectoryName(path)! : path);
                Process.Start(new ProcessStartInfo("explorer.exe", select ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start("open", select ? new[] { "-R", path } : new[] { path });
            }
        }
        catch (Exception ex) { Log.Error($"open failed: {ex.Message}"); }
    }

    public static JsonObject Summary(Meeting m)
    {
        string? preview;
        lock (m) preview = m.Notes?.Split('\n').FirstOrDefault(l => l.Trim().Length > 0 && !l.StartsWith('#'))?.Trim();
        return new JsonObject
        {
            ["id"] = m.Id,
            ["title"] = m.Title,
            ["startedAt"] = m.StartedAt.ToString("o"),
            ["endedAt"] = m.EndedAt?.ToString("o"),
            ["app"] = m.App,
            ["status"] = m.Status,
            ["error"] = m.Error,
            ["preview"] = preview,
        };
    }

    public static JsonObject Full(Meeting m)
    {
        lock (m) return JsonSerializer.SerializeToNode(m, MeetingStore.Json)!.AsObject();
    }
}

/// Owns the live meeting (at most one) and notes generation.
sealed class MeetingRecorder(MeetingStore store, Func<JsonObject, Task> emit)
{
    readonly object gate = new();
    MeetingSession? session;
    Timer? autosave;

    public Meeting? Live
    {
        get { lock (gate) return session?.Meeting; }
    }

    public JsonObject? LiveSummary() => Live is { } live ? NativeHost.Summary(live) : null;

    public Meeting Start(string title, bool titleIsAuto, string? app, string url, string language, string model)
    {
        lock (gate)
        {
            if (session != null) return session.Meeting;
            var meeting = new Meeting
            {
                Title = title, TitleIsAuto = titleIsAuto, App = app, Url = url, Language = language, NotesModel = model,
            };
            store.Save(meeting);
            session = new MeetingSession(meeting, segment =>
            {
                lock (meeting) meeting.Segments.Add(segment);
                _ = emit(new JsonObject
                {
                    ["event"] = "segment",
                    ["meetingId"] = meeting.Id,
                    ["segment"] = JsonSerializer.SerializeToNode(segment, MeetingStore.Json),
                });
            });
            if (!WhisperEngine.ModelReady)
            {
                _ = Task.Run(async () =>
                {
                    try { await WhisperEngine.DownloadModelAsync(); } catch (Exception ex) { Log.Error($"Model download failed: {ex.Message}"); }
                });
            }
            session.Start();
            autosave = new Timer(_ => { if (Live is { } live) store.Save(live); }, null, 15000, 15000);
            Log.Info($"Recording started: {title} [{app}] lang={language}");
            _ = emit(new JsonObject { ["event"] = "meeting", ["meeting"] = NativeHost.Summary(meeting) });
            return meeting;
        }
    }

    public async Task StopAsync()
    {
        MeetingSession? current;
        lock (gate)
        {
            current = session;
            session = null;
            autosave?.Dispose();
            autosave = null;
        }
        if (current == null) return;
        var meeting = current.Meeting;
        meeting.EndedAt = DateTime.UtcNow;
        meeting.Status = "processing";
        store.Save(meeting);
        await emit(new JsonObject { ["event"] = "meeting", ["meeting"] = NativeHost.Summary(meeting) });
        Log.Info($"Recording stopped: {meeting.Title}");
        await current.StopAsync();
        store.Save(meeting);
        await GenerateNotesAsync(meeting.Id, null);
    }

    public void Update(string id, Action<Meeting> change)
    {
        var meeting = Live?.Id == id ? Live : store.Get(id);
        if (meeting == null) throw new Exception("Meeting not found");
        lock (meeting) change(meeting);
        store.Save(meeting);
        if (meeting.Status == "ready") store.ExportMarkdown(meeting);
    }

    public async Task GenerateNotesAsync(string id, string? model)
    {
        var meeting = store.Get(id);
        if (meeting == null) return;
        if (model != null) meeting.NotesModel = model;
        meeting.Status = "processing";
        meeting.Error = null;
        store.Save(meeting);
        await emit(new JsonObject { ["event"] = "meeting", ["meeting"] = NativeHost.Summary(meeting) });

        if (meeting.WordCount < 8 && meeting.UserNotes.Trim().Length == 0)
        {
            meeting.Notes = "_Not enough speech was captured to write notes._";
            meeting.Status = "ready";
        }
        else
        {
            try
            {
                var (title, body) = await NotesGenerator.GenerateAsync(meeting);
                lock (meeting)
                {
                    if (meeting.TitleIsAuto && title != null) { meeting.Title = title; meeting.TitleIsAuto = false; }
                    meeting.Notes = body;
                    meeting.Status = "ready";
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Notes failed: {ex.Message}");
                meeting.Status = "failed";
                meeting.Error = ex.Message;
            }
        }
        store.Save(meeting);
        if (meeting.Status == "ready") store.ExportMarkdown(meeting);
        await emit(new JsonObject { ["event"] = "meeting", ["meeting"] = NativeHost.Summary(meeting) });
    }

    public void SaveLive()
    {
        if (Live is { } live) store.Save(live);
    }

    /// Meetings cut off by Chrome quitting mid-call: close them and write notes from what was captured.
    public async Task RecoverInterruptedAsync()
    {
        foreach (var meeting in store.All().Where(m => m.Status is "recording" or "processing"))
        {
            if (Live?.Id == meeting.Id) continue;
            meeting.EndedAt ??= meeting.Segments.Count > 0 ? meeting.Segments.Max(s => s.End) : meeting.StartedAt;
            Log.Info($"Recovering interrupted meeting {meeting.Title}");
            await GenerateNotesAsync(meeting.Id, null);
        }
    }
}
