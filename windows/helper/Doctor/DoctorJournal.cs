using System.Text;
using System.Text.Json;

namespace AIStack.Doctor;

/// One JSONL journal line; same fields as JournalRecord in Sources/AIStack/Doctor/Journal.swift.
public class JournalRecord
{
    public int V { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Checkup { get; set; } = "";
    public string Ts { get; set; } = DoctorFS.Iso();
    public string Phase { get; set; } = "";   // intent|done|failed|skipped|refused|approval|undo-intent|undo-done|undo-failed
    public DoctorAction Action { get; set; }
    public int Tier { get; set; }
    public string? PlanItem { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Temp { get; set; }
    public FileState? Before { get; set; }
    public FileState? After { get; set; }
    public Fingerprint? Fingerprint { get; set; }
    public string? By { get; set; }
    public string? Reason { get; set; }
    public long? FreedBytes { get; set; }

    public JournalRecord Copy() => (JournalRecord)MemberwiseClone();
}

/// `blocks` is allocated bytes / 512 (as on macOS); `mode` holds the Windows file attributes.
public record FileState(long Size, long Blocks, double Mtime, double Birth, int Mode, string? Sha256);

/// Append-only JSONL action journal. Every record is flushed to disk before the step it describes starts.
public class DoctorJournal
{
    public readonly string Path;

    public DoctorJournal(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
    }

    public void Append(JournalRecord r)
    {
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r, DoctorFS.JsonLine) + "\n");
        using var f = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
        f.Write(line);
        f.Flush(flushToDisk: true);
    }

    public static List<JournalRecord> Read(string path)
    {
        var out_ = new List<JournalRecord>();
        if (!File.Exists(path)) return out_;
        foreach (var line in File.ReadAllLines(path))
        {
            // A torn last line (power loss mid-write) is ignored.
            try { if (JsonSerializer.Deserialize<JournalRecord>(line, DoctorFS.Json) is { } r) out_.Add(r); } catch { }
        }
        return out_;
    }

    /// Tier 2 actions that completed without an earlier approval record for the same plan item.
    public static List<JournalRecord> UnapprovedDestructive(List<JournalRecord> records)
    {
        var approved = new HashSet<string>();
        var bad = new List<JournalRecord>();
        foreach (var r in records)
        {
            if (r.Phase == "approval" && r.PlanItem != null) approved.Add(r.PlanItem);
            if (r.Phase == "done" && (r.Tier >= (int)DoctorTier.Plan || r.Action.Tier() == DoctorTier.Plan))
                if (r.PlanItem == null || !approved.Contains(r.PlanItem)) bad.Add(r);
        }
        return bad;
    }

    public static string NewId() => Guid.NewGuid().ToString().ToLowerInvariant();
}
