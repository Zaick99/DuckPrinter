using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DuckPrinterManager.Core.Models;
using DuckPrinterManager.Core.Native;
using DuckPrinterManager.Core.Services;
using DuckPrinterManager.Dialogs;
using DuckPrinterManager.Theme;
using DuckPrinterManager.Ui;
using Microsoft.Win32;

namespace DuckPrinterManager.Pages;

public sealed class DriverItem
{
    public required string DriverName { get; init; }
    public required string Manufacturer { get; init; }
    public required string Architecture { get; init; }
    public required string InfPath { get; init; }
    /// <summary>Cartella copiata sul PC remoto (radice del pacchetto driver).</summary>
    public required string RootDir { get; init; }
    public string InfFile => Path.GetFileName(InfPath);
    public string InfRelPath => Path.GetRelativePath(RootDir, InfPath);
}

/// <summary>
/// Deploy come procedura guidata. Installazione: Operazione → Computer → Driver → Stampante → Riepilogo.
/// Rimozione: Operazione → Computer → Cosa togliere → Riepilogo. Si avanza solo se il passo e' valido;
/// dall'indicatore in alto si torna a qualunque passo gia' visto.
/// </summary>
public partial class DeployPage : UserControl, IPage
{
    private enum Step { Action, Pcs, Driver, Printer, Remove, Summary }

    private List<DriverItem> _drivers = [];
    private DriverItem? _driver;
    private HostParseResult _def = new(), _noDef = new();
    private int _index;
    private bool _done;
    private string _defaultMode = DefaultModes.Auto;
    private string _pre = "none";

    private static readonly Func<long?>[] StartOptions =
    [
        () => null,
        () => DuckCore.Now + 1800,
        () => DuckCore.Now + 3600,
        () => At(DateTime.Today.AddHours(20)),
        () => At(DateTime.Today.AddDays(1).AddHours(7)),
    ];

    private static long At(DateTime local) =>
        new DateTimeOffset(local < DateTime.Now ? local.AddDays(1) : local).ToUnixTimeSeconds();

    public DeployPage()
    {
        InitializeComponent();
        LoadDefaults();
        AppHost.ConfigChanged += LoadDefaults;
        UpdateAll();
        Go(0, forward: true, animate: false);
    }

    private void LoadDefaults()
    {
        var c = AppHost.Config;
        if (string.IsNullOrWhiteSpace(TxtName.Text)) TxtName.Text = c.PrinterNameTemplate;
        SelectDefaultMode(c.DefaultMode == DefaultModes.None ? DefaultModes.Auto : c.DefaultMode);
        ApplyProcedureDefaults();
    }

    /// <summary>Opzioni gia' attive all'apertura (Impostazioni > Procedura): chi fa sempre lo stesso deploy conferma e basta.</summary>
    private void ApplyProcedureDefaults()
    {
        var c = AppHost.Config;
        ChkSetPrt.IsChecked = c.RestartSetPrt;
        ChkRemovePorts.IsChecked = c.RemovePortsDefault;
        ChkRemoveSetPrt.IsChecked = c.RemoveRestartSetPrt;
        ChkRemoveAll.IsChecked = false;
        SelectPre(c.DeployPreRemove is "same" or "all" ? c.DeployPreRemove : "none");
        foreach (var rb in StartBar.Children.OfType<RadioButton>()) rb.IsChecked = rb.Tag as string == c.DeployStartDefault.ToString();
    }

    public void OnShown() { }

    private Window Owner => Window.GetWindow(this);
    private bool Install => ChoiceInstall.IsChecked == true;

    // ================================================================ passi
    private List<(Step Step, string Label, FrameworkElement Panel)> Steps => Install
        ? [(Step.Action, "Operazione", PanelAction), (Step.Pcs, "Computer", PanelPcs), (Step.Driver, "Driver", PanelDriver), (Step.Printer, "Stampante", PanelPrinter), (Step.Summary, "Riepilogo", PanelSummary)]
        : [(Step.Action, "Operazione", PanelAction), (Step.Pcs, "Computer", PanelPcs), (Step.Remove, "Cosa togliere", PanelRemove), (Step.Summary, "Riepilogo", PanelSummary)];

    private FrameworkElement[] AllPanels => [PanelAction, PanelPcs, PanelDriver, PanelPrinter, PanelRemove, PanelSummary, PanelDone];

    private void Go(int index, bool forward, bool animate = true)
    {
        var steps = Steps;
        index = Math.Clamp(index, 0, steps.Count - 1);
        _index = index;
        _done = false;
        var target = steps[index];

        if (target.Step == Step.Summary) BuildSummary();
        if (target.Step == Step.Pcs) UpdatePcsTexts();
        if (target.Step == Step.Printer) UpdatePreview();
        if (target.Step == Step.Remove) UpdateRemoveSummary();
        SwitchTo(target.Panel, forward, animate);

        BuildStepper();
        NavBar.Visibility = Visibility.Visible;
        BtnBack.Visibility = index == 0 ? Visibility.Hidden : Visibility.Visible;
        TxtStepInfo.Text = $"Passo {index + 1} di {steps.Count}";
        var last = index == steps.Count - 1;
        TxtNext.Text = last ? (Install ? "Programma installazione" : "Programma rimozione") : "Avanti";
        GlyphNext.Text = last ? "" : "";
        BtnNext.Style = (Style)FindResource(last && !Install ? "DangerBtn" : "PrimaryBtn");

        Dispatcher.BeginInvoke(() =>
        {
            switch (target.Step)
            {
                case Step.Pcs: TxtHostsDef.Focus(); break;
                case Step.Driver: if (_drivers.Count > 0) TxtDriverSearch.Focus(); break;
                case Step.Printer: (TxtAddress.Text.Length == 0 ? TxtAddress : TxtName).Focus(); break;
                case Step.Remove: TxtRemove.Focus(); break;
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Il pannello vecchio esce dal lato opposto mentre il nuovo entra (avanti da destra, indietro da sinistra).</summary>
    private void SwitchTo(FrameworkElement panel, bool forward, bool animate)
    {
        var old = AllPanels.FirstOrDefault(p => p != panel && p.Visibility == Visibility.Visible);
        foreach (var p in AllPanels) if (p != panel && p != old) p.Visibility = Visibility.Collapsed;
        if (!animate || old is null)
        {
            if (old is not null) old.Visibility = Visibility.Collapsed;
            panel.Visibility = Visibility.Visible;
            return;
        }
        Fx.Carousel(old, panel, forward);
    }

    private void BuildStepper()
    {
        Stepper.Children.Clear();
        var steps = Steps;
        for (int i = 0; i < steps.Count; i++)
        {
            var state = _done || i < _index ? "done" : i == _index ? "current" : "todo";
            var circle = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5) };
            var num = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, FontWeight = FontWeights.SemiBold };
            switch (state)
            {
                case "done":
                    circle.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
                    circle.SetResourceReference(Border.BorderBrushProperty, "Accent");
                    num.Text = "";
                    num.FontFamily = (FontFamily)FindResource("FIcons");
                    num.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                    break;
                case "current":
                    circle.SetResourceReference(Border.BackgroundProperty, "AccentFill");
                    circle.SetResourceReference(Border.BorderBrushProperty, "Accent");
                    num.Text = (i + 1).ToString();
                    num.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                    break;
                default:
                    circle.SetResourceReference(Border.BorderBrushProperty, "BorderControl");
                    num.Text = (i + 1).ToString();
                    num.SetResourceReference(TextBlock.ForegroundProperty, "LabelTertiary");
                    break;
            }
            circle.Child = num;
            var label = new TextBlock { Text = steps[i].Label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontWeight = state == "current" ? FontWeights.SemiBold : FontWeights.Normal };
            label.SetResourceReference(TextBlock.ForegroundProperty, state == "todo" ? "LabelTertiary" : "Label");
            var content = new StackPanel { Orientation = Orientation.Horizontal, Children = { circle, label } };
            var btn = new Button { Content = content, Style = (Style)FindResource("Toolbtn"), Padding = new Thickness(6, 0, 8, 0), Height = 32, Tag = i, Cursor = Cursors.Hand };
            btn.Click += (s, _) => JumpTo((int)((Button)s).Tag);
            Stepper.Children.Add(btn);

            if (state == "current") Fx.Pop(circle);
            if (i < steps.Count - 1)
            {
                var line = new Border { Width = 34, Height = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
                line.SetResourceReference(Border.BackgroundProperty, "Separator");
                Stepper.Children.Add(line);
                // il tratto appena completato si "riempie" da sinistra a destra
                if (_done || i < _index)
                {
                    var fill = new Border { Height = 2, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left, Width = 34 };
                    fill.SetResourceReference(Border.BackgroundProperty, "Accent");
                    line.Child = fill;
                    if (i == _index - 1) fill.BeginAnimation(WidthProperty, Motion.Slide(0, 34, 420, Motion.Ease("Quint")));
                }
            }
        }
    }

    private void JumpTo(int target)
    {
        if (target == _index && !_done) return;
        if (target > _index)
        {
            // in avanti solo se tutti i passi intermedi sono a posto
            for (int i = _index; i < target; i++)
                if (!Validate(Steps[i].Step, quiet: false)) { if (i != _index) Go(i, true); return; }
        }
        Go(target, target > _index);
    }

    private void OnBack(object sender, RoutedEventArgs e) { if (_index > 0) Go(_index - 1, forward: false); }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        var step = Steps[_index].Step;
        if (!Validate(step, quiet: false)) return;
        if (step == Step.Summary) { _ = Program(); return; }
        Go(_index + 1, forward: true);
    }

    private void OnActionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        BuildStepper();
        TxtStepInfo.Text = $"Passo {_index + 1} di {Steps.Count}";
    }

    // ================================================================ validazione
    private bool Validate(Step step, bool quiet)
    {
        void Fail(FrameworkElement el, string title, string msg)
        {
            if (quiet) return;
            Fx.Shake(el);
            Toast.Warning(title, msg);
        }
        switch (step)
        {
            case Step.Pcs:
                if (AllHosts.Count > 0) return true;
                Fail(TxtHostsDef, "Nessun PC", "Scrivi o incolla almeno un nome PC.");
                return false;
            case Step.Driver:
                if (_driver is not null) return true;
                Fail(_drivers.Count == 0 ? DropZone : LstDrivers, "Driver mancante", _drivers.Count == 0 ? "Trascina un file .inf o scegli la cartella del driver." : "Scegli il modello dall'elenco.");
                return false;
            case Step.Printer:
                if (ChkDriverOnly.IsChecked == true) return true;
                if (ValidAddress is null) { Fail(TxtAddress, "Indirizzo stampante", "Indica un solo IP, hostname o FQDN valido."); return false; }
                if (RenderedName.Length == 0) { Fail(TxtName, "Nome stampante", "Il nome della stampante risulta vuoto."); return false; }
                return true;
            case Step.Remove:
                if (ChkRemoveAll.IsChecked == true) return true;
                var t = DuckCore.ParseHosts(TxtRemove.Text);
                if (t.Valid.Count > 0 && t.Invalid.Count == 0) return true;
                Fail(TxtRemove, "Indirizzo da togliere", t.Invalid.Count > 0 ? "Non validi: " + string.Join(", ", t.Invalid) : "Indica IP, hostname o FQDN, oppure attiva \"Togli TUTTE le stampanti di rete\".");
                return false;
            case Step.Summary:
                if (!Install || _pre != "other") return true;
                var o = DuckCore.ParseHosts(TxtPreOther.Text);
                if (o.Valid.Count > 0 && o.Invalid.Count == 0) return true;
                Fail(TxtPreOther, "Stampante da sostituire", "Indica IP, hostname o FQDN della stampante da togliere prima, oppure scegli un'altra opzione.");
                return false;
            default:
                return true;
        }
    }

    // ================================================================ computer
    private List<string> DefHosts => _def.Valid;
    private List<string> NoDefHosts => _noDef.Valid.Where(h => !_def.Valid.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();
    private List<string> AllHosts => DefHosts.Concat(NoDefHosts).ToList();

    private void UpdatePcsTexts()
    {
        TxtPcsExplain.Text = Install
            ? "Due elenchi: a sinistra i PC dove la stampante diventa la predefinita, a destra quelli con installazione normale. Incollando un elenco, i nomi con l'asterisco finiscono a sinistra."
            : "La rimozione vale per tutti i PC dei due elenchi. Puoi incollare lo stesso elenco usato per l'installazione.";
        TxtDefHeader.Text = Install ? "Con stampante predefinita" : "PC da cui rimuovere";
        // rimozione: un solo elenco a tutta larghezza; la predefinita non conta
        NoDefColumn.Visibility = Install ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(DefColumn, Install ? 1 : 3);
        DefLegend.Visibility = Install ? Visibility.Visible : Visibility.Collapsed;
        DefDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, Install ? "Accent" : "Red");
        TxtHostsDef.Tag = Install ? "un PC per riga (asterisco = con predefinita)" : "un PC per riga";
        if (!Install && _noDef.Valid.Count > 0)
        {
            // i PC dell'elenco "installazione normale" non sparirebbero nel nulla: confluiscono nell'unico elenco
            var merged = AllHosts;
            TxtHostsNoDef.Clear();
            TxtHostsDef.Text = string.Join(Environment.NewLine, merged);
            Toast.Info("Elenchi uniti", $"Per la rimozione basta un elenco: {merged.Count} PC.");
        }
    }

    private void OnHostsChanged(object sender, TextChangedEventArgs e)
    {
        // l'asterisco serve solo a smistare un elenco incollato: dentro gli elenchi si ignora
        _def = DuckCore.ParseHosts(TxtHostsDef.Text.Replace("*", ""));
        _noDef = DuckCore.ParseHosts(TxtHostsNoDef.Text.Replace("*", ""));
        UpdateAll();
    }

    private void UpdateAll()
    {
        if (TxtCountDef is null) return;
        Fx.CountTo(TxtCountDef, _def.Valid.Count);
        Fx.CountTo(TxtCountNoDef, NoDefHosts.Count);
        var invalid = _def.Invalid.Concat(_noDef.Invalid).ToList();
        var both = _noDef.Valid.Count - NoDefHosts.Count;
        if (invalid.Count > 0)
        {
            TxtHostsInfo.Text = $"Nomi non validi (ignorati): {string.Join(", ", invalid.Take(12))}{(invalid.Count > 12 ? "..." : "")}";
            TxtHostsInfo.SetResourceReference(TextBlock.ForegroundProperty, "Red");
        }
        else
        {
            TxtHostsInfo.Text = AllHosts.Count == 0 ? "" : $"{AllHosts.Count} PC in totale" + (both > 0 ? $" · {both} presenti in entrambi gli elenchi: vale la predefinita" : "");
            TxtHostsInfo.SetResourceReference(TextBlock.ForegroundProperty, "LabelTertiary");
        }
    }

    /// <summary>Divide un blocco di testo fra i due elenchi: l'asterisco marca chi vuole la predefinita.</summary>
    private void AddHostsFromText(string text)
    {
        var pieces = text.Split(new[] { ' ', '\t', '\r', '\n', ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
        var arrived = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var invalid = new List<string>();
        // in rimozione l'asterisco non conta: tutto finisce nell'unico elenco
        var single = !Install;
        foreach (var p in pieces)
        {
            var wantsDefault = !single && p.Contains('*');
            var name = p.Replace("*", "").Trim().Trim('"', '\'', '.', '-');
            if (name.Length == 0) continue;
            var parsed = DuckCore.ParseHosts(name);
            if (parsed.Valid.Count != 1) { invalid.Add(name); continue; }
            name = parsed.Valid[0];
            if (arrived.TryGetValue(name, out var had)) arrived[name] = had || wantsDefault;
            else { arrived[name] = wantsDefault; order.Add(name); }
        }
        if (order.Count == 0) { Toast.Warning("Nessun PC", "Nel testo non c'e' nessun nome host valido."); return; }

        var def = _def.Valid.Where(h => !arrived.ContainsKey(h)).ToList();
        var noDef = _noDef.Valid.Where(h => !arrived.ContainsKey(h)).ToList();
        if (single) { def.AddRange(noDef.Where(h => !def.Contains(h, StringComparer.OrdinalIgnoreCase))); noDef.Clear(); }
        int nDef = 0, nNo = 0;
        foreach (var h in order)
        {
            if (arrived[h] || single) { def.Add(h); nDef++; } else { noDef.Add(h); nNo++; }
        }
        TxtHostsDef.Text = string.Join(Environment.NewLine, def);
        TxtHostsNoDef.Text = string.Join(Environment.NewLine, noDef);
        Toast.Info($"{nDef + nNo} PC aggiunti", (Install ? $"{nDef} con predefinita, {nNo} senza" : "") + (invalid.Count > 0 ? $" · scartati: {string.Join(", ", invalid.Take(6))}" : ""));
        AppHost.Core.Log("INFO", $"Elenco incollato: {nDef + nNo} host ({nDef} con predefinita, {nNo} senza)");
    }

    private void OnPasteHosts(object sender, RoutedEventArgs e)
    {
        string text = "";
        try { text = Clipboard.GetText(); } catch { }
        if (string.IsNullOrWhiteSpace(text)) { Toast.Warning("Appunti vuoti", "Copia prima l'elenco degli host (da Excel, mail, file...)."); return; }
        AddHostsFromText(text);
    }

    private void OnHostsFromFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Elenchi (*.txt;*.csv)|*.txt;*.csv|Tutti i file|*.*", Title = "Elenco PC (asterisco = con predefinita)" };
        if (dlg.ShowDialog(Owner) == true) AddHostsFromText(File.ReadAllText(dlg.FileName));
    }

    private void OnClearDef(object sender, RoutedEventArgs e) => TxtHostsDef.Clear();
    private void OnClearNoDef(object sender, RoutedEventArgs e) => TxtHostsNoDef.Clear();

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        if (AllHosts.Count == 0) return;
        if (!DuckDialog.Confirm(Owner, Install ? "Svuota elenchi" : "Svuota elenco", Install ? "Svuotare entrambi gli elenchi PC?" : "Svuotare l'elenco dei PC?", "Svuota")) return;
        TxtHostsDef.Clear();
        TxtHostsNoDef.Clear();
    }

    // ================================================================ driver
    private async void OnLoadInf(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Driver di stampa (*.inf)|*.inf", Multiselect = true, Title = "Seleziona uno o piu' file INF" };
        if (dlg.ShowDialog(Owner) != true) return;
        await LoadInfs(dlg.FileNames.Select(f => (f, Path.GetDirectoryName(f)!)).ToList(), $"{dlg.FileNames.Length} file INF");
    }

    private async void OnLoadFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Cartella del pacchetto driver (viene copiata per intero sui PC)" };
        if (dlg.ShowDialog(Owner) == true) await LoadFolder(dlg.FolderName);
    }

    private async Task LoadFolder(string root)
    {
        var infs = await Task.Run(() => Directory.EnumerateFiles(root, "*.inf", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive }).ToList());
        if (infs.Count == 0) { Fx.Shake(DropZone); Toast.Warning("Nessun INF", $"La cartella {Path.GetFileName(root)} non contiene file .inf"); return; }
        await LoadInfs(infs.Select(f => (f, root)).ToList(), Path.GetFileName(root));
    }

    // ---- trascinamento: file .inf e/o cartelle del driver
    private static string[]? DroppedPaths(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = DroppedPaths(e)?.Any(p => Directory.Exists(p) || p.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)) == true;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragEnter(object sender, DragEventArgs e) => DropHighlight(true);
    private void OnDragLeave(object sender, DragEventArgs e) => DropHighlight(false);

    private void DropHighlight(bool on)
    {
        DropRect.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, on ? "Accent" : "BorderControl");
        DropRect.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, on ? "AccentSoft" : "BgFieldAlt");
        Fx.Spring(DropImage, on ? 1.14 : 1);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight(false);
        e.Handled = true;
        var paths = DroppedPaths(e);
        if (paths is null) return;
        if (!Install) ChoiceInstall.IsChecked = true;
        var folder = paths.FirstOrDefault(Directory.Exists);
        if (folder is not null) await LoadFolder(folder);
        else
        {
            var infs = paths.Where(p => p.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
            if (infs.Count == 0) { Toast.Warning("File non valido", "Trascina file .inf o la cartella del driver."); return; }
            await LoadInfs(infs.Select(f => (f, Path.GetDirectoryName(f)!)).ToList(), $"{infs.Count} file INF");
        }
        var drv = Steps.FindIndex(s => s.Step == Step.Driver);
        if (_index != drv) Go(drv, drv > _index);
    }

    private async Task LoadInfs(List<(string Inf, string Root)> files, string source)
    {
        TxtInfSource.Text = "Lettura INF in corso...";
        var results = await Task.Run(() => files.Select(f => (f.Root, Result: SafeRead(f.Inf))).ToList());

        _drivers = results
            .SelectMany(r => r.Result.Drivers.Select(d => new DriverItem
            {
                DriverName = d.DriverName,
                Manufacturer = string.IsNullOrEmpty(d.Manufacturer) ? r.Result.Provider : d.Manufacturer,
                Architecture = d.Architecture,
                InfPath = d.InfPath,
                RootDir = r.Root,
            }))
            .GroupBy(d => d.DriverName + "|" + d.InfPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .ToList();

        var methods = string.Join(", ", results.Select(r => r.Result.Method).Distinct());
        var version = results.Where(r => r.Result.DriverVer.Length > 0).Select(r => $"{r.Result.Provider} {r.Result.DriverVer}").FirstOrDefault();
        var warnings = results.SelectMany(r => r.Result.Warnings.Select(w => $"{Path.GetFileName(r.Result.InfPath)}: {w}")).ToList();
        TxtInfSource.Text = $"{source}: {_drivers.Count} modelli letti con {methods}." + (version is null ? "" : $" Versione {version}.") + (warnings.Count > 0 ? $" {warnings.Count} avvisi (passa col mouse)." : "");
        TxtInfSource.ToolTip = warnings.Count > 0 ? string.Join("\n", warnings.Take(20)) : null;
        AppHost.Core.Log("INFO", $"INF: {_drivers.Count} modelli letti da {results.Count} file ({methods})");
        foreach (var w in warnings.Take(20)) AppHost.Core.Log("WARN", "INF " + w);

        _driver = null;
        TxtDriverSearch.Clear();
        ApplyDriverFilter();
        if (_drivers.Count == 1) LstDrivers.SelectedIndex = 0;
        if (_drivers.Count == 0) Toast.Warning("Nessun modello", "Negli INF non e' stato trovato nessun modello di stampante.");
        else Toast.Info($"{_drivers.Count} modelli trovati", _drivers.Count == 1 ? _drivers[0].DriverName : "Scegli il modello dall'elenco.");
        Fx.Cascade(LstDrivers);
    }

    private static InfReadResult SafeRead(string path)
    {
        try { return InfParser.Read(path); }
        catch (Exception ex)
        {
            var r = new InfReadResult { InfPath = path, Method = "errore" };
            r.Warnings.Add(ex.Message);
            return r;
        }
    }

    private void OnDriverSearch(object sender, TextChangedEventArgs e) => ApplyDriverFilter();

    private void ApplyDriverFilter()
    {
        var f = TxtDriverSearch.Text.Trim();
        var items = f.Length == 0 ? _drivers
            : _drivers.Where(d => d.DriverName.Contains(f, StringComparison.OrdinalIgnoreCase) || d.Manufacturer.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        LstDrivers.ItemsSource = items;
        if (_driver is not null && items.Contains(_driver)) LstDrivers.SelectedItem = _driver;
        DriversEmpty.Visibility = _drivers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDriverSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LstDrivers.SelectedItem is DriverItem d) _driver = d;
        UpdatePreview();
    }

    // ================================================================ stampante
    private void OnInputs(object sender, RoutedEventArgs e) => UpdatePreview();

    private string RenderedName => Naming.RenderPrinterName(TxtName.Text, _driver?.DriverName ?? "", TxtAddress.Text.Trim());

    private string? ValidAddress
    {
        get
        {
            var a = DuckCore.ParseHosts(TxtAddress.Text);
            return a.Valid.Count == 1 && a.Invalid.Count == 0 ? a.Valid[0] : null;
        }
    }

    private void UpdatePreview()
    {
        if (TxtPreviewName is null) return;
        var only = ChkDriverOnly.IsChecked == true;
        TxtPreviewName.Text = _driver is null ? "Nessun driver scelto" : only ? _driver.DriverName : RenderedName;
        TxtPreviewAddr.Text = only ? "solo driver" : (TxtAddress.Text.Trim().Length > 0 ? TxtAddress.Text.Trim() : "indirizzo da indicare");
        TxtPreviewDriver.Text = _driver is null ? "" : $"{_driver.Manufacturer} · {_driver.InfFile} · {_driver.Architecture}";
        TxtNamePreview.Text = _driver is null ? "{DRIVERNAME} diventa il nome del driver scelto" : "→ " + RenderedName;
        TxtNamePreview.SetResourceReference(TextBlock.ForegroundProperty, _driver is null ? "LabelTertiary" : "Accent");
        TxtName.IsEnabled = TxtAddress.IsEnabled = !only;
    }

    private void SelectDefaultMode(string mode)
    {
        foreach (var rb in DefaultBar.Children.OfType<RadioButton>())
            rb.IsChecked = rb.Tag as string == mode;
    }

    private void OnDefaultMode(object sender, RoutedEventArgs e)
    {
        _defaultMode = (string)((RadioButton)sender).Tag;
        TxtDefaultHint.Text = _defaultMode switch
        {
            DefaultModes.SetPrt => "Aggiorna set_prt in tutte le versioni (printui, SetDefaultPrinter, printers.cfg). Solo per i PC dell'elenco \"con predefinita\".",
            DefaultModes.Windows => "Imposta la predefinita di Windows per l'utente collegato al PC.",
            DefaultModes.Both => "Aggiorna set_prt e imposta anche la predefinita di Windows.",
            _ => "set_prt se e' presente sul PC, altrimenti la predefinita di Windows. Solo per i PC \"con predefinita\".",
        };
    }

    // ================================================================ rimozione
    private void OnRemoveInputs(object sender, RoutedEventArgs e) => UpdateRemoveSummary();

    private void UpdateRemoveSummary()
    {
        if (TxtRemoveWhat is null) return;
        var all = ChkRemoveAll.IsChecked == true;
        TxtRemove.IsEnabled = !all;
        TxtRemoveWhat.Text = all
            ? "Verranno tolte tutte le code di rete (TCP/IP, WSD, IPP) e le loro porte. Stampanti locali e virtuali non vengono toccate."
            : "Le porte che puntano all'indirizzo vengono confrontate anche per FQDN e IP risolti via DNS.";
    }

    // ================================================================ riepilogo
    private void BuildSummary()
    {
        SummaryRows.Children.Clear();
        var install = Install;
        ImgSummary.Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/art/{(install ? "install" : "remove")}.png"));
        var nDef = DefHosts.Count;
        var nNo = NoDefHosts.Count;
        TxtSummaryTitle.Text = install ? "Pronto per l'installazione" : "Pronto per la rimozione";
        TxtSummaryText.Text = $"Verra' creato un task per ciascuno dei {nDef + nNo} PC. I PC spenti vengono ricontrollati ogni {AppHost.Config.TaskCheckMinutes} minuti finche' non si accendono.";

        static string Preview(List<string> hosts) => hosts.Count == 0 ? "nessuno" : string.Join(", ", hosts.Take(8)) + (hosts.Count > 8 ? $"  … e altri {hosts.Count - 8}" : "");
        static string? Full(List<string> hosts) => hosts.Count <= 8 ? null : string.Join("\n", hosts.Take(400)) + (hosts.Count > 400 ? $"\n… e altri {hosts.Count - 400}" : "");
        UpdateAccountNote();
        PreRemoveSection.Visibility = install ? Visibility.Visible : Visibility.Collapsed;
        PreSame.IsEnabled = ChkDriverOnly.IsChecked != true;
        if (!PreSame.IsEnabled && _pre == "same") SelectPre("none");
        UpdatePreHint();
        int Idx(Step s) => Steps.FindIndex(x => x.Step == s);

        AddSummary("Operazione", install ? "Installazione stampante" : (ChkRemoveAll.IsChecked == true ? "Rimozione di tutte le stampanti di rete" : "Rimozione stampante"), 0);
        if (install)
        {
            AddSummary($"PC con predefinita ({nDef})", Preview(DefHosts), Idx(Step.Pcs), mono: true, tooltip: Full(DefHosts));
            AddSummary($"PC senza predefinita ({nNo})", Preview(NoDefHosts), Idx(Step.Pcs), mono: true, tooltip: Full(NoDefHosts));
            AddSummary("Driver", _driver is null ? "-" : $"{_driver.DriverName}\n{_driver.InfFile} · {_driver.Architecture}", Idx(Step.Driver));
            if (ChkDriverOnly.IsChecked == true) AddSummary("Stampante", "Solo driver: nessuna porta e nessuna coda", Idx(Step.Printer));
            else
            {
                AddSummary("Nome", RenderedName, Idx(Step.Printer));
                AddSummary("Indirizzo", ValidAddress ?? "-", Idx(Step.Printer), mono: true);
                AddSummary("Predefinita via", nDef == 0 ? "non usata (nessun PC con predefinita)" : DefaultModes.All.First(m => m.Key == _defaultMode).Label, Idx(Step.Printer));
            }
            AddSummary("set_prt", ChkSetPrt.IsChecked == true ? "rilanciato al termine" : "non rilanciato", Idx(Step.Printer));
        }
        else
        {
            AddSummary($"PC ({nDef + nNo})", Preview(AllHosts), Idx(Step.Pcs), mono: true, tooltip: Full(AllHosts));
            if (ChkRemoveAll.IsChecked != true) AddSummary("Indirizzi", string.Join(", ", DuckCore.ParseHosts(TxtRemove.Text).Valid), Idx(Step.Remove), mono: true);
            AddSummary("Opzioni", string.Join(" · ", new[] { ChkRemovePorts.IsChecked == true ? "togli le porte" : "lascia le porte", ChkRemoveSetPrt.IsChecked == true ? "rilancia set_prt" : "set_prt invariato" }), Idx(Step.Remove));
        }
    }

    /// <summary>
    /// I task girano sui PC remoti con l'account AD della sessione e/o con l'utente Windows:
    /// serve un amministratore locale, altrimenti "Accesso negato". Lo diciamo prima di programmare.
    /// </summary>
    private void UpdateAccountNote()
    {
        var win = $"{Environment.UserDomainName}\\{Environment.UserName}";
        if (AppHost.Ad.Credentials is { } c)
        {
            var ad = TaskExecutor.QualifyUser(c.User, AppHost.Config.Domain);
            TxtAccountNote.Text = AppHost.Config.DeployUseAdCredentials
                ? $"I task useranno {ad}; se riceve 'accesso negato' si riprova con {win}."
                : $"I task useranno {win}; se riceve 'accesso negato' si riprova con {ad}.";
            BtnAccountLogin.Visibility = Visibility.Collapsed;
        }
        else
        {
            TxtAccountNote.Text = $"Non hai effettuato l'accesso: i task useranno il tuo utente Windows ({win}). " +
                                  "Se non e' amministratore dei PC riceverai 'accesso negato'.";
            BtnAccountLogin.Visibility = Visibility.Visible;
        }
    }

    private void OnSummaryLogin(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mw && mw.EnsureLogin()) UpdateAccountNote();
    }

    private void AddSummary(string label, string value, int step, bool mono = false, string? tooltip = null)
    {
        if (SummaryRows.Children.Count > 0) SummaryRows.Children.Add(new System.Windows.Shapes.Rectangle { Style = (Style)FindResource("RowSep") });
        var g = new Grid { Style = (Style)FindResource("FormRow"), Margin = new Thickness(14, 7, 8, 7) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new TextBlock { Text = label, Style = (Style)FindResource("TxtCaption"), VerticalAlignment = VerticalAlignment.Center };
        var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        v.SetResourceReference(TextBlock.ForegroundProperty, "Label");
        if (mono) v.FontFamily = (FontFamily)FindResource("FMono");
        if (tooltip is not null)
        {
            v.ToolTip = tooltip;
            v.TextDecorations = TextDecorations.Underline;
            v.Cursor = Cursors.Help;
        }
        var edit = new Button { Content = "Modifica", Style = (Style)FindResource("GhostBtn"), Tag = step, VerticalAlignment = VerticalAlignment.Center };
        edit.Click += (s, _) => Go((int)((Button)s).Tag, forward: false);
        Grid.SetColumn(v, 1);
        Grid.SetColumn(edit, 2);
        g.Children.Add(l);
        g.Children.Add(v);
        g.Children.Add(edit);
        SummaryRows.Children.Add(g);
    }

    private void SelectPre(string key)
    {
        foreach (var rb in PreBar.Children.OfType<RadioButton>()) rb.IsChecked = rb.Tag as string == key;
    }

    private void OnPreRemove(object sender, RoutedEventArgs e)
    {
        _pre = (string)((RadioButton)sender).Tag;
        // scatta anche durante InitializeComponent (voce preselezionata nel XAML): i controlli
        // dichiarati piu' sotto non esistono ancora
        if (TxtPreOther is null || PreRemoveSection is null) return;
        var showOther = _pre == "other";
        var appearing = showOther && TxtPreOther.Visibility != Visibility.Visible;
        // il riquadro cambia altezza (campo indirizzo, spiegazione di lunghezza diversa): scivola invece di saltare
        Fx.AnimateHeight(PreRemoveSection, () =>
        {
            TxtPreOther.Visibility = showOther ? Visibility.Visible : Visibility.Collapsed;
            UpdatePreHint();
        });
        if (!IsLoaded) return;
        if (appearing) Fx.FadeIn(TxtPreOther);
        Fx.FadeIn(TxtPreHint, 300, 90);
        if (showOther) Dispatcher.BeginInvoke(() => TxtPreOther.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void UpdatePreHint()
    {
        if (TxtPreHint is null) return;
        var addr = ValidAddress ?? "l'indirizzo della stampante";
        TxtPreHint.Text = _pre switch
        {
            "same" => $"Su ogni PC viene tolta prima la stampante collegata a {addr} (anche se ha un altro nome o un'altra porta con lo stesso IP/FQDN), poi si installa la nuova.",
            "other" => "Su ogni PC viene tolta prima la stampante che punta all'indirizzo indicato (quella che stai sostituendo), poi si installa la nuova.",
            "all" => "Su ogni PC vengono tolte prima TUTTE le stampanti di rete; quelle locali e virtuali restano.",
            _ => "Le stampanti gia' presenti restano dove sono.",
        };
        TxtPreHint.SetResourceReference(TextBlock.ForegroundProperty, _pre == "all" ? "Red" : "LabelTertiary");
    }

    /// <summary>Task di rimozione da eseguire PRIMA dell'installazione sugli stessi PC (FIFO per host nel core).</summary>
    private List<NewTask> PreRemovalTasks()
    {
        if (!Install || _pre == "none") return [];
        List<string> targets = _pre switch
        {
            "same" => ValidAddress is { } a ? [a] : [],
            "other" => DuckCore.ParseHosts(TxtPreOther.Text).Valid,
            _ => [],
        };
        if (_pre != "all" && targets.Count == 0) return [];
        return DuckTasks.Remove(AllHosts, new TaskPayload
        {
            RemoveTargets = targets,
            RemoveAllNetwork = _pre == "all",
            RemovePorts = true,
            RestartSetPrt = false, // set_prt viene rilanciato dall'installazione che segue
        }).ToList();
    }

    private long? StartAt
    {
        get
        {
            var rb = StartBar.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
            return StartOptions[rb is null ? 0 : int.Parse((string)rb.Tag)]();
        }
    }

    // ================================================================ programmazione
    private async Task Program()
    {
        List<NewTask> tasks;
        string what;
        if (Install)
        {
            var d = _driver!;
            var driverOnly = ChkDriverOnly.IsChecked == true;
            var driverDir = d.RootDir;
            if (AppHost.Config.CacheDrivers)
            {
                BtnNext.IsEnabled = false;
                TxtNext.Text = "Copia driver...";
                try { driverDir = await Task.Run(() => DriverCache.Ensure(d.RootDir, AppPaths.DriverCache)); }
                catch (Exception ex) { DuckDialog.Error(Owner, "Copia driver", "Impossibile copiare i driver nella cache locale:\n" + ex.Message); return; }
                finally { BtnNext.IsEnabled = true; TxtNext.Text = "Programma installazione"; }
            }
            TaskPayload Payload(string mode) => new()
            {
                DriverName = d.DriverName,
                DriverDir = driverDir,
                InfRelPath = d.InfRelPath,
                PrinterName = driverOnly ? "" : RenderedName,
                PrinterAddress = driverOnly ? "" : ValidAddress ?? "",
                DefaultMode = driverOnly ? DefaultModes.None : mode,
                RestartSetPrt = ChkSetPrt.IsChecked == true,
                DriverOnly = driverOnly,
            };
            tasks = PreRemovalTasks()
                .Concat(DuckTasks.Deploy(DefHosts, Payload(_defaultMode)))
                .Concat(DuckTasks.Deploy(NoDefHosts, Payload(DefaultModes.None))).ToList();
            what = "installazione";
        }
        else
        {
            var all = ChkRemoveAll.IsChecked == true;
            if (all && !DuckDialog.Confirm(Owner, "Togliere tutte le stampanti di rete?",
                    $"Su {AllHosts.Count} PC verranno rimosse TUTTE le stampanti collegate a porte di rete. Le stampanti locali e virtuali restano.", "Togli tutte", danger: true))
                return;
            tasks = DuckTasks.Remove(AllHosts, new TaskPayload
            {
                RemoveTargets = all ? [] : DuckCore.ParseHosts(TxtRemove.Text).Valid,
                RemoveAllNetwork = all,
                RemovePorts = ChkRemovePorts.IsChecked == true,
                RestartSetPrt = ChkRemoveSetPrt.IsChecked == true,
            }).ToList();
            what = "rimozione";
        }

        var at = StartAt;
        foreach (var t in tasks) t.NextCheck = at;
        var rep = AppHost.Core.AddTasks(tasks);
        AppHost.Core.Log("TASK", $"Programmati {rep.Added.Count} task di {what}" + (rep.Duplicates.Count > 0 ? $" ({rep.Duplicates.Count} duplicati ignorati)" : ""));
        if (rep.Added.Count == 0)
        {
            Toast.Warning("Nessun task programmato", $"Tutti i PC avevano gia' lo stesso task in coda ({rep.Duplicates.Count}).");
            return;
        }
        ShowDone(rep, what, at);
    }

    private void ShowDone(AddReport rep, string what, long? at)
    {
        _done = true;
        var pre = Install && _pre != "none";
        TxtDoneTitle.Text = pre ? $"{rep.Added.Count} task programmati"
            : rep.Added.Count == 1 ? $"1 task di {what} programmato" : $"{rep.Added.Count} task di {what} programmati";
        TxtDoneText.Text = (at is null ? "Partono subito" : $"Partono non prima di {DateTimeOffset.FromUnixTimeSeconds(at.Value).LocalDateTime:dddd dd/MM HH:mm}")
                           + ", uno alla volta per PC e nell'ordine in cui li hai programmati."
                           + (pre ? " Su ogni PC la rimozione della vecchia stampante viene eseguita prima dell'installazione." : "")
                           + (rep.Duplicates.Count > 0 ? $" {rep.Duplicates.Count} PC avevano gia' lo stesso task e sono stati saltati." : "")
                           + (AppHost.Core.Revision().Paused ? " Attenzione: lo scheduler e' in pausa." : "");
        BtnSameOther.Visibility = Install ? Visibility.Visible : Visibility.Collapsed;
        NavBar.Visibility = Visibility.Collapsed;
        BuildStepper();
        SwitchTo(PanelDone, true, true);
        Fx.Celebrate(ImgDone);
    }

    private void OnOpenTasks(object sender, RoutedEventArgs e) => (Owner as MainWindow)?.Navigate("tasks");

    /// <summary>Ricomincia da capo, tenendo il driver caricato (si riparte spesso dalla stessa cartella).</summary>
    private void OnRestart(object sender, RoutedEventArgs e)
    {
        TxtHostsDef.Clear();
        TxtHostsNoDef.Clear();
        TxtAddress.Clear();
        TxtRemove.Clear();
        TxtName.Text = AppHost.Config.PrinterNameTemplate;
        ChkDriverOnly.IsChecked = false;
        SelectDefaultMode(AppHost.Config.DefaultMode == DefaultModes.None ? DefaultModes.Auto : AppHost.Config.DefaultMode);
        ApplyProcedureDefaults();
        Go(0, forward: false);
    }

    /// <summary>Stessa stampante, altri PC: si svuotano solo gli elenchi e si torna al passo Computer.</summary>
    private void OnSameOnOthers(object sender, RoutedEventArgs e)
    {
        TxtHostsDef.Clear();
        TxtHostsNoDef.Clear();
        Go(Steps.FindIndex(s => s.Step == Step.Pcs), forward: false);
    }
}
