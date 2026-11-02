using System.Text.Json;

namespace AIStack.Doctor;

/// Headless Laptop Doctor for the safety suite and CI; same verbs and JSON as `AIStack --doctor` on macOS:
///
///     AIStackHelper --doctor scan    [--root <home>] [--dry-run] [--archive <folder>]… --out report.json
///     AIStackHelper --doctor tier1   --root <home> --journal <file> [--archive <folder>]…
///     AIStackHelper --doctor plan    [--root <home>] [--dry-run] --out plan.json
///     AIStackHelper --doctor approve --plan plan.json --journal <file> [--items id,id | --all] [--by user|rule:<id>]
///     AIStackHelper --doctor execute --root <home> --plan plan.json --journal <file>
///     AIStackHelper --doctor undo    --root <home> --journal <file> [--entry <id>]
///     AIStackHelper --doctor recover --root <home> --journal <file>
///     AIStackHelper --doctor verify-journal <file>…
///
/// Without --root the real profile is used, and only `scan`, `plan` and `verify-journal` are allowed.
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
        DoctorCloud.ExposePlaceholders();
        var checkup = One("checkup") ?? DoctorFS.Iso();
        var archive = (values.GetValueOrDefault("archive") ?? new()).Select(p => Path.IsPathRooted(p) ? p : Path.Combine(home, p)).ToList();
        if (verb is "tier1" or "approve" or "execute" or "undo" or "recover" && root == null)
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
            case "approve":
            {
                var plan = LoadPlan();
                var ids = (One("items") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                var items = plan.Groups.SelectMany(g => g.Items).Where(i => flags.Contains("all") || ids.Contains(i.Id)).ToList();
                Executor(Journal()).Approve(items, One("by") ?? "user");
                Console.WriteLine($"approved {items.Count}");
                break;
            }
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

    static void Emit<T>(T v, string? outPath)
    {
        var json = JsonSerializer.Serialize(v, DoctorFS.Json);
        if (outPath != null) File.WriteAllText(outPath, json); else Console.WriteLine(json);
    }
}
