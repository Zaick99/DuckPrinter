using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DuckPrinterManager.Theme;

namespace DuckPrinterManager.Ui;

/// <summary>
/// Animazioni dell'interfaccia. Solo trasformazioni di rendering (mai layout) e curve
/// esponenziali/a molla: fluide anche a 120 Hz, interrompibili in qualunque momento.
/// </summary>
public static class Fx
{
    private static readonly HashSet<string> CardStyles = ["Card", "GroupCard", "StatTile"];
    private static readonly HashSet<string> PressStyles = ["PrimaryBtn", "SoftBtn", "QuietBtn", "DangerBtn", "GhostBtn", "Toolbtn", "SideItem"];

    // ---------------------------------------------------------------- curve
    public static IEasingFunction Expo(EasingMode m = EasingMode.EaseOut) => new ExponentialEase { Exponent = 4, EasingMode = m };
    /// <summary>Uscita cubica: la curva per gli spostamenti di contenuto (pagine, card), morbida da A a B.</summary>
    public static IEasingFunction Smooth(EasingMode m = EasingMode.EaseOut) => new CubicEase { EasingMode = m };
    public static IEasingFunction Spring(double springiness = 6) => new ElasticEase { Oscillations = 1, Springiness = springiness, EasingMode = EasingMode.EaseOut };
    public static IEasingFunction Back(double amp = 0.35) => new BackEase { Amplitude = amp, EasingMode = EasingMode.EaseOut };

    private static DoubleAnimation Anim(double from, double to, int ms, IEasingFunction? ease, int delay = 0) =>
        new(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(delay) };

    /// <summary>Fino a 120 fotogrammi al secondo dove lo schermo lo consente (default WPF: 60).</summary>
    public static void EnableHighFrameRate()
    {
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = 120 });
        // Testo in scala di grigi ovunque: e' come lo disegna la BitmapCache delle animazioni, quindi
        // a fine animazione (cache tolta) il testo non cambia resa e non "scatta" da grigio a ClearType.
        TextOptions.TextRenderingModeProperty.OverrideMetadata(typeof(Window),
            new FrameworkPropertyMetadata(TextRenderingMode.Grayscale, FrameworkPropertyMetadataOptions.Inherits));
    }

    /// <summary>
    /// Durante l'animazione l'elemento viene composto come bitmap sulla GPU (niente ri-rasterizzazione
    /// del testo a ogni fotogramma); la cache si toglie a fine animazione per tornare nitidi.
    /// </summary>
    private static void Cached(UIElement e, AnimationTimeline lastAnim)
    {
        var dpi = VisualTreeHelper.GetDpi(e);
        e.CacheMode = new BitmapCache { RenderAtScale = Math.Max(1, dpi.DpiScaleX), SnapsToDevicePixels = true, EnableClearType = false };
        lastAnim.Completed += (_, _) => e.CacheMode = null;
    }

    /// <summary>Trasformazione composta scala + traslazione, riusata da tutte le animazioni.</summary>
    private static (ScaleTransform S, TranslateTransform T) Xf(UIElement e)
    {
        if (e.RenderTransform is TransformGroup g && g.Children.Count == 2 && g.Children[0] is ScaleTransform s0 && g.Children[1] is TranslateTransform t0)
            return (s0, t0);
        var s = new ScaleTransform(1, 1);
        var t = new TranslateTransform();
        e.RenderTransform = new TransformGroup { Children = { s, t } };
        e.RenderTransformOrigin = new Point(0.5, 0.5);
        return (s, t);
    }

    private static void ScaleTo(ScaleTransform s, double from, double to, int ms, IEasingFunction ease, int delay = 0)
    {
        s.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(from, to, ms, ease, delay));
        s.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(from, to, ms, ease, delay));
    }

    // ---------------------------------------------------------------- bottoni
    public static void RegisterPress()
    {
        EventManager.RegisterClassHandler(typeof(Button), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((s, _) => Press((Button)s, true)));
        EventManager.RegisterClassHandler(typeof(Button), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((s, _) => Press((Button)s, false)));
        EventManager.RegisterClassHandler(typeof(Button), UIElement.MouseLeaveEvent, new MouseEventHandler((s, _) => Press((Button)s, false)));
    }

    private static bool IsKitStyle(FrameworkElement e, HashSet<string> keys)
    {
        if (e.Style is null) return false;
        foreach (var k in keys)
            if (Application.Current.TryFindResource(k) is Style st && (e.Style == st || e.Style.BasedOn == st)) return true;
        return false;
    }

    private static void Press(Button b, bool down)
    {
        if (b.ActualWidth < 18 || !IsKitStyle(b, PressStyles)) return;
        var (s, _) = Xf(b);
        if (!down && Math.Abs(s.ScaleX - 1) < 0.001) return;
        // giu': rapido e deciso; su: rilascio a molla
        ScaleTo(s, s.ScaleX, down ? 0.95 : 1, down ? 110 : 520, down ? Expo() : Spring(4));
    }

    // ---------------------------------------------------------------- pagine
    /// <summary>
    /// Transizione di pagina: dissolvenza + scorrimento verticale nella direzione della voce scelta.
    /// Parte solo dopo il primo layout (altrimenti il primo fotogramma se lo mangia la misura).
    /// </summary>
    public static void PageIn(FrameworkElement page, int direction)
    {
        var (s, t) = Xf(page);
        s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        s.ScaleX = s.ScaleY = 1;
        t.BeginAnimation(TranslateTransform.YProperty, null);
        t.Y = 0;
        page.BeginAnimation(UIElement.OpacityProperty, null);
        page.Opacity = 0;
        page.Dispatcher.BeginInvoke(() =>
        {
            // La pagina si limita a comparire; a muoversi sono solo le card (Entrance), nella
            // direzione della voce scelta. Pagina + card che salgono insieme sommavano due
            // movimenti con tempi diversi: sembrava un rimbalzo.
            page.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, 260, Smooth()));
            Entrance(page, 0, direction);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Le card emergono in cascata: dissolvenza e salita (curva esponenziale, cache GPU).</summary>
    public static void Entrance(DependencyObject root, int baseDelay = 0, int direction = 1)
    {
        var cards = new List<FrameworkElement>();
        Collect(root, cards);
        int i = 0;
        foreach (var c in cards.Take(10))
        {
            var delay = baseDelay + 28 * i++;
            var (s, t) = Xf(c);
            s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            s.ScaleX = s.ScaleY = 1;
            c.BeginAnimation(UIElement.OpacityProperty, null);
            c.Opacity = 0;
            // curva cubica: velocita' che cala in modo uniforme da A a B (l'esponenziale faceva
            // quasi tutto il tragitto nei primi fotogrammi e sembrava uno scatto)
            var rise = Anim(12 * direction, 0, 480, Smooth(), delay);
            Cached(c, rise);
            c.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, 340, Smooth(), delay));
            t.BeginAnimation(TranslateTransform.YProperty, rise);
        }
    }

    private static void Collect(DependencyObject d, List<FrameworkElement> list)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is Border b && b.IsVisible && IsKitStyle(b, CardStyles)) { list.Add(b); continue; }
            Collect(c, list);
        }
    }

    /// <summary>Carosello fra due pannelli: il vecchio esce rimpicciolendo, il nuovo entra dal lato opposto.</summary>
    public static void Carousel(FrameworkElement oldPanel, FrameworkElement newPanel, bool forward)
    {
        var dir = forward ? 1 : -1;
        var (os, ot) = Xf(oldPanel);
        var fade = Anim(oldPanel.Opacity, 0, 220, Expo(EasingMode.EaseIn));
        Cached(oldPanel, fade);
        fade.Completed += (_, _) => { if (oldPanel.Opacity < 0.05) oldPanel.Visibility = Visibility.Collapsed; };
        oldPanel.BeginAnimation(UIElement.OpacityProperty, fade);
        ot.BeginAnimation(TranslateTransform.XProperty, Anim(0, -70 * dir, 260, Expo(EasingMode.EaseIn)));
        ScaleTo(os, 1, 0.96, 260, Expo(EasingMode.EaseIn));

        newPanel.Visibility = Visibility.Visible;
        var (ns, nt) = Xf(newPanel);
        newPanel.Opacity = 0;
        var slide = Anim(80 * dir, 0, 560, Expo(), 90);
        Cached(newPanel, slide);
        newPanel.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, 300, Expo(), 90));
        nt.BeginAnimation(TranslateTransform.XProperty, slide);
        ScaleTo(ns, 0.985, 1, 560, Expo(), 90);
    }

    /// <summary>Le voci di una lista entrano scivolando una dopo l'altra.</summary>
    public static void Cascade(ItemsControl list)
    {
        list.Dispatcher.BeginInvoke(() =>
        {
            for (int i = 0; i < Math.Min(list.Items.Count, 16); i++)
            {
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
                var (_, t) = Xf(c);
                c.Opacity = 0;
                var slide = Anim(22, 0, 460, Expo(), 24 * i);
                Cached(c, slide);
                c.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, 260, Expo(), 24 * i));
                t.BeginAnimation(TranslateTransform.XProperty, slide);
            }
        }, DispatcherPriority.Loaded);
    }

    // ---------------------------------------------------------------- piccoli gesti
    /// <summary>Comparsa elastica (cerchio del passo corrente, badge).</summary>
    public static void Pop(FrameworkElement e)
    {
        var (s, _) = Xf(e);
        ScaleTo(s, 0.4, 1, 650, Spring(5));
    }

    /// <summary>Scala a molla verso un valore (zona di trascinamento, hover).</summary>
    public static void Spring(FrameworkElement e, double to)
    {
        var (s, _) = Xf(e);
        ScaleTo(s, s.ScaleX, to, 600, Spring(5));
    }

    /// <summary>Numero che "conta" fino al nuovo valore (curva quintica, 600 ms).</summary>
    public static void CountTo(TextBlock tb, int value)
    {
        var from = tb.Tag is int f ? f : (int.TryParse(tb.Text, out var p) ? p : 0);
        tb.Tag = value;
        if (from == value) { tb.Text = value.ToString(); return; }
        var start = DateTime.UtcNow;
        void Step(object? s, EventArgs e)
        {
            var t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / 600);
            var k = 1 - Math.Pow(1 - t, 5);
            tb.Text = ((int)Math.Round(from + (value - from) * k)).ToString();
            if (t >= 1 || !Equals(tb.Tag, value)) CompositionTarget.Rendering -= Step;
        }
        CompositionTarget.Rendering += Step;
        if (value > from) Pop(tb);
    }

    /// <summary>"Respiro" continuo: opacita' e scala insieme (stato in corso).</summary>
    public static void Pulse(UIElement e, bool on)
    {
        var (s, _) = Xf(e);
        if (!on)
        {
            e.BeginAnimation(UIElement.OpacityProperty, null);
            s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            e.Opacity = 1;
            return;
        }
        if (e.HasAnimatedProperties) return;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        var grow = new DoubleAnimation(1, 1.35, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>Scuotimento smorzato: "questo campo non va bene".</summary>
    public static void Shake(FrameworkElement e)
    {
        var (_, t) = Xf(e);
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        var amp = 9.0;
        for (int i = 1; i <= 6; i++, amp *= 0.62)
            a.KeyFrames.Add(new EasingDoubleKeyFrame(i % 2 == 1 ? -amp : amp, KeyTime.FromPercent(i / 7.0), new SineEase()));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        t.BeginAnimation(TranslateTransform.XProperty, a);
        if (e is Control c) c.Focus();
    }

    /// <summary>Pannello flottante che sale/scende con una molla (barra della selezione).</summary>
    public static void SlideVisible(FrameworkElement e, bool show, double distance = 30)
    {
        if (show == (e.Visibility == Visibility.Visible && e.Opacity > 0.5)) return;
        var (s, t) = Xf(e);
        if (show)
        {
            e.Visibility = Visibility.Visible;
            e.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, 220, Expo()));
            t.BeginAnimation(TranslateTransform.YProperty, Anim(distance, 0, 640, Spring(6)));
            ScaleTo(s, 0.92, 1, 640, Spring(6));
        }
        else
        {
            var fade = Anim(e.Opacity, 0, 180, Expo(EasingMode.EaseIn));
            fade.Completed += (_, _) => { if (e.Opacity < 0.05) e.Visibility = Visibility.Collapsed; };
            e.BeginAnimation(UIElement.OpacityProperty, fade);
            t.BeginAnimation(TranslateTransform.YProperty, Anim(0, distance, 200, Expo(EasingMode.EaseIn)));
            ScaleTo(s, 1, 0.95, 200, Expo(EasingMode.EaseIn));
        }
    }

    /// <summary>Indicatore della sidebar: scivola sotto la voce scelta con una molla morbida.</summary>
    public static void MoveIndicator(FrameworkElement pill, FrameworkElement target, FrameworkElement container, bool immediate)
    {
        var p = target.TranslatePoint(new Point(0, 0), container);
        if (pill.RenderTransform is not TranslateTransform tt) pill.RenderTransform = tt = new TranslateTransform();
        pill.Height = target.ActualHeight;
        if (immediate || pill.Opacity < 0.5)
        {
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = p.Y;
            pill.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, 300, Expo()));
            return;
        }
        tt.BeginAnimation(TranslateTransform.YProperty, Anim(tt.Y, p.Y, 620, Spring(7)));
    }

    // ---------------------------------------------------------------- festa
    /// <summary>Fine procedura: l'illustrazione salta, ondeggia e spara coriandoli nei colori del tema.</summary>
    public static void Celebrate(FrameworkElement anchor)
    {
        var (s, _) = Xf(anchor);
        ScaleTo(s, 0.3, 1, 900, Spring(4));
        if (anchor.Parent is FrameworkElement host && FindHostPanel(host) is Panel panel)
        {
            panel.Dispatcher.BeginInvoke(() => Confetti(anchor, panel), DispatcherPriority.Loaded);
        }
    }

    /// <summary>La griglia piu' esterna della pagina: i coriandoli devono avere spazio, non il riquadro dell'immagine.</summary>
    private static Panel? FindHostPanel(DependencyObject d)
    {
        Grid? last = null;
        for (var cur = d; cur is not null && cur is not UserControl && cur is not Window; cur = VisualTreeHelper.GetParent(cur))
            if (cur is Grid g) last = g;
        return last;
    }

    private static void Confetti(FrameworkElement anchor, Panel host)
    {
        var canvas = new Canvas { IsHitTestVisible = false, ClipToBounds = false };
        if (host is Grid g) { Grid.SetRowSpan(canvas, Math.Max(1, g.RowDefinitions.Count)); Grid.SetColumnSpan(canvas, Math.Max(1, g.ColumnDefinitions.Count)); }
        host.Children.Add(canvas);
        var c = anchor.TranslatePoint(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2), host);
        string[] colors = ["Accent", "Yellow", "Green", "Blue", "Pink", "Purple", "Teal"];
        var rnd = new Random();
        for (int i = 0; i < 46; i++)
        {
            var piece = new Rectangle { Width = rnd.Next(6, 10), Height = rnd.Next(9, 15), RadiusX = 2, RadiusY = 2 };
            piece.SetResourceReference(Shape.FillProperty, colors[i % colors.Length]);
            var rt = new RotateTransform(rnd.Next(0, 360));
            var tt = new TranslateTransform();
            piece.RenderTransform = new TransformGroup { Children = { rt, tt } };
            piece.RenderTransformOrigin = new Point(0.5, 0.5);
            Canvas.SetLeft(piece, c.X);
            Canvas.SetTop(piece, c.Y);
            canvas.Children.Add(piece);

            var ang = rnd.NextDouble() * Math.PI * 2;
            var speed = 140 + rnd.NextDouble() * 220;
            var dx = Math.Cos(ang) * speed;
            var peak = -Math.Abs(Math.Sin(ang)) * speed - 60;
            var ms = 1300 + rnd.Next(0, 500);
            tt.BeginAnimation(TranslateTransform.XProperty, Anim(0, dx * 1.4, ms, new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            var y = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(ms) };
            y.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromPercent(0.32), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            y.KeyFrames.Add(new EasingDoubleKeyFrame(peak + 380, KeyTime.FromPercent(1), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            tt.BeginAnimation(TranslateTransform.YProperty, y);
            rt.BeginAnimation(RotateTransform.AngleProperty, Anim(rt.Angle, rt.Angle + rnd.Next(-540, 540), ms, null));
            piece.BeginAnimation(UIElement.OpacityProperty, Anim(1, 0, 400, null, ms - 400));
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        timer.Tick += (_, _) => { timer.Stop(); host.Children.Remove(canvas); };
        timer.Start();
    }

}
