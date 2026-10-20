using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DuckPrinterManager.Theme;
using DuckPrinterManager.Ui;

namespace DuckPrinterManager.Dialogs;

/// <summary>
/// Accesso AD in tre stati che scorrono a carosello: modulo → verifica in corso (anello animato,
/// messaggi che si alternano) → fatto (spunta, chiusura automatica). Errore: si torna al modulo.
/// </summary>
public partial class LoginDialog : Window
{
    private static readonly string[] Steps =
    [
        "Contatto il domain controller…",
        "Verifico le credenziali…",
        "Leggo i gruppi dell'account…",
        "Controllo i permessi sulla OU delle stampanti…",
    ];

    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private int _step;

    public LoginDialog(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        TxtUser.Text = AppHost.Config.LastUser;
        CmbDc.Items.Add("Automatico");
        if (!string.IsNullOrWhiteSpace(AppHost.Config.DomainController)) CmbDc.Items.Add(AppHost.Config.DomainController);
        CmbDc.SelectedItem = CmbDc.Items.Contains(AppHost.Ad.SelectedDc ?? "") ? AppHost.Ad.SelectedDc : "Automatico";
        _ticker.Tick += (_, _) => NextBusyText();
        Loaded += (_, _) =>
        {
            Motion.OpenDialog(Root);
            Fx.Pop(Art);
            if (TxtUser.Text.EndsWith('\\') || TxtUser.Text.Length == 0) { TxtUser.Focus(); TxtUser.CaretIndex = TxtUser.Text.Length; }
            else TxtPwd.Focus();
        };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        Closed += (_, _) => _ticker.Stop();
    }

    private void NextBusyText()
    {
        _step = Math.Min(_step + 1, Steps.Length - 1);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            BusyText.Text = Steps[_step];
            BusyText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = Fx.Expo() });
        };
        BusyText.BeginAnimation(OpacityProperty, fade);
    }

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        var user = TxtUser.Text.Trim();
        if (user.Length == 0 || user.EndsWith('\\')) { ShowError("Inserire l'utente (DOMINIO\\utente)"); Fx.Shake(TxtUser); return; }
        if (TxtPwd.Password.Length == 0) { ShowError("Inserire la password"); Fx.Shake(TxtPwd); return; }

        ErrBox.Visibility = Visibility.Collapsed;
        AppHost.Ad.SelectedDc = CmbDc.SelectedItem as string ?? "Automatico";
        _step = 0;
        BusyText.Text = Steps[0];
        BusyUser.Text = user;
        Fx.Carousel(FormPanel, BusyPanel, forward: true);
        _ticker.Start();
        try
        {
            var s = await AppHost.Ad.LoginAsync(user, TxtPwd.Password);
            _ticker.Stop();
            var cfg = AppHost.Config.Clone();
            cfg.LastUser = user;
            AppHost.SaveConfig(cfg);
            TxtPwd.Clear();

            var write = AppHost.Ad.CanEdit || AppHost.Ad.CanCreate;
            DoneTitle.Text = string.IsNullOrWhiteSpace(s.DisplayName) ? "Accesso eseguito" : $"Ciao, {s.DisplayName.Split(' ')[0]}";
            DoneText.Text = write ? "Puoi cercare, modificare e creare le utenze delle stampanti." : "Accesso in sola lettura: puoi cercare le utenze ma non modificarle.";
            Fx.Carousel(BusyPanel, DonePanel, forward: true);
            Fx.Pop(DoneCircle);
            Fx.Pop(DoneTick);
            var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
            close.Tick += (_, _) => { close.Stop(); Motion.CloseDialog(this, Root, true); };
            close.Start();
        }
        catch (Exception ex)
        {
            _ticker.Stop();
            Fx.Carousel(BusyPanel, FormPanel, forward: false);
            ShowError(ex.Message);
            Fx.Shake(Root);
            Dispatcher.BeginInvoke(() => { TxtPwd.Focus(); TxtPwd.SelectAll(); }, DispatcherPriority.Input);
        }
    }

    private void ShowError(string msg)
    {
        ErrText.Text = msg;
        if (ErrBox.Visibility != Visibility.Visible)
        {
            ErrBox.Visibility = Visibility.Visible;
            ErrBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Motion.CloseDialog(this, Root, false);
}
