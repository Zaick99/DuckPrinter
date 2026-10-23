using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuckPrinterManager.Core.Services;

/// <summary>
/// Configurazione persistente. I nomi delle chiavi restano quelli di duck_config.json dello
/// script PowerShell originale, cosi' un file esistente viene importato cosi' com'e'.
/// </summary>
public sealed class AppConfig
{
    // ---- Active Directory ----
    public string OUPath { get; set; } = "OU=Printers,OU=MAC Authentication,DC=example,DC=local";
    public string Domain { get; set; } = "example.local";
    public string DomainController { get; set; } = "";
    /// <summary>Schema del nome: testo + {INCREMENTAL} oppure {INCREMENTAL:4} per fissare le cifre.</summary>
    public string NamePattern { get; set; } = "PR{INCREMENTAL}";
    public int IncrementalDigits { get; set; } = 4;
    public List<string> ADGroups { get; set; } = ["MAC-Auth Printers"];
    public string RepartiFile { get; set; } = @"\\fileserver\share\config\reparti.txt";
    public bool StrictReparti { get; set; } = true;
    public bool AutoLoginProbe { get; set; } = true;
    public bool ForceWriteMode { get; set; }
    public string LastUser { get; set; } = "EXAMPLE\\";

    /// <summary>Password di passaggio per la creazione (protetta con DPAPI per l'utente corrente).</summary>
    public string DefaultPasswordProtected { get; set; } = "";

    // ---- Deploy ----
    public string PrinterNameTemplate { get; set; } = "{DRIVERNAME}";
    public string DefaultMode { get; set; } = Models.DefaultModes.Auto;
    public bool RestartSetPrt { get; set; } = true;
    // valori di partenza della procedura guidata (si cambiano a ogni deploy, qui solo il punto di partenza)
    public string DeployPreRemove { get; set; } = "none";       // none | same | all
    public bool RemovePortsDefault { get; set; } = true;
    public bool RemoveRestartSetPrt { get; set; } = true;
    public int DeployStartDefault { get; set; }                 // 0 subito · 1 tra 30 min · 2 tra 1 ora · 3 stasera · 4 domani
    public List<string> SetPrtPaths { get; set; } = [@"C:\set_prt"];
    public string SetPrtBatch { get; set; } = "set_prt.bat";
    public string SetPrtProcess { get; set; } = "set_prt_agent";
    public string RemoteWorkDir { get; set; } = @"C:\ProgramData\DuckPM";
    public bool DeployUseAdCredentials { get; set; } = true;
    public bool CacheDrivers { get; set; } = true;

    // ---- Scheduler ----
    public int TaskCheckMinutes { get; set; } = 10;
    public int TaskBackoffBase { get; set; } = 5;
    public int TaskBackoffMax { get; set; } = 60;
    public int TaskMaxAgeHours { get; set; }
    public int TaskMaxParallel { get; set; } = 50;
    public int RemoteTimeoutSeconds { get; set; } = 900;
    public int PingTimeoutMs { get; set; } = 3000;
    public bool PingTcpFallback { get; set; } = true;
    public bool SchedulerAutoStart { get; set; } = true;

    // ---- Interfaccia / sistema ----
    public bool DucksAnimate { get; set; } = true;
    public bool WindowTranslucent { get; set; }
    public string PowerShellPath { get; set; } = "";

    // compatibilita' con lo script originale (letta una volta e migrata)
    [JsonPropertyName("UserPrefix")] public string? LegacyUserPrefix { get; set; }
    [JsonPropertyName("DefaultPassword")] public string? LegacyDefaultPassword { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load(string path, string? legacyPath = null)
    {
        AppConfig cfg = new();
        var source = File.Exists(path) ? path : legacyPath is not null && File.Exists(legacyPath) ? legacyPath : null;
        if (source is not null)
        {
            try { cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(source), Json) ?? new(); }
            catch { cfg = new(); }
        }
        cfg.Migrate();
        cfg.Clamp();
        return cfg;
    }

    private void Migrate()
    {
        if (!string.IsNullOrWhiteSpace(LegacyUserPrefix) && NamePattern == "PR{INCREMENTAL}")
            NamePattern = LegacyUserPrefix.Trim() + "{INCREMENTAL}";
        if (!string.IsNullOrEmpty(LegacyDefaultPassword) && string.IsNullOrEmpty(DefaultPasswordProtected))
            DefaultPassword = LegacyDefaultPassword;
        LegacyUserPrefix = null;
        LegacyDefaultPassword = null;
        if (string.IsNullOrEmpty(DefaultPasswordProtected)) DefaultPassword = "Pippopippo1234!";
        ADGroups = ADGroups.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SetPrtPaths = SetPrtPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim().TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (SetPrtPaths.Count == 0) SetPrtPaths.Add(@"C:\set_prt");
    }

    public void Clamp()
    {
        TaskCheckMinutes = Math.Clamp(TaskCheckMinutes, 1, 1440);
        TaskBackoffBase = Math.Clamp(TaskBackoffBase, 1, 1440);
        TaskBackoffMax = Math.Clamp(TaskBackoffMax, TaskBackoffBase, 10080);
        TaskMaxAgeHours = Math.Clamp(TaskMaxAgeHours, 0, 87600);
        TaskMaxParallel = Math.Clamp(TaskMaxParallel, 1, 1000);
        RemoteTimeoutSeconds = Math.Clamp(RemoteTimeoutSeconds, 60, 7200);
        PingTimeoutMs = Math.Clamp(PingTimeoutMs, 250, 30000);
        IncrementalDigits = Math.Clamp(IncrementalDigits, 1, 9);
        if (string.IsNullOrWhiteSpace(PrinterNameTemplate)) PrinterNameTemplate = "{DRIVERNAME}";
        if (!Naming.IsValidPattern(NamePattern)) NamePattern = "PR{INCREMENTAL}";
        if (DeployPreRemove is not ("none" or "same" or "all")) DeployPreRemove = "none";
        DeployStartDefault = Math.Clamp(DeployStartDefault, 0, 4);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    [JsonIgnore]
    public string DefaultPassword
    {
        get
        {
            if (string.IsNullOrEmpty(DefaultPasswordProtected)) return "";
            try
            {
                var raw = Convert.FromBase64String(DefaultPasswordProtected);
                if (OperatingSystem.IsWindows())
                    raw = ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch { return ""; }
        }
        set
        {
            var raw = Encoding.UTF8.GetBytes(value ?? "");
            if (OperatingSystem.IsWindows())
                raw = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
            DefaultPasswordProtected = Convert.ToBase64String(raw);
        }
    }

    private static readonly byte[] Entropy = "DuckPrinterManager/v1"u8.ToArray();

    public AppConfig Clone() => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, Json), Json)!;
}

public static class AppPaths
{
    /// <summary>Tutti i file dell'app (configurazione, task, log, cache driver, script, tema).</summary>
    public static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "duck", "DuckPrinter");

    public static string Config => Path.Combine(Root, "duck_config.json");
    public static string Tasks => Path.Combine(Root, "duck_tasks.json");
    public static string LogFile => Path.Combine(Root, "duck_printer_manager.log");
    public static string DriverCache => Path.Combine(Root, "_driver_cache");
    public static string ScriptsDir => Path.Combine(Root, "ps");
    public static string ThemePreference => Path.Combine(Root, "theme");

    /// <summary>duck_config.json accanto all'eseguibile (installazione "portatile" come lo script).</summary>
    public static string LegacyConfig => Path.Combine(AppContext.BaseDirectory, "duck_config.json");

    /// <summary>Cartella delle versioni precedenti, migrata in <see cref="Root"/> al primo avvio.</summary>
    public static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Duck", "PrinterManager");

    /// <summary>Preferenza del tema delle versioni precedenti (condivisa con le altre app Duck: solo lettura).</summary>
    public static string LegacyThemePreference => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Duck", "theme");

    public static void Ensure()
    {
        MigrateLegacy();
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DriverCache);
        Directory.CreateDirectory(ScriptsDir);
    }

    /// <summary>
    /// Sposta i dati da %LOCALAPPDATA%\Duck\PrinterManager (2.0.0) finche' nella nuova cartella non c'e'
    /// una configurazione salvata: configurazione, coda dei task (+ .bak), log e cache driver.
    /// La 2.0.1 creava gia' la cartella nuova con una coda vuota e la cache vuota: una coda senza
    /// task viene sostituita da quella vecchia e la cache si sposta cartella per cartella. I task
    /// puntano ai driver con il percorso assoluto della cache, quindi il percorso viene riscritto
    /// dentro duck_tasks.json. Gli script (ps\) si rigenerano da soli.
    /// </summary>
    private static void MigrateLegacy()
    {
        try
        {
            var old = LegacyRoot;
            if (!Directory.Exists(old) || File.Exists(Config)) return;
            Directory.CreateDirectory(Root);

            foreach (var name in new[] { "duck_config.json", "duck_tasks.json", "duck_tasks.json.bak", "duck_printer_manager.log", "duck_printer_manager.log.1" })
            {
                var src = Path.Combine(old, name);
                var dst = Path.Combine(Root, name);
                if (!File.Exists(src)) continue;
                if (File.Exists(dst))
                {
                    if (!name.StartsWith("duck_tasks", StringComparison.Ordinal) || !IsEmptyQueue(dst)) continue;
                    File.Delete(dst);
                }
                MoveOrCopy(src, dst);
            }

            var oldCache = Path.Combine(old, "_driver_cache");
            if (Directory.Exists(oldCache))
            {
                Directory.CreateDirectory(DriverCache);
                foreach (var dir in Directory.EnumerateDirectories(oldCache))
                {
                    var target = Path.Combine(DriverCache, Path.GetFileName(dir));
                    if (Directory.Exists(target)) continue;
                    try { Directory.Move(dir, target); }
                    catch { CopyDir(dir, target); }
                }
            }

            foreach (var name in new[] { "duck_tasks.json", "duck_tasks.json.bak" })
            {
                var f = Path.Combine(Root, name);
                if (!File.Exists(f)) continue;
                var json = File.ReadAllText(f);
                // nel JSON le barre rovesciate sono raddoppiate
                var fixedJson = json.Replace(JsonEscape(oldCache), JsonEscape(DriverCache), StringComparison.OrdinalIgnoreCase);
                if (fixedJson != json) File.WriteAllText(f, fixedJson);
            }

            try { Directory.Delete(Path.Combine(old, "ps"), true); } catch { }
            try { if (Directory.Exists(oldCache) && !Directory.EnumerateFileSystemEntries(oldCache).Any()) Directory.Delete(oldCache); } catch { }
            try { if (!Directory.EnumerateFileSystemEntries(old).Any()) Directory.Delete(old); } catch { }
        }
        catch
        {
            // la migrazione non deve mai impedire l'avvio: nel peggiore dei casi si riparte da zero
        }
    }

    /// <summary>Coda salvata senza alcun task (quella che la 2.0.1 scriveva al primo avvio).</summary>
    private static bool IsEmptyQueue(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("tasks", out var t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() == 0;
        }
        catch { return false; }
    }

    private static string JsonEscape(string s) => s.Replace("\\", "\\\\");

    private static void MoveOrCopy(string src, string dst)
    {
        try { File.Move(src, dst); }
        catch { File.Copy(src, dst, overwrite: false); }
    }

    private static void CopyDir(string src, string dst)
    {
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
        }
    }
}
