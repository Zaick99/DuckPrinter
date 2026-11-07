using System.IO.Compression;
using System.Text.Json;
using DuckPrinterManager.Core.Models;

namespace DuckPrinterManager.Core.Services;

public sealed record TaskOutcome(string Outcome, string Message)
{
    public static TaskOutcome Ok(string m) => new("ok", m);
    public static TaskOutcome Offline(string m) => new("offline", m);
    public static TaskOutcome Error(string m) => new("error", m);
}

public sealed record PsCredentialInput(string User, string Password);

/// <summary>Esegue UN task su UN host: ping, poi HostWorker.ps1 (che prova piu' metodi in cascata).</summary>
public sealed class TaskExecutor(PowerShellRunner ps, Func<AppConfig> config, Func<PsCredentialInput?> deployCredentials)
{
    public async Task<TaskOutcome> ExecuteAsync(TaskInfo task, Action<string, string> log, CancellationToken ct)
    {
        var cfg = config();
        if (!await Network.IsOnlineAsync(task.Host, cfg.PingTimeoutMs, cfg.PingTcpFallback, ct))
            return TaskOutcome.Offline("Host non raggiungibile");

        var p = task.GetPayload();
        // Account da provare in ordine: se uno riceve "accesso negato" si passa al successivo.
        // null = utente Windows che ha avviato l'app. Le credenziali AD della sessione si usano
        // sempre come alternativa, prima o dopo a seconda dell'impostazione.
        var ad = deployCredentials();
        if (ad is not null) ad = ad with { User = QualifyUser(ad.User, cfg.Domain) };
        var creds = new List<PsCredentialInput?>();
        if (ad is not null && cfg.DeployUseAdCredentials) creds.Add(ad);
        creds.Add(null);
        if (ad is not null && !cfg.DeployUseAdCredentials) creds.Add(ad);
        log("INFO", $"Host raggiungibile, avvio {(task.Action == TaskActions.Deploy ? "installazione" : "rimozione")} - account: " +
                    string.Join(" poi ", creds.Select(c => c?.User ?? $"{Environment.UserDomainName}\\{Environment.UserName} (Windows)")));

        object input;
        string? zip = null;
        if (task.Action == TaskActions.Deploy)
        {
            if (!Directory.Exists(p.DriverDir))
                return TaskOutcome.Error($"Cartella driver non disponibile: {p.DriverDir}");
            // Il driver viaggia come UN solo file: copiare file per file pacchetti con migliaia di
            // file e cartelle profonde (Canon) falliva per lunghezza dei percorsi e sola lettura.
            // .NET 8 legge percorsi lunghi senza limiti; lo zip si cancella a fine tentativo.
            try { zip = await Task.Run(() => PackDriver(p.DriverDir, task.Id), ct); }
            catch (Exception ex) { return TaskOutcome.Error($"Preparazione del driver non riuscita: {ex.Message}"); }
            log("DEBUG", $"Driver compresso per l'invio: {new FileInfo(zip).Length / 1048576.0:N1} MB");
            input = new
            {
                Action = "deploy",
                task.Host,
                Creds = creds,
                LoggedIn = ad is not null,
                RemoteDir = cfg.RemoteWorkDir,
                TimeoutSec = cfg.RemoteTimeoutSeconds,
                TaskId = task.Id,
                PayloadText = PowerShellRunner.GetScriptText("RemoteDeploy.ps1"),
                Deploy = new
                {
                    p.DriverName,
                    LocalDriverZip = zip,
                    p.InfRelPath,
                    p.PrinterName,
                    p.PrinterAddress,
                    p.DefaultMode,
                    p.RestartSetPrt,
                    p.DriverOnly,
                    cfg.SetPrtPaths,
                    cfg.SetPrtBatch,
                    cfg.SetPrtProcess,
                },
            };
        }
        else
        {
            var targets = new List<string>();
            foreach (var t in p.RemoveTargets)
                targets.AddRange(await Network.ExpandIdentityAsync(t));
            if (targets.Count > 0) log("DEBUG", "Porte cercate: " + string.Join(", ", targets.Distinct(StringComparer.OrdinalIgnoreCase)));
            input = new
            {
                Action = "remove",
                task.Host,
                Creds = creds,
                LoggedIn = ad is not null,
                RemoteDir = cfg.RemoteWorkDir,
                TimeoutSec = cfg.RemoteTimeoutSeconds,
                TaskId = task.Id,
                PayloadText = PowerShellRunner.GetScriptText("RemoteRemove.ps1"),
                Remove = new
                {
                    Targets = targets.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    p.RemoveAllNetwork,
                    p.RemovePorts,
                    p.RestartSetPrt,
                    cfg.SetPrtPaths,
                    cfg.SetPrtBatch,
                    cfg.SetPrtProcess,
                },
            };
        }

        PsRunResult res;
        try { res = await ps.RunAsync("HostWorker.ps1", input, log, TimeSpan.FromSeconds(cfg.RemoteTimeoutSeconds + 120), ct); }
        finally { if (zip is not null) TryDelete(zip); }
        if (!res.HasResult) return TaskOutcome.Error(res.ErrorText);

        var r = res.Result;
        var status = r.TryGetProperty("status", out var s) ? s.GetString() : "error";
        var msg = r.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        return status switch
        {
            "ok" => TaskOutcome.Ok(msg),
            "offline" => TaskOutcome.Offline(msg),
            _ => TaskOutcome.Error(msg.Length > 0 ? msg : res.ErrorText),
        };
    }

    /// <summary>
    /// Comprime la cartella del driver in %APPDATA%\duck\DuckPrinter\tmp. Prima toglie gli zip
    /// rimasti da tentativi interrotti (app chiusa a meta'), piu' vecchi di un giorno.
    /// </summary>
    private static string PackDriver(string driverDir, string taskId)
    {
        Directory.CreateDirectory(AppPaths.TempDir);
        foreach (var old in Directory.EnumerateFiles(AppPaths.TempDir, "*.zip"))
            if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) TryDelete(old);
        var zip = Path.Combine(AppPaths.TempDir, $"{taskId}-{Guid.NewGuid():N}"[..Math.Min(taskId.Length + 9, 64)] + ".zip");
        TryDelete(zip);
        ZipFile.CreateFromDirectory(driverDir, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
        return zip;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>"mario" -> "mario@dominio": senza dominio WinRM/SMB cercherebbero un utente locale del PC remoto.</summary>
    public static string QualifyUser(string user, string domain) =>
        user.Contains('\\') || user.Contains('@') || string.IsNullOrWhiteSpace(domain) ? user : $"{user}@{domain.Trim()}";
}

/// <summary>Costruisce i task a partire da cio' che e' selezionato nella pagina Deploy.</summary>
public static class DuckTasks
{
    public static IEnumerable<NewTask> Deploy(IEnumerable<string> hosts, TaskPayload p) =>
        hosts.Select(h => new NewTask
        {
            Host = h,
            Action = TaskActions.Deploy,
            Label = p.DriverOnly ? $"Solo driver {p.DriverName}" : $"{p.PrinterName} -> {p.PrinterAddress}",
            DedupeKey = Naming.DedupeKey(p.DriverName, p.PrinterName, p.PrinterAddress, p.DriverOnly.ToString()),
            Payload = p.ToJson(),
        });

    public static IEnumerable<NewTask> Remove(IEnumerable<string> hosts, TaskPayload p) =>
        hosts.Select(h => new NewTask
        {
            Host = h,
            Action = TaskActions.Remove,
            Label = p.RemoveAllNetwork ? "Rimuovi tutte le stampanti di rete" : "Rimuovi porte " + string.Join(", ", p.RemoveTargets),
            DedupeKey = Naming.DedupeKey(p.RemoveAllNetwork ? "*" : string.Join(",", p.RemoveTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))),
            Payload = p.ToJson(),
        });
}
