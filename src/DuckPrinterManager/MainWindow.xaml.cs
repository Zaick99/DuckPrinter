using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DuckPrinterManager.Core.Native;
using DuckPrinterManager.Core.Services;
using DuckPrinterManager.Dialogs;
using DuckPrinterManager.Pages;
using DuckPrinterManager.Theme;
using DuckPrinterManager.Ui;
using Shape = System.Windows.Shapes.Shape;

namespace DuckPrinterManager;

public partial class MainWindow : Window
{
    private const string Moon = "M 10,2 A 8,8 0 1 0 18,10 A 6,6 0 1 1 10,2 Z";
    private const string Sun = "M6,10 A4,4 0 1 1 14,10 A4,4 0 1 1 6,10 Z M10,0.8 V3 M10,17 V19.2 M0.8,10 H3 M17,10 H19.2 " +
                               "M3.5,3.5 L5,5 M15,15 L16.5,16.5 M16.5,3.5 L15,5 M5,15 L3.5,16.5";

    private readonly Dictionary<Button, (string Title, Func<FrameworkElement> Factory)> _pages;
    private readonly Dictionary<Button, FrameworkElement> _cache = [];
    private readonly DuckFlock _flock;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ulong _lastLogSeq;

    public MainWindow()
    {
        InitializeComponent();
        _pages = new()
        {
            [NavDeploy] = ("Deploy stampanti", () => new DeployPage()),
            [NavTasks] = ("Task programmati", () => new TasksPage()),
            [NavMac] = ("MAC Auth", () => new MacAuthPage()),
            [NavLog] = ("Log", () => new LogPage()),
            [NavSettings] = ("Impostazioni", () => new SettingsPage()),
        };

        Toast.Attach(ToastHost);
        _flock = new DuckFlock(DuckLayer);
        ThemeManager.TranslucentChanged += _ => ApplyGlass();
        SourceInitialized += (_, _) => ApplyGlass();
        Loaded += (_, _) =>
        {
            _flock.Animate = AppHost.Config.DucksAnimate;
            Prewarm();
        };
        AppHost.ConfigChanged += () => _flock.Animate = AppHost.Config.DucksAnimate;

        ThemeManager.Changed += OnThemeChanged;
        OnThemeChanged(ThemeManager.Effective);

        AppHost.Ad.SessionChanged += UpdateAdState;
        UpdateAdState();

        QueueHistory.Start();
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();

        NavArea.SizeChanged += (_, _) => { if (_current is not null) Fx.MoveIndicator(NavPill, _current, NavArea, true); };
        StatusRight.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "");
        Navigate(NavDeploy);
    }

    /// <summary>
    /// Costruisce le pagine non ancora aperte a priorita' bassa, una per volta, dopo l'avvio:
    /// la prima apertura di ogni pagina non deve pagare la creazione del XAML durante l'animazione.
    /// </summary>
    private void Prewarm()
    {
        var pending = _pages.Keys.Where(b => !_cache.ContainsKey(b)).ToList();
        void Next()
        {
            if (pending.Count == 0) return;
            var b = pending[0];
            pending.RemoveAt(0);
            if (!_cache.ContainsKey(b))
            {
                try
                {
                    var page = _pages[b].Factory();
                    _cache[b] = page;
                    // misura fuori schermo: stili e template vengono applicati ora, non al primo clic
                    page.Measure(new Size(PageHost.ActualWidth, PageHost.ActualHeight));
                    page.Arrange(new Rect(0, 0, PageHost.ActualWidth, PageHost.ActualHeight));
                }
                catch (Exception ex)
                {
                    // la preparazione anticipata non deve mai bloccare l'avvio: la pagina verra'
                    // ricostruita al primo clic, e l'errore resta nel log con lo stack completo
                    _cache.Remove(b);
                    AppHost.TryLog("ERR", $"Preparazione pagina '{_pages[b].Title}': {ex}");
                }
            }
            Dispatcher.BeginInvoke(Next, DispatcherPriority.ApplicationIdle);
        }
        Dispatcher.BeginInvoke(Next, DispatcherPriority.ApplicationIdle);
    }

    public void Navigate(string page)
    {
        var b = page switch { "tasks" => NavTasks, "mac" => NavMac, "log" => NavLog, "settings" => NavSettings, _ => NavDeploy };
        Navigate(b);
    }

    private Button? _current;
    private Button? _previous;

    private void Navigate(Button nav)
    {
        var first = _current is null;
        _current = nav;
        foreach (var b in _pages.Keys) b.Tag = b == nav ? "Active" : null;
        if (!_cache.TryGetValue(nav, out var page)) _cache[nav] = page = _pages[nav].Factory();
        Title = "Duck Printer Manager - " + _pages[nav].Title;
        PageHost.Content = page;
        var order = _pages.Keys.ToList();
        Fx.PageIn(page, _previous is null || order.IndexOf(nav) >= order.IndexOf(_previous) ? 1 : -1);
        _previous = nav;
        Dispatcher.BeginInvoke(() => Fx.MoveIndicator(NavPill, nav, NavArea, first), DispatcherPriority.Loaded);
        if (page is IPage p) p.OnShown();
    }

    private void OnNav(object sender, RoutedEventArgs e) => Navigate((Button)sender);

    // ---------------------------------------------------------------- stato
    private void RefreshStatus()
    {
        try
        {
            var r = AppHost.Core.Revision();
            var s = r.Stats;
            var running = AppHost.Scheduler.RunningCount;
            SchedTitle.Text = r.Paused ? "Scheduler in pausa" : "Scheduler attivo";
            SchedDot.SetResourceReference(Shape.FillProperty, r.Paused ? "Orange" : running > 0 ? "Blue" : "Green");
            var pending = s.Waiting + s.Retry + s.Paused;
            SchedText.Text = s.Total == 0 ? "Nessun task in coda" : $"{running} in corso · {pending} in coda" + (s.Retry > 0 ? $" · {s.Retry} in errore" : "");
            SchedProgress.Value = s.Total == 0 ? 0 : (double)s.Done / s.Total;
            SchedPercent.Text = s.Total == 0 ? "" : $"{s.Done}/{s.Total}";
            Fx.Pulse(SchedDot, running > 0 && !r.Paused);
            var active = s.Running + s.Waiting + s.Retry;
            TasksBadge.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
            TasksBadgeText.Text = active > 999 ? "999+" : active.ToString();
            TasksBadge.SetResourceReference(Border.BackgroundProperty, s.Retry > 0 ? "RedSoft" : "BlueSoft");
            TasksBadgeText.SetResourceReference(TextBlock.ForegroundProperty, s.Retry > 0 ? "Red" : "Blue");

            var last = AppHost.Core.GetLog(null, _lastLogSeq, 1);
            if (last.Count > 0)
            {
                _lastLogSeq = last[^1].Seq;
                StatusText.Text = $"{last[^1].Time:HH:mm:ss}  {last[^1].Msg}";
            }
        }
        catch { }
    }

    private void UpdateAdState()
    {
        var s = AppHost.Ad.Session;
        if (s is null)
        {
            AccountDuck.Visibility = Visibility.Collapsed;
            AccountAvatar.SetResourceReference(Border.BackgroundProperty, "BgPressed");
            AccountInitials.Text = "\uE77B";
            AccountInitials.Style = (Style)FindResource("Icon");
            AccountInitials.SetResourceReference(TextBlock.ForegroundProperty, "LabelSecondary");
            AccountName.Text = "Accedi";
            AccountSub.Text = "Active Directory";
            AccountDot.SetResourceReference(Shape.FillProperty, "LabelQuaternary");
            BtnAccount.ToolTip = "Accedi con un account di dominio";
            return;
        }
        var write = AppHost.Ad.CanEdit || AppHost.Ad.CanCreate;
        var user = s.User.Contains('\\') ? s.User[(s.User.IndexOf('\\') + 1)..] : s.User.Split('@')[0];
        var name = string.IsNullOrWhiteSpace(s.DisplayName) ? user : s.DisplayName;
        AccountAvatar.SetResourceReference(Border.BackgroundProperty, "AccentFill");
        AccountInitials.Style = null;
        AccountInitials.FontWeight = FontWeights.Bold;
        AccountInitials.FontSize = 12;
        AccountInitials.VerticalAlignment = VerticalAlignment.Center;
        AccountInitials.HorizontalAlignment = HorizontalAlignment.Center;
        AccountInitials.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        AccountInitials.Text = string.Concat(name.Split(' ', '.', '_').Where(p => p.Length > 0).Take(2).Select(p => char.ToUpperInvariant(p[0])));
        AccountDuck.Source = DuckAvatar.For(name);
        AccountDuck.Visibility = Visibility.Visible;
        Fx.Pop(AccountDuck);
        AccountName.Text = name;
        AccountSub.Text = write ? "Modifica abilitata" : "Sola lettura";
        AccountDot.SetResourceReference(Shape.FillProperty, write ? "Green" : "Blue");
        BtnAccount.ToolTip = $"{s.User}\nDC: {s.DcUsed}\n{string.Join("\n", s.Reasons)}\n\nClic per disconnettere";
    }

    private void OnLogin(object sender, RoutedEventArgs e)
    {
        if (AppHost.Ad.IsLoggedIn)
        {
            if (DuckDialog.Confirm(this, "Disconnessione", "Chiudere la sessione Active Directory? La password viene cancellata dalla memoria.", "Esci")) AppHost.Ad.Logout();
            return;
        }
        new LoginDialog(this).ShowDialog();
    }

    public bool EnsureLogin()
    {
        if (AppHost.Ad.IsLoggedIn) return true;
        return new LoginDialog(this).ShowDialog() == true;
    }

    // ---------------------------------------------------------------- tema
    public void ChangeTheme(Action change) => change();

    private void OnTheme(object sender, RoutedEventArgs e) => ThemeManager.Toggle();
    private void OnThemeSystem(object sender, MouseButtonEventArgs e) { ThemeManager.Set("system"); e.Handled = true; }

    private void OnThemeChanged(string theme)
    {
        if (theme == "dark")
        {
            ThemeGlyph.Data = Geometry.Parse(Sun);
            ThemeGlyph.Fill = null;
            ThemeGlyph.SetResourceReference(Shape.StrokeProperty, "LabelSecondary");
            ThemeGlyph.StrokeThickness = 1.3;
        }
        else
        {
            ThemeGlyph.Data = Geometry.Parse(Moon);
            ThemeGlyph.Stroke = null;
            ThemeGlyph.SetResourceReference(Shape.FillProperty, "LabelSecondary");
        }
    }

    // ---------------------------------------------------------------- finestra
    private void OnClose(object sender, RoutedEventArgs e)
    {
        var running = AppHost.Scheduler.RunningCount;
        if (running > 0 && !DuckDialog.Confirm(this, "Task in esecuzione",
                $"{running} task sono in esecuzione. Chiudendo vengono interrotti e ripartiranno al prossimo avvio.\nI task in coda restano salvati.", "Chiudi comunque", danger: true))
            return;
        Close();
    }

    private void OnMin(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMax(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Vetro: sfocatura di Windows dietro la finestra + angoli compatibili con quelli di DWM.</summary>
    private void ApplyGlass()
    {
        var on = ThemeManager.Translucent && Backdrop.Apply(this, ThemeManager.Translucent);
        if (!ThemeManager.Translucent) Backdrop.Apply(this, false);
        _glass = on;
        OnStateChanged(EventArgs.Empty);
    }

    private bool _glass;

    /// <summary>Raggio degli angoli: con il vetro segue quelli di Windows (8 su Win11, dritti su Win10).</summary>
    private CornerRadius Corners => WindowState == WindowState.Maximized ? new CornerRadius(0)
        : _glass ? new CornerRadius(Backdrop.IsWindows11 ? 8 : 0) : new CornerRadius(12);

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // massimizzata senza bordo: niente margine dello shadow, angoli dritti
        RootBorder.CornerRadius = Corners;
        RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(6) : new Thickness(0);
    }
}

public interface IPage
{
    void OnShown();
}
