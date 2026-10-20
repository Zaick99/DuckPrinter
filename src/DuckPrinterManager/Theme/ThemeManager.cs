using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DuckPrinterManager.Theme;

/// <summary>
/// Motore giorno/notte (port C# di Duck.Theme.ps1). I token diventano SolidColorBrush NON
/// congelati in un ResourceDictionary: cambiare tema muta il Color dei pennelli esistenti, quindi
/// ogni DynamicResource si aggiorna in un frame senza ricostruire l'albero visuale.
/// </summary>
public static class ThemeManager
{
    private static Dictionary<string, Dictionary<string, string>> _palettes = new();
    private static ResourceDictionary _tokens = new();
    private static Application? _app;
    private static LinearGradientBrush? _accentFill;
    private static DispatcherTimer? _watch;
    private static readonly string PrefPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Duck", "theme");

    /// <summary>Tema effettivamente applicato: light | dark.</summary>
    public static string Effective { get; private set; } = "light";

    /// <summary>Preferenza: system | light | dark.</summary>
    public static string Preference { get; private set; } = "system";

    public static event Action<string>? Changed;

    public static void Initialize(Application app, bool translucent = false)
    {
        Translucent = translucent;
        using (var s = typeof(ThemeManager).Assembly.GetManifestResourceStream("Theme.tokens.extended.json")!)
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(s)!;
            _palettes = raw.ToDictionary(k => k.Key.ToLowerInvariant(), v => v.Value);
        }
        _app = app;
        app.Resources.MergedDictionaries.Insert(0, _tokens);

        _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _watch.Tick += (_, _) =>
        {
            if (Preference == "system" && SystemTheme() != Effective) Set("system", save: false);
        };
        _watch.Start();
        Set(LoadPreference(), save: false, animate: false);
    }

    public static string SystemTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0 ? "dark" : "light";
        }
        catch { return "light"; }
    }

    private static string LoadPreference()
    {
        try
        {
            var p = File.ReadAllText(PrefPath).Trim().ToLowerInvariant();
            if (p is "system" or "light" or "dark") return p;
        }
        catch { }
        return "system";
    }

    /// <summary>
    /// Applica il tema. I pennelli dei token restano GLI STESSI per tutta la vita dell'app e
    /// il loro colore sfuma verso il nuovo (ColorAnimation): nessun dizionario sostituito, nessuna
    /// foto sovrapposta, quindi nessun lampo del tema vecchio. Un pennello con un'animazione
    /// attiva non e' congelabile, percio' WPF non lo blocca in sola lettura. Se un pennello
    /// risultasse comunque congelato si ripiega sul cambio istantaneo (dizionario nuovo).
    /// </summary>
    public static void Set(string preference, bool save = true, bool animate = true)
    {
        Preference = preference is "light" or "dark" ? preference : "system";
        var effective = Preference == "system" ? SystemTheme() : Preference;
        var targets = TargetColors(effective);

        var first = _tokens.Count == 0;
        var frozen = !first && targets.Keys.Any(k => _tokens[k] is SolidColorBrush { IsFrozen: true });
        if (first || frozen)
        {
            var dict = new ResourceDictionary();
            foreach (var (key, c) in targets) dict[key] = Live(c);
            dict["AccentFill"] = AccentFill();
            if (_app is not null)
            {
                var merged = _app.Resources.MergedDictionaries;
                var i = merged.IndexOf(_tokens);
                if (i >= 0) merged[i] = dict; else merged.Insert(0, dict);
            }
            _tokens = dict;
        }
        else
        {
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var duration = TimeSpan.FromMilliseconds(animate ? 520 : 0);
            foreach (var (key, c) in targets)
            {
                if (_tokens[key] is SolidColorBrush b) b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, duration) { EasingFunction = ease });
                else _tokens[key] = Live(c);
            }
        }

        Effective = effective;
        if (save)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PrefPath)!);
                File.WriteAllText(PrefPath, Preference);
            }
            catch { }
        }
        Changed?.Invoke(effective);
    }

    /// <summary>Pennello "vivo": un'animazione a durata zero lo tiene non congelabile.</summary>
    private static SolidColorBrush Live(Color c)
    {
        var b = new SolidColorBrush(c);
        b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, TimeSpan.Zero));
        return b;
    }

    private static LinearGradientBrush AccentFill()
    {
        if (_accentFill is not null) return _accentFill;
        _accentFill = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString("#FFD700"),
            (Color)ColorConverter.ConvertFromString("#FF8C00"),
            new Point(0, 0), new Point(1, 1));
        _accentFill.Freeze(); // identico nei due temi
        return _accentFill;
    }

    /// <summary>Colore finale di ogni token per il tema dato (palette + finiture + opzioni).</summary>
    private static Dictionary<string, Color> TargetColors(string theme)
    {
        var dark = theme == "dark";
        var t = _palettes[theme].ToDictionary(kv => kv.Key, kv => (Color)ColorConverter.ConvertFromString(kv.Value));
        Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        t["GlassRim"] = C(dark ? "#33FFFFFF" : "#1F000000");
        t["GlassField"] = C(dark ? "#40000000" : "#59FFFFFF");

        // Colori del grafico di avanzamento, validati con dataviz/validate_palette.js
        // (banda di luminosita', separazione CVD, contrasto) separatamente per ogni tema.
        foreach (var (key, hex) in dark
                     ? new[] { ("ChartDone", "#3FAE62"), ("ChartRun", "#5B8DEF"), ("ChartWait", "#D67A00"), ("ChartErr", "#D9466A") }
                     : new[] { ("ChartDone", "#1F9D45"), ("ChartRun", "#2E6BE6"), ("ChartWait", "#FF8C00"), ("ChartErr", "#C0392B") })
            t[key] = C(hex);

        if (Translucent)
        {
            // Vetro: dietro c'e' la sfocatura di Windows (Ui/Backdrop). Alfa abbastanza basso da
            // vedere il vetro, abbastanza alto da tenere il contrasto del testo.
            static System.Windows.Media.Color A(System.Windows.Media.Color c, byte a) => System.Windows.Media.Color.FromArgb(a, c.R, c.G, c.B);
            t["BgWindow"] = A(t["BgGrouped"], (byte)(dark ? 0xB8 : 0xA8));
            t["BgSidebarGlass"] = A(t["BgSidebar"], (byte)(dark ? 0xA0 : 0x90));
            t["BgToolbarGlass"] = A(t["BgToolbar"], (byte)(dark ? 0xA0 : 0x90));
        }
        // Superfici opache di default: i token *Glass hanno senso solo sopra una sfocatura acrilica.
        if (!Translucent)
        {
            t["BgWindow"] = t["BgGrouped"];
            t["BgToolbarGlass"] = t["BgToolbar"];
            t["BgSidebarGlass"] = t["BgSidebar"];
            t["BgContentGlass"] = t["BgContent"];
            t["Panel"] = t["BgContent"];
            t["PanelSoft"] = t["BgFieldAlt"];
        }
        return t;
    }

    public static void Toggle() => Set(Effective == "dark" ? "light" : "dark");

    /// <summary>Finestra traslucida (vetro) invece che opaca. Default: opaca.</summary>
    public static bool Translucent { get; private set; }

    /// <summary>La finestra principale applica/toglie la sfocatura di Windows quando cambia.</summary>
    public static event Action<bool>? TranslucentChanged;

    public static void SetTranslucent(bool value)
    {
        if (Translucent == value) return;
        Translucent = value;
        Set(Preference, save: false);
        TranslucentChanged?.Invoke(value);
    }

    /// <summary>Colore del tema corrente (per cio' che non puo' essere una DynamicResource).</summary>
    public static Color Color(string key) => (Color)ColorConverter.ConvertFromString(_palettes[Effective][key]);
}
