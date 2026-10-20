namespace DuckPrinterManager.Core.Services;

/// <summary>Elenco reparti (file di testo su share): una riga = un reparto, virgolette tollerate.</summary>
public sealed class Reparti
{
    public IReadOnlyList<string> Items { get; private set; } = [];
    public string Source { get; private set; } = "";
    public string? Error { get; private set; }

    public async Task LoadAsync(string path, TimeSpan timeout)
    {
        Source = path;
        try
        {
            // la share puo' essere lenta o irraggiungibile: mai bloccare l'interfaccia
            var read = Task.Run(() => File.Exists(path) ? File.ReadAllLines(path) : null);
            if (await Task.WhenAny(read, Task.Delay(timeout)) != read)
            {
                Error = "timeout di lettura";
                return;
            }
            var lines = await read;
            if (lines is null) { Error = "file non trovato"; Items = []; return; }
            Items = Parse(lines);
            Error = null;
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    public static List<string> Parse(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim().Trim('"', '\'', '﻿').Trim())
             .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith(';'))
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(l => l, StringComparer.CurrentCultureIgnoreCase)
             .ToList();

    public IEnumerable<string> Suggest(string text, int max = 12)
    {
        if (string.IsNullOrWhiteSpace(text)) return Items.Take(max);
        var t = text.Trim();
        return Items.Where(i => i.StartsWith(t, StringComparison.CurrentCultureIgnoreCase))
            .Concat(Items.Where(i => !i.StartsWith(t, StringComparison.CurrentCultureIgnoreCase) && i.Contains(t, StringComparison.CurrentCultureIgnoreCase)))
            .Take(max);
    }

    public bool Contains(string value) => Items.Contains(value.Trim(), StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Valore canonico (maiuscole/minuscole dell'elenco) se presente.</summary>
    public string Canonical(string value) =>
        Items.FirstOrDefault(i => string.Equals(i, value.Trim(), StringComparison.CurrentCultureIgnoreCase)) ?? value.Trim();
}
