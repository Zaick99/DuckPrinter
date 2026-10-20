using System.Text.RegularExpressions;

namespace DuckPrinterManager.Core.Services;

/// <summary>Nomi stampante ({DRIVERNAME}) e nomi utente AD incrementali (PR{INCREMENTAL}).</summary>
public static partial class Naming
{
    public const string DriverToken = "{DRIVERNAME}";

    [GeneratedRegex(@"\{(DRIVERNAME|IP|ADDRESS|DATE)\}", RegexOptions.IgnoreCase)]
    private static partial Regex PrinterTokens();

    [GeneratedRegex(@"\{INCREMENTAL(?::(\d{1,2}))?\}", RegexOptions.IgnoreCase)]
    private static partial Regex IncrementalToken();

    // caratteri non ammessi nel nome di una coda di stampa (\ e , in particolare rompono printui e set_prt)
    [GeneratedRegex(@"[\\/,!""\p{C}]")]
    private static partial Regex InvalidPrinterChars();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    /// <summary>
    /// {DRIVERNAME} -> nome driver; il resto del testo resta cosi' com'e' ("{DRIVERNAME} (PR0042)").
    /// Supporta anche {IP}/{ADDRESS} (indirizzo stampante) e {DATE}.
    /// </summary>
    public static string RenderPrinterName(string? template, string driverName, string? printerAddress = null, DateTime? now = null)
    {
        var t = string.IsNullOrWhiteSpace(template) ? DriverToken : template;
        var s = PrinterTokens().Replace(t, m => m.Groups[1].Value.ToUpperInvariant() switch
        {
            "DRIVERNAME" => driverName ?? "",
            "IP" or "ADDRESS" => printerAddress ?? "",
            "DATE" => (now ?? DateTime.Now).ToString("yyyyMMdd"),
            _ => m.Value,
        });
        s = MultiSpace().Replace(InvalidPrinterChars().Replace(s, " "), " ").Trim();
        if (s.Length > 220) s = s[..220].TrimEnd();
        return s.Length == 0 ? (driverName ?? "").Trim() : s;
    }

    public static bool IsValidPattern(string? pattern) =>
        !string.IsNullOrWhiteSpace(pattern) && IncrementalToken().Matches(pattern).Count == 1;

    /// <summary>Parte di nome fissa prima del contatore (per filtrare AD con Name -like 'PR*').</summary>
    public static string PatternPrefix(string pattern)
    {
        var m = IncrementalToken().Match(pattern);
        return m.Success ? pattern[..m.Index] : pattern;
    }

    /// <summary>
    /// Prossimo nome libero: max(contatori esistenti) + 1. Cifre: quelle esplicite del token,
    /// altrimenti la lunghezza piu' usata fra i nomi esistenti, altrimenti <paramref name="defaultDigits"/>.
    /// </summary>
    public static string NextName(string pattern, IEnumerable<string> existing, int defaultDigits = 4)
    {
        var m = IncrementalToken().Match(pattern);
        if (!m.Success) throw new ArgumentException("lo schema deve contenere {INCREMENTAL}", nameof(pattern));
        var prefix = pattern[..m.Index];
        var suffix = pattern[(m.Index + m.Length)..];
        int? explicitDigits = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : null;

        var rx = new Regex("^" + Regex.Escape(prefix) + @"(\d+)" + Regex.Escape(suffix) + "$", RegexOptions.IgnoreCase);
        long max = 0;
        var lengths = new Dictionary<int, int>();
        foreach (var name in existing)
        {
            var mm = rx.Match(name ?? "");
            if (!mm.Success) continue;
            var digits = mm.Groups[1].Value;
            if (long.TryParse(digits, out var n) && n > max) max = n;
            lengths[digits.Length] = lengths.GetValueOrDefault(digits.Length) + 1;
        }
        var pad = explicitDigits ?? (lengths.Count > 0 ? lengths.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key).First().Key : defaultDigits);
        return prefix + (max + 1).ToString().PadLeft(pad, '0') + suffix;
    }

    /// <summary>Chiave di deduplica di un task: stessi parametri sullo stesso host = duplicato.</summary>
    public static string DedupeKey(params string?[] parts) =>
        string.Join("|", parts.Select(p => (p ?? "").Trim().ToLowerInvariant()));
}
