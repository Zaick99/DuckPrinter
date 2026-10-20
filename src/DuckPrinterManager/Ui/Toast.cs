using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DuckPrinterManager.Theme;

namespace DuckPrinterManager.Ui;

/// <summary>
/// Notifiche non bloccanti in alto a destra (al posto dei popup di conferma per gli esiti
/// positivi): entrano scivolando, restano 6 s (si fermano col mouse sopra), azione facoltativa.
/// </summary>
public static class Toast
{
    private static Panel? _host;

    public static void Attach(Panel host) => _host = host;

    public static void Success(string title, string text, string? action = null, Action? onAction = null) => Show("ChipGreen", "Green", "", title, text, action, onAction);
    public static void Info(string title, string text, string? action = null, Action? onAction = null) => Show("ChipBlue", "Blue", "", title, text, action, onAction);
    public static void Warning(string title, string text, string? action = null, Action? onAction = null) => Show("ChipOrange", "Orange", "", title, text, action, onAction);

    private static void Show(string chip, string fg, string glyph, string title, string text, string? action, Action? onAction)
    {
        if (_host is null) return;
        var app = Application.Current;

        var icon = new Border { Style = (Style)app.FindResource(chip), Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        var ig = new TextBlock { Text = glyph, Style = (Style)app.FindResource("Icon"), HorizontalAlignment = HorizontalAlignment.Center };
        ig.SetResourceReference(TextBlock.ForegroundProperty, fg);
        icon.Child = ig;

        var body = new StackPanel { Margin = new Thickness(12, 0, 8, 0) };
        body.Children.Add(new TextBlock { Text = title, Style = (Style)app.FindResource("TxtBody"), FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = text, Style = (Style)app.FindResource("TxtCaption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), MaxHeight = 90, TextTrimming = TextTrimming.CharacterEllipsis });

        var close = new Button { Content = new TextBlock { Text = "", Style = (Style)app.FindResource("Icon"), FontSize = 9 }, Style = (Style)app.FindResource("Toolbtn"), Width = 22, Height = 22, MinWidth = 0, VerticalAlignment = VerticalAlignment.Top };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(body, 1);
        Grid.SetColumn(close, 2);
        grid.Children.Add(icon);
        grid.Children.Add(body);
        grid.Children.Add(close);
        if (action is not null)
        {
            var btn = new Button { Content = action, Style = (Style)app.FindResource("SoftBtn"), Height = 26, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            btn.Click += (_, _) => onAction?.Invoke();
            body.Children.Add(btn);
        }

        var card = new Border
        {
            Width = 340,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(14, 12, 10, 12),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Child = grid,
            RenderTransform = new TranslateTransform(40, 0),
            Opacity = 0,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.28, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "BgElevated");
        card.SetResourceReference(Border.BorderBrushProperty, "Separator");
        _host.Children.Insert(0, card);
        while (_host.Children.Count > 4) _host.Children.RemoveAt(_host.Children.Count - 1);

        card.BeginAnimation(UIElement.OpacityProperty, Motion.Slide(0, 1, 240, Fx.Expo()));
        ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.XProperty, Motion.Slide(60, 0, 700, Fx.Spring(6)));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(action is null ? 5 : 9) };
        void Dismiss()
        {
            timer.Stop();
            var fade = Motion.Slide(card.Opacity, 0, 180, Motion.Ease(mode: System.Windows.Media.Animation.EasingMode.EaseIn));
            fade.Completed += (_, _) => _host?.Children.Remove(card);
            card.BeginAnimation(UIElement.OpacityProperty, fade);
            ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.XProperty, Motion.Slide(0, 30, 180, null));
        }
        timer.Tick += (_, _) => Dismiss();
        card.MouseEnter += (_, _) => timer.Stop();
        card.MouseLeave += (_, _) => timer.Start();
        close.Click += (_, _) => Dismiss();
        if (action is not null) ((Button)body.Children[^1]).Click += (_, _) => Dismiss();
        timer.Start();
    }
}
