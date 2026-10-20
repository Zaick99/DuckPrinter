using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DuckPrinterManager.Core.Models;
using DuckPrinterManager.Core.Native;
using DuckPrinterManager.Core.Services;
using DuckPrinterManager.Dialogs;
using DuckPrinterManager.Theme;
using DuckPrinterManager.Ui;
using Microsoft.Win32;

namespace DuckPrinterManager.Pages;

public partial class SettingsPage : UserControl, IPage
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        CmbDefault.ItemsSource = DefaultModes.All.Select(m => new { m.Key, m.Label }).ToList();
        AppHost.RepartiChanged += UpdateRepartiInfo;
        Load(AppHost.Config);
    }

    public void OnShown()
    {
        UpdateRepartiInfo();
        TxtSysInfo.Text = $"In uso: {AppHost.Ps.Executable}\nDati: {AppPaths.Root}\nCore Rust: {DuckCore.CallStatic("core.version").GetProperty("version").GetString()}";
    }

    private Window Owner => Window.GetWindow(this);

    // ---------------------------------------------------------------- carica / salva
    private void Load(AppConfig c)
    {
        _loading = true;
        TxtDomain.Text = c.Domain;
        TxtOu.Text = c.OUPath;
        CmbDc.Items.Clear();
        CmbDc.Items.Add("Automatico");
        if (!string.IsNullOrWhiteSpace(c.DomainController)) CmbDc.Items.Add(c.DomainController);
        CmbDc.SelectedItem = string.IsNullOrWhiteSpace(c.DomainController) ? "Automatico" : c.DomainController;
        TxtPattern.Text = c.NamePattern;
        TxtDigits.Text = c.IncrementalDigits.ToString();
        TxtTempPwd.Password = c.DefaultPassword;
        LstGroups.Items.Clear();
        foreach (var g in c.ADGroups) LstGroups.Items.Add(g);
        TxtReparti.Text = c.RepartiFile;
        ChkStrict.IsChecked = c.StrictReparti;
        ChkProbe.IsChecked = c.AutoLoginProbe;
        ChkForceWrite.IsChecked = c.ForceWriteMode;

        TxtNameTpl.Text = c.PrinterNameTemplate;
        CmbDefault.SelectedValue = c.DefaultMode;
        TxtSetPrtPaths.Text = string.Join(Environment.NewLine, c.SetPrtPaths);
        TxtSetPrtBat.Text = c.SetPrtBatch;
        TxtSetPrtProc.Text = c.SetPrtProcess;
        TxtRemoteDir.Text = c.RemoteWorkDir;
        ChkSetPrtDefault.IsChecked = c.RestartSetPrt;
        CmbPreRemove.SelectedValue = c.DeployPreRemove;
        ChkRemovePortsDefault.IsChecked = c.RemovePortsDefault;
        ChkRemoveSetPrtDefault.IsChecked = c.RemoveRestartSetPrt;
        CmbStartDefault.SelectedValue = c.DeployStartDefault.ToString();
        ChkCache.IsChecked = c.CacheDrivers;
        ChkAdCreds.IsChecked = c.DeployUseAdCredentials;

        TxtParallel.Text = c.TaskMaxParallel.ToString();
        TxtCheckMin.Text = c.TaskCheckMinutes.ToString();
        TxtMaxAge.Text = c.TaskMaxAgeHours.ToString();
        TxtBackoffBase.Text = c.TaskBackoffBase.ToString();
        TxtBackoffMax.Text = c.TaskBackoffMax.ToString();
        TxtTimeout.Text = c.RemoteTimeoutSeconds.ToString();
        TxtPing.Text = c.PingTimeoutMs.ToString();
        ChkTcp.IsChecked = c.PingTcpFallback;
        ChkAutoStart.IsChecked = c.SchedulerAutoStart;

        ChkDucks.IsChecked = c.DucksAnimate;
        ChkTranslucent.IsChecked = c.WindowTranslucent;
        TxtPs.Text = c.PowerShellPath;
        (ThemeManager.Preference switch { "light" => ThemeLight, "dark" => ThemeDark, _ => ThemeSystem }).IsChecked = true;
        OnSection(this, new RoutedEventArgs());
        _loading = false;
        UpdatePatternPreview();
        UpdateRepartiInfo();
        TxtSaveInfo.Text = $"Configurazione: {AppPaths.Config}";
    }

    private static int Int(TextBox b, int fallback) => int.TryParse(b.Text.Trim(), out var v) ? v : fallback;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var c = AppHost.Config.Clone();
        var errors = new List<string>();

        c.Domain = TxtDomain.Text.Trim();
        c.OUPath = TxtOu.Text.Trim();
        if (!c.OUPath.Contains("DC=", StringComparison.OrdinalIgnoreCase)) errors.Add("La OU deve essere un distinguished name (OU=...,DC=...)");
        var dc = CmbDc.SelectedItem as string ?? "Automatico";
        c.DomainController = dc == "Automatico" ? "" : dc;
        if (!Naming.IsValidPattern(TxtPattern.Text)) errors.Add("Lo schema del nome deve contenere una volta {INCREMENTAL} (es. PR{INCREMENTAL})");
        else c.NamePattern = TxtPattern.Text.Trim();
        c.IncrementalDigits = Int(TxtDigits, c.IncrementalDigits);
        if (TxtTempPwd.Password.Length > 0) c.DefaultPassword = TxtTempPwd.Password;
        c.ADGroups = LstGroups.Items.Cast<string>().ToList();
        c.RepartiFile = TxtReparti.Text.Trim();
        c.StrictReparti = ChkStrict.IsChecked == true;
        c.AutoLoginProbe = ChkProbe.IsChecked == true;
        c.ForceWriteMode = ChkForceWrite.IsChecked == true;

        c.PrinterNameTemplate = string.IsNullOrWhiteSpace(TxtNameTpl.Text) ? Naming.DriverToken : TxtNameTpl.Text.Trim();
        c.DefaultMode = CmbDefault.SelectedValue as string ?? DefaultModes.Auto;
        c.SetPrtPaths = TxtSetPrtPaths.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        c.SetPrtBatch = TxtSetPrtBat.Text.Trim();
        c.SetPrtProcess = TxtSetPrtProc.Text.Trim();
        c.RemoteWorkDir = TxtRemoteDir.Text.Trim().TrimEnd('\\');
        if (!System.Text.RegularExpressions.Regex.IsMatch(c.RemoteWorkDir, @"^[A-Za-z]:\\")) errors.Add("La cartella di lavoro remota deve essere un percorso locale (es. C:\\ProgramData\\DuckPM)");
        c.RestartSetPrt = ChkSetPrtDefault.IsChecked == true;
        c.DeployPreRemove = CmbPreRemove.SelectedValue as string ?? "none";
        c.RemovePortsDefault = ChkRemovePortsDefault.IsChecked == true;
        c.RemoveRestartSetPrt = ChkRemoveSetPrtDefault.IsChecked == true;
        c.DeployStartDefault = int.TryParse(CmbStartDefault.SelectedValue as string, out var sd) ? sd : 0;
        c.CacheDrivers = ChkCache.IsChecked == true;
        c.DeployUseAdCredentials = ChkAdCreds.IsChecked == true;

        c.TaskMaxParallel = Int(TxtParallel, c.TaskMaxParallel);
        c.TaskCheckMinutes = Int(TxtCheckMin, c.TaskCheckMinutes);
        c.TaskMaxAgeHours = Int(TxtMaxAge, c.TaskMaxAgeHours);
        c.TaskBackoffBase = Int(TxtBackoffBase, c.TaskBackoffBase);
        c.TaskBackoffMax = Int(TxtBackoffMax, c.TaskBackoffMax);
        c.RemoteTimeoutSeconds = Int(TxtTimeout, c.RemoteTimeoutSeconds);
        c.PingTimeoutMs = Int(TxtPing, c.PingTimeoutMs);
        c.PingTcpFallback = ChkTcp.IsChecked == true;
        c.SchedulerAutoStart = ChkAutoStart.IsChecked == true;

        c.DucksAnimate = ChkDucks.IsChecked == true;
        c.WindowTranslucent = ChkTranslucent.IsChecked == true;
        c.PowerShellPath = TxtPs.Text.Trim();
        if (c.PowerShellPath.Length > 0 && !File.Exists(c.PowerShellPath)) errors.Add("Il percorso di PowerShell non esiste");

        if (errors.Count > 0) { DuckDialog.Warning(Owner, "Impostazioni non salvate", string.Join("\n", errors)); return; }

        try
        {
            AppHost.SaveConfig(c);
            AppHost.Ad.SelectedDc = dc;
            Load(AppHost.Config); // mostra i valori riportati nei limiti
            OnShown();
            TxtSaveInfo.Text = $"Salvate alle {DateTime.Now:HH:mm:ss} in {AppPaths.Config}";
            Toast.Success("Impostazioni salvate", "Le modifiche sono gia' attive, compreso lo scheduler.");
        }
        catch (Exception ex) { DuckDialog.Error(Owner, "Salvataggio non riuscito", ex.Message); }
    }

    private void OnRevert(object sender, RoutedEventArgs e) => Load(AppHost.Config);

    private int _section;

    private static readonly (string Art, string Title, string Sub)[] Sections =
    [
        ("server", "Active Directory", "Dominio, OU delle stampanti e domain controller su cui scrivere. Elenco reparti e verifiche automatiche."),
        ("user_new", "Nuove utenze", "Come si chiamano le utenze create (PR{INCREMENTAL}), la password di passaggio e i gruppi a cui vengono aggiunte."),
        ("driver", "Deploy", "Come vengono eseguiti i task sui PC: account, cache dei driver, cartelle set_prt e cartella di lavoro."),
        ("install", "Procedura", "Le opzioni gia' attive quando apri il Deploy o la rimozione: imposti una volta, confermi e basta."),
        ("settings", "Task e sistema", "Quanti PC in parallelo, ogni quanto ricontrollare quelli spenti, i tempi limite. Aspetto e PowerShell."),
    ];

    private void OnSection(object sender, RoutedEventArgs e)
    {
        if (PanelAd is null || PanelProc is null || PanelSys is null) return;
        FrameworkElement[] panels = [PanelAd, PanelUsers, PanelDeploy, PanelProc, PanelSys];
        var idx = SecUsers.IsChecked == true ? 1 : SecDeploy.IsChecked == true ? 2 : SecProc.IsChecked == true ? 3 : SecSys.IsChecked == true ? 4 : 0;
        var (art, title, sub) = Sections[idx];
        SectionArt.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/Assets/art/{art}.png"));
        SectionTitle.Text = title;
        SectionSub.Text = sub;
        if (!IsLoaded || idx == _section)
        {
            for (int i = 0; i < panels.Length; i++) panels[i].Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;
            _section = idx;
            return;
        }
        Fx.Pop(SectionArt);
        Fx.Carousel(panels[_section], panels[idx], idx > _section);
        foreach (var p in panels.Where((_, i) => i != idx && i != _section)) p.Visibility = Visibility.Collapsed;
        _section = idx;
    }

    // ---------------------------------------------------------------- AD
    private async void OnListDcs(object sender, RoutedEventArgs e)
    {
        await Busy.Run(BtnDcs, "...", async () =>
        {
            try
            {
                var dcs = await AppHost.Ad.ListDomainControllersAsync();
                var sel = CmbDc.SelectedItem;
                foreach (var d in dcs.Where(d => !CmbDc.Items.Contains(d))) CmbDc.Items.Add(d);
                CmbDc.SelectedItem = sel;
                CmbDc.IsDropDownOpen = dcs.Count > 0;
                if (dcs.Count == 0) DuckDialog.Info(Owner, "Domain controller", "Nessun DC restituito per il dominio " + AppHost.Config.Domain);
            }
            catch (Exception ex) { DuckDialog.Error(Owner, "Domain controller", ex.Message); }
        });
    }

    private void OnPatternChanged(object sender, TextChangedEventArgs e) => UpdatePatternPreview();

    private void UpdatePatternPreview()
    {
        if (_loading || TxtPatternPreview is null) return;
        TxtPatternPreview.Text = Naming.IsValidPattern(TxtPattern.Text)
            ? $"Esempi: {Naming.NextName(TxtPattern.Text, [], Int(TxtDigits, 4))} (OU vuota) · dopo {Naming.NextName(TxtPattern.Text, [], Int(TxtDigits, 4))} viene il successivo libero. Il numero di cifre segue i nomi gia' presenti."
            : "Lo schema deve contenere {INCREMENTAL} (es. PR{INCREMENTAL} oppure PR-{INCREMENTAL:5}-TS)";
    }

    private void OnAddGroup(object sender, RoutedEventArgs e)
    {
        var g = TxtGroup.Text.Trim();
        if (g.Length == 0 || LstGroups.Items.Cast<string>().Contains(g, StringComparer.OrdinalIgnoreCase)) return;
        LstGroups.Items.Add(g);
        TxtGroup.Clear();
    }

    private void OnRemoveGroupItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string g) LstGroups.Items.Remove(g);
    }

    private async void OnFindGroup(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow mw && !mw.EnsureLogin()) return;
        await Busy.Run(BtnFindGroup, "...", async () =>
        {
            try
            {
                var groups = await AppHost.Ad.SearchGroupsAsync(TxtGroup.Text);
                LstGroupResults.ItemsSource = groups.Select(g => g.Name).ToList();
                LstGroupResults.Visibility = Visibility.Visible;
                if (groups.Count == 0) DuckDialog.Info(Owner, "Gruppi", "Nessun gruppo trovato.");
            }
            catch (Exception ex) { DuckDialog.Error(Owner, "Ricerca gruppi", ex.Message); }
        });
    }

    private void OnPickGroup(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LstGroupResults.SelectedItem is string g && !LstGroups.Items.Cast<string>().Contains(g, StringComparer.OrdinalIgnoreCase))
            LstGroups.Items.Add(g);
    }

    // ---------------------------------------------------------------- reparti
    private void UpdateRepartiInfo()
    {
        var r = AppHost.Reparti;
        TxtRepartiInfo.Text = r.Error is null
            ? $"{r.Items.Count} reparti caricati da {r.Source}"
            : $"Elenco non caricato ({r.Error}). Il campo reparto andra' scritto a mano.";
    }

    private void OnBrowseReparti(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Testo (*.txt)|*.txt|Tutti i file|*.*" };
        if (dlg.ShowDialog(Owner) == true) TxtReparti.Text = dlg.FileName;
    }

    private async void OnReloadReparti(object sender, RoutedEventArgs e)
    {
        await Busy.Run(BtnReloadReparti, "...", async () =>
        {
            await AppHost.Reparti.LoadAsync(TxtReparti.Text.Trim(), TimeSpan.FromSeconds(20));
            UpdateRepartiInfo();
        });
    }

    // ---------------------------------------------------------------- sistema
    // anteprima immediata; resta anche ai prossimi avvii solo dopo Salva
    private void OnTranslucentClick(object sender, RoutedEventArgs e) => ThemeManager.SetTranslucent(ChkTranslucent.IsChecked == true);

    private void OnThemePick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var pref = ThemeLight.IsChecked == true ? "light" : ThemeDark.IsChecked == true ? "dark" : "system";
        if (Window.GetWindow(this) is MainWindow mw) mw.ChangeTheme(() => ThemeManager.Set(pref));
        else ThemeManager.Set(pref);
    }

    private void OnBrowsePs(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PowerShell (pwsh.exe;powershell.exe)|pwsh.exe;powershell.exe" };
        if (dlg.ShowDialog(Owner) == true) TxtPs.Text = dlg.FileName;
    }

    private void OnPruneCache(object sender, RoutedEventArgs e)
    {
        var inUse = AppHost.Core.ListTasks()
            .Where(t => t.Status is not (Core.Models.TaskStatus.Done or Core.Models.TaskStatus.Expired))
            .Select(t => t.GetPayload().DriverDir);
        var n = DriverCache.Prune(AppPaths.DriverCache, inUse);
        DuckDialog.Info(Owner, "Cache driver", $"Eliminate {n} copie di driver non piu' usate da task attivi.");
    }

    private void OnOpenData(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppPaths.Root) { UseShellExecute = true });
}
