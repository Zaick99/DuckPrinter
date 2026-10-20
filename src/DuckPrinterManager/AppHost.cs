using System.IO;
using DuckPrinterManager.Core.Native;
using DuckPrinterManager.Core.Services;

namespace DuckPrinterManager;

/// <summary>Servizi applicativi condivisi fra le pagine (un'istanza per processo).</summary>
public static class AppHost
{
    public static AppConfig Config { get; private set; } = new();
    public static DuckCore Core { get; private set; } = null!;
    public static PowerShellRunner Ps { get; private set; } = null!;
    public static AdService Ad { get; private set; } = null!;
    public static Scheduler Scheduler { get; private set; } = null!;
    public static Reparti Reparti { get; } = new();

    public static event Action? ConfigChanged;
    public static event Action? RepartiChanged;

    public static void Start()
    {
        AppPaths.Ensure();
        Config = AppConfig.Load(AppPaths.Config, AppPaths.LegacyConfig);
        Core = new DuckCore();
        Ps = new PowerShellRunner(AppPaths.ScriptsDir, () => Config.PowerShellPath);
        Ad = new AdService(Ps, Core, () => Config) { SelectedDc = string.IsNullOrWhiteSpace(Config.DomainController) ? "Automatico" : Config.DomainController };

        try
        {
            var n = Core.Load(AppPaths.Tasks);
            Core.Log("INFO", $"Avvio: {n} task ripristinati dalla coda");
        }
        catch (Exception ex)
        {
            Core.Log("ERR", "Coda task non leggibile, si riparte vuoti: " + ex.Message);
        }
        Core.Log("INFO", $"PowerShell: {Ps.Executable}");

        var exec = new TaskExecutor(Ps, () => Config, () => Ad.Credentials);
        Scheduler = new Scheduler(Core, exec, () => Config);
        Scheduler.Start(paused: !Config.SchedulerAutoStart);

        // Appena si accede con un account AD, i task fermi per "accesso negato" ripartono subito
        // (senza aspettare i 5 minuti del prossimo tentativo): ora hanno un account in piu' da provare.
        Ad.SessionChanged += () =>
        {
            if (!Ad.IsLoggedIn) return;
            try
            {
                var ids = Core.ListTasks()
                    .Where(t => t.Status == DuckPrinterManager.Core.Models.TaskStatus.Retry && t.LastMessage.Contains("Accesso negato", StringComparison.OrdinalIgnoreCase))
                    .Select(t => t.Id).ToList();
                if (ids.Count > 0) Core.Log("INFO", $"Accesso AD: {Core.CheckNow(ids)} task in 'accesso negato' riprovati subito");
            }
            catch (Exception ex) { TryLog("WARN", "Ripresa dei task dopo l'accesso: " + ex.Message); }
        };
    }

    public static void SaveConfig(AppConfig cfg)
    {
        cfg.Clamp();
        cfg.Save(AppPaths.Config);
        var repartiChanged = !string.Equals(cfg.RepartiFile, Config.RepartiFile, StringComparison.OrdinalIgnoreCase);
        Config = cfg;
        Theme.ThemeManager.SetTranslucent(cfg.WindowTranslucent);
        Ps.ResetExecutable();
        Scheduler.ApplyConfig();
        Core.Log("INFO", "Impostazioni salvate");
        ConfigChanged?.Invoke();
        if (repartiChanged) _ = LoadRepartiAsync();
    }

    public static async Task LoadRepartiAsync()
    {
        await Reparti.LoadAsync(Config.RepartiFile, TimeSpan.FromSeconds(20));
        if (Reparti.Error is null) Core.Log("INFO", $"Reparti caricati: {Reparti.Items.Count}");
        else Core.Log("WARN", $"Elenco reparti non caricato ({Reparti.Error}): {Config.RepartiFile}");
        RepartiChanged?.Invoke();
    }

    public static void TryLog(string level, string msg)
    {
        try { Core?.Log(level, msg); } catch { }
        try { File.AppendAllText(AppPaths.LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level,-5}] {msg}{Environment.NewLine}"); } catch { }
    }

    public static void Stop()
    {
        try { Scheduler?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(8)); } catch { }
        try { Core?.Dispose(); } catch { }
    }
}
