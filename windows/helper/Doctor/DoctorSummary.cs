using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIStack.Doctor;

/// The one optional Claude call per checkup (same facts as Sources/AIStack/Doctor/Summary.swift). Only `Facts`
/// decides what leaves the machine: category totals, security settings, and for startup items and processes their
/// name, signature flag and program path. Never the user's file or folder names: findings are sent as counts and
/// bytes, malware as a count, skipped notes as a count, and a program inside the profile folder as `~\…\<name>`.
public static class DoctorSummary
{
    public const string SystemPrompt =
        "You write the summary of a laptop checkup for a non-technical user. Use plain English and at most " +
        "120 words, then one short line per startup item or process listed, saying what it most likely is and " +
        "whether it looks legitimate. Say \"unknown\" rather than guess. Do not invent findings, sizes or settings.";

    public record Group(string Category, DoctorAction Action, int Tier, int Count, long Bytes);
    public record Item(string Name, string? Flag, string? Executable, string? CpuPercent);
    public record SummaryFacts(Dictionary<string, string> Security, List<Group> Findings, List<Item> StartupItems,
                               List<Item> Processes, int MalwareDetections, int AreasNotScanned);

    public static SummaryFacts Facts(DoctorReport r)
    {
        var groups = new Dictionary<string, Group>();
        List<Item> startup = new(), procs = new();
        int malware = 0;
        foreach (var f in r.Findings)
        {
            if (f.Category == "malware" || f.Flag == "malware") { malware++; continue; }
            var exe = f.Executable is string e ? Redact(e, r.Root) : null;
            if (f.Category == "persistence") { startup.Add(new Item(f.ProcessName ?? "unnamed", f.Flag, exe, null)); continue; }
            if (f.ProcessName is string name) { procs.Add(new Item(name, null, exe, f.Reason.Split('%')[0])); continue; }
            var g = groups.GetValueOrDefault(f.Category) ?? new Group(f.Category, f.Action, f.Tier, 0, 0);
            groups[f.Category] = g with { Count = g.Count + 1, Bytes = g.Bytes + f.Bytes };
        }
        return new SummaryFacts(r.Posture, groups.Values.OrderByDescending(g => g.Bytes).ToList(), startup, procs, malware, r.Skipped.Count);
    }

    /// System and program paths are kept; under the profile folder only the program's own name is.
    public static string Redact(string path, string home)
    {
        // Plain string rules rather than Path.* so a Windows report reads the same on any machine.
        static bool IsSep(char c) => c is '\\' or '/';
        var h = home.TrimEnd('\\', '/');
        var rooted = path.Length > 0 && IsSep(path[0]) || path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsSep(path[2]);
        var inHome = path.StartsWith(h, StringComparison.OrdinalIgnoreCase) && (path.Length == h.Length || IsSep(path[h.Length]));
        return rooted && !inHome ? path : "~\\…\\" + path.TrimEnd('\\', '/').Split('\\', '/')[^1];
    }

    static readonly JsonSerializerOptions PromptJson = new(DoctorFS.Json)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Prompt(DoctorReport r) =>
        "Checkup facts (bytes are exact; tier 1 = done automatically and reversibly, tier 2 = waiting for approval):\n"
        + JsonSerializer.Serialize(Facts(r), PromptJson);

    public static Task<string> WriteAsync(DoctorReport r, string model = "haiku") =>
        ClaudeCli.Shared.CompleteAsync(Prompt(r), new ClaudeConfig(model, SystemPrompt, Effort: "low"), TimeSpan.FromSeconds(120));
}
