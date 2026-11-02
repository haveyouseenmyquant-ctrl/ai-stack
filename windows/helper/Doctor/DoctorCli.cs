using System.Text.Json;

namespace AIStack.Doctor;

/// Headless Laptop Doctor for the safety suite and CI; same verbs and JSON as `AIStack --doctor` on macOS:
///
///     AIStackHelper --doctor scan    [--root <home>] [--dry-run] [--archive <folder>]… --out report.json
///     AIStackHelper --doctor tier1   --root <home> --journal <file> [--archive <folder>]…
///     AIStackHelper --doctor plan    [--root <home>] [--dry-run] --out plan.json
///     AIStackHelper --doctor approve --plan plan.json --journal <file> [--items id,id | --all] [--by user|rule:<id>]
///     AIStackHelper --doctor decline --plan plan.json --journal <file> [--items id,id | --all]
///     AIStackHelper --doctor rule-add --root <home> --dir <checkups dir> --category <id>   ("always do this"; ruleable kinds only)
///     AIStackHelper --doctor decide  --root <home> --journal <file> --dir <checkups dir> --plan plan.json --decision approve|decline|always
///                                    [--items id,id | --all]   (what the report's buttons do)
///     AIStackHelper --doctor history --journal <file>
///     AIStackHelper --doctor undo-checkup --root <home> --journal <file> --checkup <id>
///     AIStackHelper --doctor execute --root <home> --plan plan.json --journal <file>
///     AIStackHelper --doctor undo    --root <home> --journal <file> [--entry <id>]
///     AIStackHelper --doctor recover --root <home> --journal <file>
///     AIStackHelper --doctor checkup --root <home> --journal <file> --dir <out> [--tier1 compress,evictCloud,archiveFolder,quarantine]
///                                    [--threshold coldDays=90]… [--archive <folder>]…   (what the scheduler runs)
///     AIStackHelper --doctor due     --now <iso> [--last <iso>] --weekday 1-7 --hour 0-23 [--notified <iso>]   (UTC)
///     AIStackHelper --doctor verify-journal <file>…
///     AIStackHelper --doctor summary-prompt --report report.json   (exactly what the Claude summary would send)
///     AIStackHelper --doctor summary --report report.json [--model haiku]
///
/// Without --root the real profile is used, and only `scan`, `plan`, `verify-journal` and the summary verbs are allowed.
public static class DoctorCli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("doctor: verb required"); return 2; }
        try { return Run(args[0], args[1..]); }
        catch (Exception e) { Console.Error.WriteLine($"doctor: {e.Message}"); return 2; }
    }

    static int Run(string verb, string[] a)
    {
        var values = new Dictionary<string, List<string>>();
        var flags = new HashSet<string>();
        var positional = new List<string>();
        for (int k = 0; k < a.Length;)
        {
            if (a[k].StartsWith("--"))
            {
                var key = a[k][2..];
                if (k + 1 < a.Length && !a[k + 1].StartsWith("--")) { (values.TryGetValue(key, out var l) ? l : values[key] = new()).Add(a[k + 1]); k += 2; }
                else { flags.Add(key); k++; }
            }
            else positional.Add(a[k++]);
        }
        string? One(string k) => values.TryGetValue(k, out var l) ? l[^1] : null;

        var root = One("root") is string r ? Path.GetFullPath(r) : null;
        var home = root ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var catalogue = DoctorCatalogue.Load(One("catalogue"));
        var checkup = One("checkup") ?? DoctorFS.Iso();
        var archive = (values.GetValueOrDefault("archive") ?? new()).Select(p => Path.IsPathRooted(p) ? p : Path.Combine(home, p)).ToList();
        if (verb is "tier1" or "checkup" or "decide" or "undo-checkup" or "approve" or "decline" or "rule-add" or "execute" or "undo" or "recover" && root == null)
            throw new InvalidOperationException($"{verb} needs --root <fixture home>; real-profile writes are not enabled");
        if (root != null && !Directory.Exists(home)) throw new InvalidOperationException($"no such root {home}");

        (List<Finding>, List<string>) Scan()
        {
            var s = new DoctorScanner(home, catalogue, archive, root != null);
            var f = s.Scan();
            return (f, s.Skipped);
        }
        DoctorJournal Journal() => new(One("journal") ?? throw new InvalidOperationException("--journal <file> required"));
        DoctorExecutor Executor(DoctorJournal j) => new(home, catalogue, j, checkup, root != null);
        TreatmentPlan LoadPlan() => JsonSerializer.Deserialize<TreatmentPlan>(
            File.ReadAllText(One("plan") ?? throw new InvalidOperationException("--plan <file> required")), DoctorFS.Json)!;

        switch (verb)
        {
            case "scan":
                var (findings, skipped) = Scan();
                Emit(new DoctorReport(1, DoctorFS.Iso(), home, true, findings, skipped, root == null ? DoctorScanner.Posture() : new()), One("out"));
                break;
            case "plan":
                Emit(MakePlan(Scan().Item1, catalogue, checkup, home), One("out"));
                break;
            case "tier1":
            {
                var j = Journal(); var ex = Executor(j);
                ex.Recover(DoctorJournal.Read(j.Path));
                Emit(ex.RunTier1(Scan().Item1), null);
                break;
            }
            case "checkup":
            {
                var dir = One("dir") ?? throw new InvalidOperationException("--dir <folder> required");
                var opts = new DoctorCheckupOptions { ArchiveFolders = archive };
                if (One("tier1") is string t)
                    opts.Tier1 = t.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => Enum.TryParse<DoctorAction>(x, true, out var act) ? act : (DoctorAction?)null)
                        .Where(x => x != null).Select(x => x!.Value).ToHashSet();
                if (flags.Contains("tier1")) opts.Tier1 = new();   // `--tier1` with nothing after it: every switch off
                foreach (var kv in values.GetValueOrDefault("threshold") ?? new())
                    if (kv.Split('=') is [var k, var v] && int.TryParse(v, out var n)) opts.Thresholds[k] = n;
                Emit(DoctorCheckup.Run(home, catalogue, opts, Journal(), dir, true, checkup), null);
                break;
            }
            case "due":
            {
                var now = DoctorSchedule.ParseUtc(One("now")) ?? throw new InvalidOperationException("--now required");
                if (!int.TryParse(One("weekday"), out var wd) || !int.TryParse(One("hour"), out var h))
                    throw new InvalidOperationException("--weekday and --hour required");
                var due = DoctorSchedule.IsDue(now, DoctorSchedule.ParseUtc(One("last")), wd, h);
                var notify = DoctorSchedule.MayNotify(now, DoctorSchedule.ParseUtc(One("notified")));
                Console.WriteLine($"{{\"due\": {(due ? "true" : "false")}, \"mayNotify\": {(notify ? "true" : "false")}}}");
                break;
            }
            case "approve":
            {
                var plan = LoadPlan();
                var ids = (One("items") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                var items = plan.Groups.SelectMany(g => g.Items).Where(i => flags.Contains("all") || ids.Contains(i.Id)).ToList();
                Executor(Journal()).Approve(items, One("by") ?? "user");
                Console.WriteLine($"approved {items.Count}");
                break;
            }
            case "decline":
            {
                var plan = LoadPlan();
                var ids = (One("items") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                var items = plan.Groups.SelectMany(g => g.Items).Where(i => flags.Contains("all") || ids.Contains(i.Id)).ToList();
                Executor(Journal()).Decline(items, "user");
                Console.WriteLine($"declined {items.Count}");
                break;
            }
            case "rule-add":
            {
                var dir = One("dir") ?? throw new InvalidOperationException("--dir required");
                var cat = One("category") ?? throw new InvalidOperationException("--category required");
                var book = DoctorRuleBook.Load(Path.Combine(dir, "rules.json"), catalogue);
                var rule = book.Add(cat, catalogue);
                Directory.CreateDirectory(dir);
                Emit(book, Path.Combine(dir, "rules.json"));
                Emit(rule, null);
                break;
            }
            case "decide":
            {
                var dir = One("dir") ?? throw new InvalidOperationException("--dir required");
                var ids = flags.Contains("all") ? null : (One("items") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                Emit(DoctorDesk.Decide(One("decision") ?? "", ids, LoadPlan(), home, catalogue, Journal(), dir, true), null);
                break;
            }
            case "history":
                Emit(DoctorDesk.History(DoctorJournal.Read(Journal().Path)), null);
                break;
            case "undo-checkup":
                Emit(DoctorDesk.Undo(One("checkup") ?? throw new InvalidOperationException("--checkup required"), home, catalogue, Journal(), true), null);
                break;
            case "execute":
            {
                var j = Journal();
                Emit(Executor(j).Execute(LoadPlan(), DoctorJournal.Read(j.Path)), null);
                break;
            }
            case "undo":
            {
                var j = Journal();
                Emit(Executor(j).Undo(DoctorJournal.Read(j.Path), One("entry")), null);
                break;
            }
            case "recover":
            {
                var j = Journal();
                Console.WriteLine($"recovered {Executor(j).Recover(DoctorJournal.Read(j.Path))}");
                break;
            }
            case "verify-journal":
                var bad = positional.Concat(values.GetValueOrDefault("journal") ?? new())
                    .SelectMany(f => DoctorJournal.UnapprovedDestructive(DoctorJournal.Read(f))).Count();
                Console.WriteLine($"{{\"unapprovedDestructive\": {bad}}}");
                return bad == 0 ? 0 : 1;
            case "summary-prompt":
            case "summary":
            {
                var report = JsonSerializer.Deserialize<DoctorReport>(
                    File.ReadAllText(One("report") ?? throw new InvalidOperationException("--report <file> required")), DoctorFS.Json)!;
                Console.WriteLine(verb == "summary" ? DoctorSummary.WriteAsync(report, One("model") ?? "haiku").GetAwaiter().GetResult()
                                                    : DoctorSummary.Prompt(report));
                break;
            }
            default:
                throw new InvalidOperationException($"unknown verb {verb}");
        }
        return 0;
    }

    public static TreatmentPlan MakePlan(List<Finding> findings, DoctorCatalogue catalogue, string checkup, string root)
    {
        var plan = new TreatmentPlan { Checkup = checkup, Root = root };
        foreach (var c in catalogue.Categories)
        {
            var items = findings.Where(f => f.Tier == (int)DoctorTier.Plan && f.Category == c.Id).ToList();
            if (items.Count == 0) continue;
            var label = c.Label.Replace("{count}", items.Count.ToString());
            foreach (var (k, v) in catalogue.Defaults) label = label.Replace("{" + k + "}", ((int)v).ToString());
            plan.Groups.Add(new PlanGroup { Category = c.Id, Action = c.Action, Label = label, TotalBytes = items.Sum(i => i.Bytes), Items = items });
        }
        return plan;
    }

    public static void Emit<T>(T v, string? outPath)
    {
        var json = JsonSerializer.Serialize(v, DoctorFS.Json);
        if (outPath != null) File.WriteAllText(outPath, json); else Console.WriteLine(json);
    }
}
