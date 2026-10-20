using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DuckPrinterManager.Core.Services;

public sealed class PsRunResult
{
    public bool HasResult { get; init; }
    public JsonElement Result { get; init; }
    public int ExitCode { get; init; }
    public string Stderr { get; init; } = "";
    public string Output { get; init; } = "";
    public bool TimedOut { get; init; }

    public string ErrorText =>
        TimedOut ? "Timeout dello script PowerShell"
        : !string.IsNullOrWhiteSpace(Stderr) ? Stderr.Trim()
        : !string.IsNullOrWhiteSpace(Output) ? Output.Trim()
        : $"PowerShell terminato (exit {ExitCode}) senza risultato";
}

/// <summary>
/// Esegue gli script PowerShell incorporati in un processo separato (pwsh 7 se presente,
/// altrimenti Windows PowerShell 5.1). Protocollo:
///  stdin  -> una riga base64(UTF-8 JSON) con i parametri (anche le credenziali: mai su riga di comando)
///  stdout -> "##LOG|LIVELLO|testo" per il log in tempo reale, "##RESULT|{json}" per l'esito.
/// </summary>
public sealed class PowerShellRunner(string scriptsDir, Func<string?> configuredExe)
{
    private static readonly JsonSerializerOptions InputJson = new() { PropertyNamingPolicy = null };
    private string? _exe;
    private bool _extracted;
    private readonly object _gate = new();

    public static string ResolveExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var candidates = new List<string>();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "PowerShell", "7", "pwsh.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir, "pwsh.exe"));
            candidates.Add(Path.Combine(dir, "pwsh"));
        }
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(sys)) candidates.Add(Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe"));
        return candidates.FirstOrDefault(File.Exists) ?? "powershell.exe";
    }

    public string Executable
    {
        get { lock (_gate) return _exe ??= ResolveExecutable(configuredExe()); }
    }

    public void ResetExecutable() { lock (_gate) _exe = null; }

    /// <summary>Testo di uno script incorporato (Scripts/*.ps1).</summary>
    public static string GetScriptText(string name)
    {
        using var s = typeof(PowerShellRunner).Assembly.GetManifestResourceStream("Scripts." + name)
                      ?? throw new FileNotFoundException("script incorporato mancante: " + name);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>Scrive gli script su disco (UTF-8 con BOM, cosi' anche PS 5.1 legge gli accenti).</summary>
    public void ExtractScripts()
    {
        lock (_gate)
        {
            if (_extracted) return;
            Directory.CreateDirectory(scriptsDir);
            var asm = Assembly.GetExecutingAssembly();
            foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("Scripts.") && n.EndsWith(".ps1")))
            {
                var text = GetScriptText(res["Scripts.".Length..]);
                var target = Path.Combine(scriptsDir, res["Scripts.".Length..]);
                var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
                if (!File.Exists(target) || !File.ReadAllBytes(target).SequenceEqual(bytes))
                    File.WriteAllBytes(target, bytes);
            }
            _extracted = true;
        }
    }

    public async Task<PsRunResult> RunAsync(string script, object input, Action<string, string>? onLog, TimeSpan timeout, CancellationToken ct = default)
    {
        ExtractScripts();
        var psi = new ProcessStartInfo
        {
            FileName = Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = scriptsDir,
        };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(scriptsDir, script) })
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        var other = new StringBuilder();
        string? resultLine = null;

        proc.Start();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input, InputJson)));
        await proc.StandardInput.WriteLineAsync(payload);
        proc.StandardInput.Close();

        var errTask = Task.Run(async () =>
        {
            string? l;
            while ((l = await proc.StandardError.ReadLineAsync()) is not null) lock (stderr) stderr.AppendLine(l);
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(cts.Token)) is not null)
            {
                if (line.StartsWith("##RESULT|", StringComparison.Ordinal)) resultLine = line[9..];
                else if (line.StartsWith("##LOG|", StringComparison.Ordinal))
                {
                    var parts = line.Split('|', 3);
                    onLog?.Invoke(parts.Length > 1 ? parts[1] : "INFO", parts.Length > 2 ? parts[2] : "");
                }
                else if (line.Length > 0) other.AppendLine(line);
            }
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (!timedOut) throw;
        }
        try { await errTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }

        JsonElement result = default;
        bool has = false;
        if (resultLine is not null)
        {
            try { using var doc = JsonDocument.Parse(resultLine); result = doc.RootElement.Clone(); has = true; }
            catch (JsonException ex) { other.AppendLine("Risultato non valido: " + ex.Message); }
        }
        return new PsRunResult
        {
            HasResult = has,
            Result = result,
            ExitCode = proc.HasExited ? proc.ExitCode : -1,
            Stderr = stderr.ToString(),
            Output = other.ToString(),
            TimedOut = timedOut,
        };
    }
}
