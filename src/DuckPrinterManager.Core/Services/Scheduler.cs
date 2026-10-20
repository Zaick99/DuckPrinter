using System.Collections.Concurrent;
using System.Text;
using DuckPrinterManager.Core.Native;

namespace DuckPrinterManager.Core.Services;

/// <summary>
/// Ciclo di servizio sempre vivo: ogni secondo chiede a duck_core i task in scadenza (la
/// politica - FIFO per host, ricontrollo ogni 10 minuti degli host spenti, backoff - sta in Rust),
/// li esegue in parallelo fino al massimo configurato, salva la coda e scrive il log su file.
/// "Pausa" ferma solo l'avvio di nuovi task: quelli gia' partiti finiscono.
/// </summary>
public sealed class Scheduler : IAsyncDisposable
{
    private readonly DuckCore _core;
    private readonly TaskExecutor _executor;
    private readonly Func<AppConfig> _config;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private ulong _savedRevision;
    private DateTime _lastSave = DateTime.MinValue;
    private ulong _logFlushed;
    private DateTime _nextPrune = DateTime.UtcNow.AddMinutes(1);

    public event Action? Tick;

    public Scheduler(DuckCore core, TaskExecutor executor, Func<AppConfig> config)
    {
        _core = core;
        _executor = executor;
        _config = config;
    }

    public int RunningCount => _running.Count;
    public bool IsPaused => _core.Revision().Paused;

    public void ApplyConfig()
    {
        var c = _config();
        _core.Configure(c.TaskCheckMinutes * 60L, c.TaskBackoffBase * 60L, c.TaskBackoffMax * 60L, c.TaskMaxAgeHours * 3600L);
    }

    public void Start(bool paused)
    {
        ApplyConfig();
        _core.SetSchedulerPaused(paused);
        _core.Log("TASK", paused ? "Scheduler pronto (in pausa)" : $"Scheduler avviato: max {_config().TaskMaxParallel} host in parallelo, host spenti ricontrollati ogni {_config().TaskCheckMinutes} min senza scadenza");
        _loop ??= Task.Run(() => LoopAsync(_stop.Token));
    }

    public void SetPaused(bool paused)
    {
        _core.SetSchedulerPaused(paused);
        _core.Log("TASK", paused ? "Scheduler in pausa: i task in corso terminano, nessun nuovo avvio" : "Scheduler ripreso");
    }

    /// <summary>Annulla l'esecuzione in corso di un task (il processo PowerShell viene terminato).</summary>
    public bool Cancel(string taskId)
    {
        if (!_running.TryGetValue(taskId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cfg = _config();
                var slots = cfg.TaskMaxParallel - _running.Count;
                if (slots > 0)
                {
                    var (due, expired) = _core.Due(slots);
                    if (expired > 0) _core.Log("WARN", $"{expired} task scaduti (limite {cfg.TaskMaxAgeHours} ore)");
                    if (due.Count > 1) _core.Log("TASK", $"Avvio di {due.Count} task in parallelo ({_running.Count + due.Count}/{cfg.TaskMaxParallel} slot)");
                    foreach (var t in due) StartOne(t, ct);
                }
                PersistIfNeeded(false);
                FlushLogToFile();
                if (DateTime.UtcNow >= _nextPrune) PruneCache();
                Tick?.Invoke();
            }
            catch (Exception ex)
            {
                try { _core.Log("ERR", "Scheduler: " + ex.Message); } catch { }
            }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void StartOne(Models.TaskInfo t, CancellationToken appCt)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        _running[t.Id] = cts;
        _ = Task.Run(async () =>
        {
            TaskOutcome outcome;
            try
            {
                outcome = await _executor.ExecuteAsync(t, (lvl, msg) => _core.Log(lvl, msg, t.Id), cts.Token);
            }
            catch (OperationCanceledException) when (appCt.IsCancellationRequested)
            {
                return; // chiusura app: al riavvio il task riparte (Running -> Waiting al load)
            }
            catch (OperationCanceledException)
            {
                outcome = TaskOutcome.Error("Esecuzione annullata dall'utente");
            }
            catch (Exception ex)
            {
                outcome = TaskOutcome.Error(ex.Message);
            }
            finally
            {
                _running.TryRemove(t.Id, out _);
                cts.Dispose();
            }
            try { _core.Finish(t.Id, outcome.Outcome, outcome.Message); } catch (DuckCoreException) { /* task cancellato nel frattempo */ }
        });
    }

    public void PersistIfNeeded(bool force)
    {
        var rev = _core.Revision().Revision;
        if (rev == _savedRevision) return;
        if (!force && DateTime.UtcNow - _lastSave < TimeSpan.FromSeconds(3)) return;
        _core.Save(AppPaths.Tasks);
        _savedRevision = rev;
        _lastSave = DateTime.UtcNow;
    }

    private void FlushLogToFile()
    {
        var entries = _core.GetLog(null, _logFlushed, 5000);
        if (entries.Count == 0) return;
        try
        {
            var fi = new FileInfo(AppPaths.LogFile);
            if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                File.Move(AppPaths.LogFile, AppPaths.LogFile + ".1", true);
            var sb = new StringBuilder();
            foreach (var e in entries) sb.AppendLine(e.ToString());
            File.AppendAllText(AppPaths.LogFile, sb.ToString(), Encoding.UTF8);
        }
        catch { /* il log su file non deve mai fermare lo scheduler */ }
        _logFlushed = entries[^1].Seq;
    }

    private void PruneCache()
    {
        _nextPrune = DateTime.UtcNow.AddHours(1);
        try
        {
            var inUse = _core.ListTasks()
                .Where(t => t.Status is not (Models.TaskStatus.Done or Models.TaskStatus.Expired))
                .Select(t => t.GetPayload().DriverDir);
            DriverCache.Prune(AppPaths.DriverCache, inUse);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) { try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
        try { PersistIfNeeded(true); FlushLogToFile(); } catch { }
        _stop.Dispose();
    }
}
