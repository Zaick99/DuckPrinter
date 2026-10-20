using System.Text.Json;
using DuckPrinterManager.Core.Models;
using DuckPrinterManager.Core.Native;

namespace DuckPrinterManager.Core.Services;

public sealed class AdException(string message) : Exception(message);

/// <summary>
/// Operazioni Active Directory tramite Ad.ps1 (modulo ActiveDirectory/RSAT + ADSI).
/// La password della sessione resta nel core Rust ("ad.password") e passa solo via stdin.
/// </summary>
public sealed class AdService(PowerShellRunner ps, DuckCore core, Func<AppConfig> config)
{
    private const string SecretName = "ad.password";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public AdSession? Session { get; private set; }
    public string? SelectedDc { get; set; }

    public event Action? SessionChanged;

    public bool IsLoggedIn => Session is not null;
    public bool CanEdit => Session is not null && (Session.CanEdit || config().ForceWriteMode);
    public bool CanCreate => Session is not null && (Session.CanCreate || config().ForceWriteMode);

    public PsCredentialInput? Credentials =>
        Session is null ? null : new PsCredentialInput(Session.User, core.GetSecret(SecretName) ?? "");

    private string Server
    {
        get
        {
            var dc = SelectedDc;
            if (string.IsNullOrWhiteSpace(dc) || dc == "Automatico") dc = config().DomainController;
            return dc?.Trim() ?? "";
        }
    }

    private async Task<JsonElement> RunAsync(string action, object extra, PsCredentialInput? cred = null, int timeoutSec = 180)
    {
        var cfg = config();
        cred ??= Credentials;
        var input = new Dictionary<string, object?>
        {
            ["Action"] = action,
            ["Cred"] = cred,
            ["Server"] = Server,
            ["Domain"] = cfg.Domain,
            ["Ou"] = cfg.OUPath,
        };
        foreach (var p in extra.GetType().GetProperties()) input[p.Name] = p.GetValue(extra);

        var res = await ps.RunAsync("Ad.ps1", input, (lvl, msg) => core.Log(lvl, "AD: " + msg), TimeSpan.FromSeconds(timeoutSec));
        if (!res.HasResult) throw new AdException(res.ErrorText);
        var r = res.Result;
        if (r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } err)
            throw new AdException(err);
        return r;
    }

    public async Task<AdSession> LoginAsync(string user, string password)
    {
        var cred = new PsCredentialInput(user.Trim(), password);
        var r = await RunAsync("login", new { }, cred);
        var s = r.Deserialize<AdSession>(Json)!;
        s.User = user.Trim();
        core.PutSecret(SecretName, password);
        Session = s;
        core.Log("INFO", $"AD: accesso come {s.User} - {(s.CanWrite ? "modifica abilitata" : "sola lettura")} ({string.Join("; ", s.Reasons)})");
        SessionChanged?.Invoke();
        return s;
    }

    public void Logout()
    {
        core.DropSecret(SecretName);
        Session = null;
        core.Log("INFO", "AD: disconnesso");
        SessionChanged?.Invoke();
    }

    private void RequireLogin()
    {
        if (Session is null) throw new AdException("Accedere prima con credenziali di dominio");
    }

    public async Task<AdSearchResult> SearchAsync(string term)
    {
        RequireLogin();
        var r = await RunAsync("search", new { Term = term.Trim(), Probe = config().AutoLoginProbe });
        return r.Deserialize<AdSearchResult>(Json)!;
    }

    public async Task<ProbeResult> ProbeAsync(AdUserInfo u, string? password = null)
    {
        RequireLogin();
        var r = await RunAsync("probe", new { u.SamAccountName, u.UserPrincipalName, Password = password ?? u.MacCurrent, Force = true });
        return r.Deserialize<ProbeResult>(Json)!;
    }

    public async Task<List<string>> ExistingNamesAsync()
    {
        RequireLogin();
        var r = await RunAsync("names", new { Prefix = Naming.PatternPrefix(config().NamePattern) });
        return r.GetProperty("names").Deserialize<List<string>>(Json) ?? [];
    }

    public async Task<string> NextNameAsync() =>
        Naming.NextName(config().NamePattern, await ExistingNamesAsync(), config().IncrementalDigits);

    public async Task<AdUpdateResult> UpdateAsync(AdUserInfo u, string? newMacRaw, string newDescription)
    {
        RequireLogin();
        if (!CanEdit) throw new AdException("Le credenziali in uso non hanno permessi di modifica");
        string newMac = "";
        if (!string.IsNullOrWhiteSpace(newMacRaw))
            newMac = DuckCore.NormalizeMac(newMacRaw) ?? throw new AdException("MAC non valido: servono 12 caratteri esadecimali (es. 001122aabbcc)");

        var r = await RunAsync("update", new
        {
            u.Guid,
            u.DistinguishedName,
            OldMac = u.MacCurrent,
            NewMac = newMac,
            OldDesc = u.Description,
            NewDesc = newDescription.Trim(),
            Probe = config().AutoLoginProbe,
        });
        return r.Deserialize<AdUpdateResult>(Json)!;
    }

    /// <summary>Elimina definitivamente l'utenza (per GUID, solo dentro la OU configurata). Restituisce il DN eliminato.</summary>
    public async Task<string> DeleteAsync(AdUserInfo u)
    {
        RequireLogin();
        if (!CanEdit) throw new AdException("Le credenziali in uso non hanno permessi di modifica");
        var r = await RunAsync("delete", new { u.Guid, ExpectedSam = u.SamAccountName });
        return r.TryGetProperty("DistinguishedName", out var dn) ? dn.GetString() ?? u.DistinguishedName : u.DistinguishedName;
    }

    public async Task<AdCreateResult> CreateAsync(string macRaw, string description, Action<string>? progress = null)
    {
        RequireLogin();
        if (!CanCreate) throw new AdException("Le credenziali in uso non hanno permessi di creazione nella OU");
        var mac = DuckCore.NormalizeMac(macRaw) ?? throw new AdException("MAC non valido: servono 12 caratteri esadecimali");
        var cfg = config();
        AdCreateResult? last = null;
        // il nome si ricalcola a ogni giro: un collega puo' aver creato lo stesso PRxxxx un attimo prima
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var name = await NextNameAsync();
            progress?.Invoke($"Creazione di {name}...");
            var r = await RunAsync("create", new
            {
                Name = name,
                Mac = mac,
                Desc = description.Trim(),
                Groups = cfg.ADGroups,
                TempPassword = cfg.DefaultPassword,
                Probe = cfg.AutoLoginProbe,
            });
            last = r.Deserialize<AdCreateResult>(Json)!;
            if (last.Ok || last.Reason != "NameExists") break;
        }
        return last!;
    }

    public async Task<List<string>> ListDomainControllersAsync()
    {
        var r = await RunAsync("dcs", new { }, Credentials, 60);
        return r.GetProperty("dcs").Deserialize<List<string>>(Json) ?? [];
    }

    public async Task<List<AdGroupInfo>> SearchGroupsAsync(string term)
    {
        RequireLogin();
        var r = await RunAsync("groups", new { Term = term.Trim() });
        return r.GetProperty("groups").Deserialize<List<AdGroupInfo>>(Json) ?? [];
    }
}
