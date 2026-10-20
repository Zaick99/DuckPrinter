using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace DuckPrinterManager.Ui;

/// <summary>
/// Controllo segmentato con pillola che scorre (stile iOS/macOS). Si aggancia al Border che
/// contiene i RadioButton: <c>ui:Seg.Slide="True"</c>. La pillola e' un elemento solo, dietro le
/// voci; cambiando scelta scivola (molla) e si allarga/stringe sulla larghezza della nuova voce.
/// </summary>
public static class Seg
{
    public static readonly DependencyProperty SlideProperty = DependencyProperty.RegisterAttached(
        "Slide", typeof(bool), typeof(Seg), new PropertyMetadata(false, (d, e) =>
        {
            if (d is Border b && (bool)e.NewValue) b.Loaded += Setup;
        }));

    public static void SetSlide(DependencyObject d, bool v) => d.SetValue(SlideProperty, v);
    public static bool GetSlide(DependencyObject d) => (bool)d.GetValue(SlideProperty);

    private sealed class State(Grid host, Border pill, List<RadioButton> radios)
    {
        public Grid Host { get; } = host;
        public Border Pill { get; } = pill;
        public List<RadioButton> Radios { get; } = radios;
    }

    private static void Setup(object sender, RoutedEventArgs e)
    {
        var border = (Border)sender;
        if (border.Child is Grid { Tag: State }) return;
        var content = border.Child;
        if (content is null) return;
        border.Child = null;

        var pill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(7),
            IsHitTestVisible = false,
            Opacity = 0,
            RenderTransform = new TranslateTransform(),
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Direction = 270, Opacity = 0.18, Color = Colors.Black },
        };
        pill.SetResourceReference(Border.BackgroundProperty, "AccentFill");
        var host = new Grid();
        host.Children.Add(pill);
        host.Children.Add(content);
        border.Child = host;

        var radios = new List<RadioButton>();
        Find(content, radios);
        var st = new State(host, pill, radios);
        host.Tag = st;
        foreach (var r in radios)
            r.Checked += (_, _) => Move(st, r, immediate: false);
        host.SizeChanged += (_, _) => Move(st, radios.FirstOrDefault(x => x.IsChecked == true), immediate: true);
        host.Dispatcher.BeginInvoke(() => Move(st, radios.FirstOrDefault(x => x.IsChecked == true), immediate: true),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void Find(DependencyObject d, List<RadioButton> list)
    {
        if (d is RadioButton r) { list.Add(r); return; }
        if (d is Panel p) foreach (UIElement c in p.Children) Find(c, list);
        else if (d is Decorator { Child: { } ch }) Find(ch, list);
    }

    private static void Move(State st, RadioButton? r, bool immediate)
    {
        var pill = st.Pill;
        var tt = (TranslateTransform)pill.RenderTransform;
        if (r is null || r.ActualWidth <= 0 || !r.IsVisible)
        {
            if (r is null) pill.Opacity = 0;
            return;
        }
        var x = r.TranslatePoint(new Point(0, 0), st.Host).X;
        if (immediate || pill.Opacity < 0.5)
        {
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            pill.BeginAnimation(FrameworkElement.WidthProperty, null);
            tt.X = x;
            pill.Width = r.ActualWidth;
            if (pill.Opacity < 0.5) pill.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            return;
        }
        var dur = TimeSpan.FromMilliseconds(520);
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(tt.X, x, dur) { EasingFunction = Fx.Spring(8) });
        pill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(pill.ActualWidth, r.ActualWidth, TimeSpan.FromMilliseconds(380)) { EasingFunction = Fx.Expo() });
    }
}
