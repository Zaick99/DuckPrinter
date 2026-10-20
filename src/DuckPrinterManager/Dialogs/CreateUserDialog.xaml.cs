using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DuckPrinterManager.Core.Native;
using DuckPrinterManager.Theme;
using DuckPrinterManager.Ui;

namespace DuckPrinterManager.Dialogs;

/// <summary>Procedura guidata di creazione utenza: MAC → reparto → conferma → fatto.</summary>
public partial class CreateUserDialog : Window
{
    private int _step;

    /// <summary>MAC dell'utenza creata (o da mostrare) quando la finestra si chiude con "Mostra l'utenza".</summary>
    public string? ShowMac { get; private set; }

    public CreateUserDialog(Window owner, string? mac = null)
    {
        InitializeComponent();
        Owner = owner;
        SuggestionPopup.AttachMacFilter(TxtMac);
        SuggestionPopup.Attach(TxtDesc, t => AppHost.Reparti.Suggest(t));
        TxtMac.TextChanged += (_, _) => UpdateMac();
        if (mac is not null) TxtMac.Text = mac;
        Loaded += (_, _) =>
        {
            Motion.OpenDialog(Root);
            Go(mac is null ? 0 : 1, true, animate: false);
        };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) try { DragMove(); } catch { } };
    }

    private void Go(int step, bool forward, bool animate = true)
    {
        FrameworkElement[] panels = [Step1, Step2, Step3, Done];
        var target = panels[step];
        var old = panels.FirstOrDefault(p => p.Visibility == Visibility.Visible && p != target);
        foreach (var p in panels) if (p != target && p != old) p.Visibility = Visibility.Collapsed;
        if (animate && old is not null) Fx.Carousel(old, target, forward);
        else { if (old is not null) old.Visibility = Visibility.Collapsed; target.Visibility = Visibility.Visible; }
        _step = step;

        for (int i = 0; i < Dots.Children.Count; i++)
        {
            var dot = (Border)Dots.Children[i];
            var done = i < step || step == 3;
            dot.SetResourceReference(Border.BackgroundProperty, done ? "Accent" : i == step ? "AccentBorder" : "Separator");
            dot.BeginAnimation(WidthProperty, new DoubleAnimation(dot.ActualWidth > 0 ? dot.ActualWidth : 8, i == step && step < 3 ? 22 : 8, TimeSpan.FromMilliseconds(420)) { EasingFunction = Fx.Spring(7) });
        }
        BtnBack.Visibility = step is 1 or 2 ? Visibility.Visible : Visibility.Hidden;
        BtnShow.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        TxtNext.Text = step switch { 2 => "Crea utenza", 3 => "Crea un'altra", _ => "Avanti" };
        GlyphNext.Text = step switch { 2 => "", 3 => "", _ => "" };

        if (step == 1)
            TxtRepartoHelp.Text = AppHost.Reparti.Items.Count > 0
                ? $"Finisce nella descrizione dell'utenza. Scegli fra i {AppHost.Reparti.Items.Count} reparti dell'elenco."
                : "Finisce nella descrizione dell'utenza (elenco reparti non disponibile: scrivilo a mano).";
        if (step == 2)
        {
            SumMac.Text = DuckCore.NormalizeMac(TxtMac.Text) ?? "";
            SumDesc.Text = AppHost.Reparti.Canonical(TxtDesc.Text);
            SumGroups.Text = AppHost.Config.ADGroups.Count == 0 ? "nessuno" : string.Join(", ", AppHost.Config.ADGroups);
            TxtWhere.Text = $"In {AppHost.Config.OUPath}";
            TxtProgress.Text = "";
            _ = CalcName();
        }
        Dispatcher.BeginInvoke(() => { if (step == 0) TxtMac.Focus(); else if (step == 1) TxtDesc.Focus(); else BtnNext.Focus(); }, DispatcherPriority.Input);
    }

    private void UpdateMac()
    {
        var ok = DuckCore.NormalizeMac(TxtMac.Text) is not null;
        TxtMacCount.Text = TxtMac.Text.Length == 0 ? "" : ok ? "MAC valido" : $"{TxtMac.Text.Length}/12 caratteri";
        var was = MacOk.Opacity > 0.5;
        MacOk.BeginAnimation(OpacityProperty, new DoubleAnimation(ok ? 1 : 0, TimeSpan.FromMilliseconds(200)));
        if (ok && !was) Fx.Pop(MacOk);
    }

    private async Task CalcName()
    {
        TxtName.Text = "…";
        try { TxtName.Text = await AppHost.Ad.NextNameAsync(); Fx.Pop(TxtName); }
        catch (Exception ex) { TxtName.Text = "----"; TxtProgress.Text = "Calcolo del nome non riuscito: " + ex.Message; }
    }

    private async void OnCalc(object sender, RoutedEventArgs e) => await Busy.Run(BtnCalc, "…", CalcName);

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnNext(sender, e); e.Handled = true; }
    }

    private void OnBack(object sender, RoutedEventArgs e) { if (_step is 1 or 2) Go(_step - 1, false); }

    private bool ValidateReparto(out string value)
    {
        value = TxtDesc.Text.Trim();
        var r = AppHost.Reparti;
        if (value.Length == 0) { Toast.Warning("Reparto", "Indica il reparto."); return false; }
        if (r.Items.Count > 0)
        {
            if (r.Contains(value)) { value = r.Canonical(value); return true; }
            if (AppHost.Config.StrictReparti) { Toast.Warning("Reparto non in elenco", $"'{value}' non e' nell'elenco reparti: scegli una voce suggerita."); return false; }
        }
        return true;
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 0:
                if (DuckCore.NormalizeMac(TxtMac.Text) is null) { Fx.Shake(TxtMac); Toast.Warning("MAC non valido", "Servono 12 caratteri esadecimali (es. 001122aabbcc)."); return; }
                Go(1, true);
                return;
            case 1:
                if (!ValidateReparto(out var d)) { Fx.Shake(TxtDesc); return; }
                TxtDesc.Text = d;
                Go(2, true);
                return;
            case 2:
                await Create();
                return;
            default:
                TxtMac.Clear();
                TxtDesc.Clear();
                Go(0, false);
                return;
        }
    }

    private async Task Create()
    {
        var mac = DuckCore.NormalizeMac(TxtMac.Text)!;
        var desc = AppHost.Reparti.Canonical(TxtDesc.Text);
        BtnNext.IsEnabled = BtnBack.IsEnabled = false;
        TxtNext.Text = "Creazione…";
        try
        {
            var r = await AppHost.Ad.CreateAsync(mac, desc, s => TxtProgress.Text = s);
            if (!r.Ok)
            {
                var why = r.Reason == "MacExists" ? $"Il MAC {mac} e' gia' registrato sull'utenza {r.Name}." : $"Il nome {r.Name} risulta gia' esistente anche dopo il ricalcolo.";
                TxtProgress.Text = why;
                Fx.Shake(Root);
                if (r.Reason == "MacExists" && DuckDialog.Confirm(this, "MAC gia' registrato", why + "\n\nAprire l'utenza esistente?", "Apri")) { ShowMac = mac; Motion.CloseDialog(this, Root, true); }
                return;
            }
            AppHost.Core.Log("INFO", $"AD: creato {r.Name} / {r.Mac} ({desc})");
            ShowMac = r.Mac;
            TxtDoneTitle.Text = $"{r.Name} e' pronta";
            TxtDoneSub.Text = $"SAM e password {r.Mac} · {desc}"
                              + (r.GroupsOk.Count > 0 ? $"\nGruppi: {string.Join(", ", r.GroupsOk)}" : "")
                              + (r.Probe is null ? "" : $"\nProva di login: {r.Probe.Display}");
            Go(3, true);
            Fx.Celebrate(ImgDone);
            if (r.GroupsFailed.Count > 0) DuckDialog.Warning(this, "Gruppi non assegnati", string.Join("\n", r.GroupsFailed));
        }
        catch (Exception ex)
        {
            AppHost.Core.Log("ERR", "AD: creazione non riuscita - " + ex.Message);
            TxtProgress.Text = ex.Message;
            Fx.Shake(Root);
        }
        finally
        {
            BtnNext.IsEnabled = BtnBack.IsEnabled = true;
            if (_step == 2) TxtNext.Text = "Crea utenza";
        }
    }

    private void OnShow(object sender, RoutedEventArgs e) => Motion.CloseDialog(this, Root, true);
    private void OnClose(object sender, RoutedEventArgs e) => Motion.CloseDialog(this, Root, false);
}
