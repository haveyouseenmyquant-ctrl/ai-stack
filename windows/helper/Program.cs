using System.Text.Json;
using System.Text.Json.Nodes;
using AIStack;

// Chrome starts native hosts with the extension origin as first argument.
if (args.Length > 0 && (args[0].StartsWith("chrome-extension://") || args[0] == "--host"))
{
    await new NativeHost().RunAsync();
    return;
}

// Laptop Doctor's headless CLI (safety suite and CI); prints JSON only, so it runs before console logging is set up.
if (args.FirstOrDefault() == "--doctor")
{
    Environment.Exit(AIStack.Doctor.DoctorCli.Run(args[1..]));
}

Log.EchoToConsole = true;
switch (args.FirstOrDefault())
{
    case "--register-test":
        Installer.RegisterForChromeDir(args[1]);
        break;
    case "--uninstall":
        Installer.Uninstall();
        break;
    case "--selftest":
        await SelfTest.RunAsync(args.Skip(1).ToArray());
        break;
    default:
        await Installer.RunAsync();
        break;
}

static class SelfTest
{
    public static async Task RunAsync(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "claude":
                var started = DateTime.UtcNow;
                var reply = await ClaudeCli.Shared.CompleteAsync("Reply with exactly: OK",
                    new ClaudeConfig("haiku", "You are a connectivity test.", "low", NoThinking: true), TimeSpan.FromSeconds(60));
                Console.WriteLine($"reply={reply} in {(DateTime.UtcNow - started).TotalSeconds:F1}s");
                Console.WriteLine($"auth={await ClaudeCli.Shared.AuthStatusAsync()}");
                break;

            case "meeting":
                // selftest meeting <me.wav> <them.wav>: full pipeline with files instead of WASAPI.
                Environment.SetEnvironmentVariable("AISTACK_FAKE_AUDIO_ME", args[1]);
                Environment.SetEnvironmentVariable("AISTACK_FAKE_AUDIO_THEM", args[2]);
                await WhisperEngine.DownloadModelAsync(p => Console.Error.Write($"\rmodel {p:P0}  "));
                var store = new MeetingStore();
                var done = new TaskCompletionSource();
                var recorder = new MeetingRecorder(store, message =>
                {
                    Console.WriteLine("event: " + message.ToJsonString());
                    if (message["event"]?.GetValue<string>() == "meeting" && message["meeting"]?["status"]?.GetValue<string>() is "ready" or "failed")
                        done.TrySetResult();
                    return Task.CompletedTask;
                });
                var meeting = recorder.Start("Google Meet call", true, "Google Meet", "https://meet.google.com/abc-defg-hij", args.Length > 3 ? args[3] : "auto", "opus");
                await Task.Delay(TimeSpan.FromSeconds(double.Parse(Environment.GetEnvironmentVariable("AISTACK_TEST_SECONDS") ?? "12")));
                await recorder.StopAsync();
                await done.Task.WaitAsync(TimeSpan.FromMinutes(5));
                var final = store.Get(meeting.Id)!;
                Console.WriteLine($"\nTITLE: {final.Title}\nTRANSCRIPT:\n{final.TranscriptText()}\n\nNOTES:\n{final.Notes}\nEXPORT: {final.ExportPath}");
                store.Delete(final.Id);
                if (final.ExportPath != null) File.Delete(final.ExportPath);
                break;

            default:
                Console.WriteLine("selftest claude | meeting <me.wav> <them.wav> [language]");
                break;
        }
    }
}
