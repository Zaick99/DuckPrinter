using System.Runtime.InteropServices;
using System.Text.Json;
using DuckPrinterManager.Core.Models;

namespace DuckPrinterManager.Core.Native;

public sealed class DuckCoreException(string message) : Exception(message);

/// <summary>
/// Ponte verso duck_core (Rust). Tutto lo stato a lunga vita (coda task, log, segreti di
/// sessione) vive nella memoria gestita da Rust; qui si passano solo JSON a vita breve.
/// </summary>
public sealed class DuckCore : IDisposable
{
    private const string Lib = "duck_core";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern uint duck_abi_version();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr duck_core_create();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void duck_core_destroy(IntPtr core);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr duck_call(IntPtr core, IntPtr method, IntPtr args);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void duck_free_string(IntPtr s);

    public const uint ExpectedAbi = 1;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private IntPtr _handle;
    private readonly object _gate = new();

    public DuckCore()
    {
        var abi = duck_abi_version();
        if (abi != ExpectedAbi)
            throw new DuckCoreException($"duck_core ABI {abi}, attesa {ExpectedAbi}: ricompilare la libreria nativa");
        _handle = duck_core_create();
    }

    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Metodi senza stato (hosts.parse, mac.*): non richiedono un'istanza.</summary>
    public static JsonElement CallStatic(string method, object? args = null) => Invoke(IntPtr.Zero, method, args);

    public JsonElement Call(string method, object? args = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            return Invoke(_handle, method, args);
        }
    }

    private static JsonElement Invoke(IntPtr core, string method, object? args)
    {
        var m = Marshal.StringToCoTaskMemUTF8(method);
        var a = Marshal.StringToCoTaskMemUTF8(args is null ? "{}" : JsonSerializer.Serialize(args, Json));
        IntPtr res = IntPtr.Zero;
        try
        {
            res = duck_call(core, m, a);
            var text = Marshal.PtrToStringUTF8(res) ?? "{}";
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                throw new DuckCoreException(root.TryGetProperty("error", out var e) ? e.GetString() ?? "errore" : "errore sconosciuto");
            return root.TryGetProperty("data", out var d) ? d.Clone() : default;
        }
        finally
        {
            Marshal.FreeCoTaskMem(m);
            // gli argomenti possono contenere segreti: azzerati prima del rilascio
            ZeroUtf8(a);
            Marshal.FreeCoTaskMem(a);
            if (res != IntPtr.Zero) duck_free_string(res);
        }
    }

    private static unsafe void ZeroUtf8(IntPtr p)
    {
        var b = (byte*)p;
        while (*b != 0) *b++ = 0;
    }

    private T Call<T>(string method, object? args = null) => Call(method, args).Deserialize<T>(Json)!;

    // ---------- stateless ----------
    public static HostParseResult ParseHosts(string text) => CallStatic("hosts.parse", new { text }).Deserialize<HostParseResult>(Json)!;
    public static string? NormalizeMac(string value) => CallStatic("mac.normalize", new { value }).GetProperty("mac").GetString();
    public static string SanitizeMacTyping(string value) => CallStatic("mac.sanitize", new { value }).GetProperty("value").GetString() ?? "";

    // ---------- task ----------
    public void Configure(long offlineSecs, long backoffBaseSecs, long backoffMaxSecs, long maxAgeSecs) =>
        Call("cfg.set", new { offline_secs = offlineSecs, backoff_base_secs = backoffBaseSecs, backoff_max_secs = backoffMaxSecs, max_age_secs = maxAgeSecs });

    public AddReport AddTasks(IEnumerable<NewTask> tasks, bool allowDuplicates = false) =>
        Call<AddReport>("tasks.add", new { now = Now, tasks, allow_duplicates = allowDuplicates });

    public List<TaskInfo> ListTasks() => Call<List<TaskInfo>>("tasks.list");
    public TaskInfo GetTask(string id) => Call<TaskInfo>("tasks.get", new { id });
    public QueueRevision Revision() => Call<QueueRevision>("tasks.revision");
    public TaskInfo EditTask(string id, object patch) => Call<TaskInfo>("tasks.edit", new { now = Now, id, patch });
    public int RemoveTasks(IEnumerable<string> ids) => Call("tasks.remove", new { ids }).GetProperty("removed").GetInt32();
    public int ClearTasks(string mode) => Call("tasks.clear", new { mode }).GetProperty("removed").GetInt32();
    public int CheckNow(IEnumerable<string>? ids = null) =>
        Call("tasks.check_now", ids is null ? new { now = Now } : (object)new { now = Now, ids }).GetProperty("queued").GetInt32();
    public int Pause(IEnumerable<string> ids) => Call("tasks.pause", new { now = Now, ids }).GetProperty("changed").GetInt32();
    public int Resume(IEnumerable<string> ids) => Call("tasks.resume", new { now = Now, ids }).GetProperty("changed").GetInt32();
    public void SetSchedulerPaused(bool paused) => Call("tasks.scheduler_paused", new { paused });

    public (List<TaskInfo> Tasks, int Expired) Due(int slots)
    {
        var d = Call("tasks.due", new { now = Now, slots });
        return (d.GetProperty("tasks").Deserialize<List<TaskInfo>>(Json)!, d.GetProperty("expired").GetInt32());
    }

    public TaskInfo Finish(string id, string outcome, string message) =>
        Call<TaskInfo>("tasks.finish", new { now = Now, id, outcome, message });

    public void Save(string path) => Call("tasks.save", new { path });
    public int Load(string path) => Call("tasks.load", new { now = Now, path }).GetProperty("loaded").GetInt32();

    // ---------- log ----------
    public void Log(string level, string message, string? task = null) => Call("log.append", new { now = Now, level, message, task });
    public List<LogEntry> GetLog(string? task, ulong after = 0, int limit = 1000) => Call<List<LogEntry>>("log.get", new { task, after, limit });
    public void ClearLog() => Call("log.clear");

    // ---------- segreti ----------
    public void PutSecret(string name, string value) => Call("secret.put", new { name, value });
    public string? GetSecret(string name) => Call("secret.get", new { name }).GetProperty("value").GetString();
    public void DropSecret(string name) => Call("secret.drop", new { name });

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            try { Invoke(_handle, "secret.clear", null); } catch { /* best effort */ }
            duck_core_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
