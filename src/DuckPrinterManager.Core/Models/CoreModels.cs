using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuckPrinterManager.Core.Models;

public sealed class HostParseResult
{
    public List<string> Valid { get; set; } = [];
    public List<string> Invalid { get; set; } = [];
    public int Duplicates { get; set; }
}

public static class TaskActions
{
    public const string Deploy = "deploy";
    public const string Remove = "remove";
}

public static class TaskStatus
{
    public const string Waiting = "waiting";
    public const string Running = "running";
    public const string Retry = "retry";
    public const string Paused = "paused";
    public const string Done = "done";
    public const string Expired = "expired";

    public static string Display(string s) => s switch
    {
        Waiting => "In attesa",
        Running => "In esecuzione",
        Retry => "Riprova",
        Paused => "In pausa",
        Done => "Completato",
        Expired => "Scaduto",
        _ => s,
    };
}

public sealed class NewTask
{
    public string Host { get; set; } = "";
    public string Action { get; set; } = "";
    public string Label { get; set; } = "";
    public string DedupeKey { get; set; } = "";
    public JsonElement Payload { get; set; }
    public long? NextCheck { get; set; }
}

public sealed class TaskInfo
{
    public string Id { get; set; } = "";
    public ulong Seq { get; set; }
    public string Host { get; set; } = "";
    public string Action { get; set; } = "";
    public string Label { get; set; } = "";
    public string DedupeKey { get; set; } = "";
    public JsonElement Payload { get; set; }
    public string Status { get; set; } = "";
    public long Created { get; set; }
    public long NextCheck { get; set; }
    public long LastCheck { get; set; }
    public long CompletedAt { get; set; }
    public int Attempts { get; set; }
    public int Errors { get; set; }
    public int OfflineHits { get; set; }
    public string LastError { get; set; } = "";
    public string LastMessage { get; set; } = "";

    public TaskPayload GetPayload() =>
        Payload.ValueKind == JsonValueKind.Object ? Payload.Deserialize<TaskPayload>(TaskPayload.Json) ?? new() : new();
}

public sealed class AddReport
{
    public List<string> Added { get; set; } = [];
    public List<DuplicateInfo> Duplicates { get; set; } = [];
}

public sealed class DuplicateInfo
{
    public string Host { get; set; } = "";
    public string ExistingId { get; set; } = "";
}

public sealed class QueueStats
{
    public int Total { get; set; }
    public int Waiting { get; set; }
    public int Running { get; set; }
    public int Retry { get; set; }
    public int Paused { get; set; }
    public int Done { get; set; }
    public int Expired { get; set; }
}

public sealed class QueueRevision
{
    public ulong Revision { get; set; }
    public bool Paused { get; set; }
    public QueueStats Stats { get; set; } = new();
}

public sealed class LogEntry
{
    public ulong Seq { get; set; }
    public long Ts { get; set; }
    public string Level { get; set; } = "";
    public string? Task { get; set; }
    public string Msg { get; set; } = "";

    [JsonIgnore] public DateTime Time => DateTimeOffset.FromUnixTimeSeconds(Ts).LocalDateTime;
    public override string ToString() => $"[{Time:yyyy-MM-dd HH:mm:ss}] [{Level,-5}] {(Task is null ? "" : $"[{Task}] ")}{Msg}";
}

/// <summary>Parametri di un task (deploy o rimozione), serializzati nel payload Rust.</summary>
public sealed class TaskPayload
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // deploy
    public string DriverName { get; set; } = "";
    public string DriverDir { get; set; } = "";
    public string InfRelPath { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public string PrinterAddress { get; set; } = "";
    public string DefaultMode { get; set; } = DefaultModes.Auto;
    public bool DriverOnly { get; set; }

    // rimozione
    public List<string> RemoveTargets { get; set; } = [];
    public bool RemoveAllNetwork { get; set; }
    public bool RemovePorts { get; set; } = true;

    // comuni
    public bool RestartSetPrt { get; set; } = true;

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, Json);
}

public static class DefaultModes
{
    public const string None = "none";
    public const string Auto = "auto";
    public const string SetPrt = "setprt";
    public const string Windows = "windows";
    public const string Both = "both";

    public static readonly (string Key, string Label)[] All =
    [
        (Auto, "Automatica: set_prt se presente, altrimenti Windows"),
        (SetPrt, "Solo set_prt (tutte le versioni)"),
        (Windows, "Solo predefinita di Windows (utente collegato)"),
        (Both, "set_prt + Windows"),
        (None, "Non impostare come predefinita"),
    ];
}
