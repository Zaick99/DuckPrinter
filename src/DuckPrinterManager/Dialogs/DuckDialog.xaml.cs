using System.Windows;
using System.Windows.Input;
using DuckPrinterManager.Theme;

namespace DuckPrinterManager.Dialogs;

/// <summary>Sostituto Duck di MessageBox: segue il tema e ha un solo bottone primario.</summary>
public partial class DuckDialog : Window
{
    private enum Kind { Info, Success, Warning, Error, Question }

    private DuckDialog(Window? owner, string title, string body, Kind kind, string ok, string? cancel, bool danger)
    {
        InitializeComponent();
        if (owner is { IsVisible: true }) Owner = owner; else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TitleText.Text = title;
        BodyText.Text = body;
        BtnOk.Content = ok;
        if (danger) BtnOk.Style = (Style)FindResource("DangerBtn");
        if (cancel is null) BtnCancel.Visibility = Visibility.Collapsed; else BtnCancel.Content = cancel;
        (IconGlyph.Text, IconBox.Style, IconGlyph.Foreground) = kind switch
        {
            Kind.Success => ("", (Style)FindResource("ChipGreen"), (System.Windows.Media.Brush)FindResource("Green")),
            Kind.Warning => ("", (Style)FindResource("ChipOrange"), (System.Windows.Media.Brush)FindResource("Orange")),
            Kind.Error => ("", (Style)FindResource("ChipRed"), (System.Windows.Media.Brush)FindResource("Red")),
            Kind.Question => ("", (Style)FindResource("ChipBlue"), (System.Windows.Media.Brush)FindResource("Blue")),
            _ => ("", (Style)FindResource("ChipBlue"), (System.Windows.Media.Brush)FindResource("Blue")),
        };
        Loaded += (_, _) => Motion.OpenDialog(Root);
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
    }

    private void OnOk(object sender, RoutedEventArgs e) => Motion.CloseDialog(this, Root, true);
    private void OnCancel(object sender, RoutedEventArgs e) => Motion.CloseDialog(this, Root, false);

    private static bool Show(Window? owner, string title, string body, Kind kind, string ok = "OK", string? cancel = null, bool danger = false) =>
        new DuckDialog(owner, title, body, kind, ok, cancel, danger).ShowDialog() == true;

    public static void Info(Window? o, string t, string b) => Show(o, t, b, Kind.Info);
    public static void Success(Window? o, string t, string b) => Show(o, t, b, Kind.Success);
    public static void Warning(Window? o, string t, string b) => Show(o, t, b, Kind.Warning);
    public static void Error(Window? o, string t, string b) => Show(o, t, b, Kind.Error);
    public static bool Confirm(Window? o, string t, string b, string ok = "Conferma", bool danger = false) =>
        Show(o, t, b, danger ? Kind.Warning : Kind.Question, ok, "Annulla", danger);
}
