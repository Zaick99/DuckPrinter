using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DuckPrinterManager.Core.Models;

namespace DuckPrinterManager.Ui;

/// <summary>Un campione dello stato della coda.</summary>
public readonly record struct QueueSample(DateTime Time, int Done, int Running, int Waiting, int Errors, int Total);

/// <summary>
/// Storico in memoria della coda: un campione ogni 2 s, ultime 2 ore. Gira sempre (anche
/// quando la pagina Task non e' aperta), cosi' il grafico ha gia' la storia quando lo si apre.
/// </summary>
public static class QueueHistory
{
    private const int Capacity = 3600;
    private static readonly List<QueueSample> Samples = [];
    private static DispatcherTimer? _timer;

    public static event Action? Sampled;
    public static IReadOnlyList<QueueSample> All => Samples;

    public static void Start()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Take();
        _timer.Start();
        Take();
    }

    private static void Take()
    {
        try
        {
            var s = AppHost.Core.Revision().Stats;
            Samples.Add(new QueueSample(DateTime.Now, s.Done, s.Running, s.Waiting + s.Paused, s.Retry, s.Total));
            if (Samples.Count > Capacity) Samples.RemoveRange(0, Samples.Count - Capacity);
            Sampled?.Invoke();
        }
        catch { }
    }

    /// <summary>Task completati al minuto negli ultimi <paramref name="minutes"/> minuti (per la stima di fine).</summary>
    public static double Throughput(int minutes = 10)
    {
        if (Samples.Count < 2) return 0;
        var now = Samples[^1];
        var from = Samples.LastOrDefault(s => s.Time <= now.Time.AddMinutes(-minutes));
        if (from == default) from = Samples[0];
        var mins = (now.Time - from.Time).TotalMinutes;
        return mins < 0.2 ? 0 : Math.Max(0, now.Done - from.Done) / mins;
    }
}

/// <summary>
/// Grafico ad area/linee in tempo reale (regole dataviz: linee 2 px, area al 10% solo per la
/// serie principale, griglia a filo recessiva, un solo asse, valore diretto a fine linea, testo
/// sempre nei colori del testo, mirino + tooltip al passaggio del mouse). Scorre in continuo.
/// </summary>
public sealed class LiveChart : FrameworkElement
{
    public static readonly DependencyProperty WindowMinutesProperty = DependencyProperty.Register(
        nameof(WindowMinutes), typeof(double), typeof(LiveChart), new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double WindowMinutes { get => (double)GetValue(WindowMinutesProperty); set => SetValue(WindowMinutesProperty, value); }

    public static readonly (string Name, string Token, Func<QueueSample, int> Value)[] Series =
    [
        ("Completati", "ChartDone", s => s.Done),
        ("In corso", "ChartRun", s => s.Running),
        ("In coda", "ChartWait", s => s.Waiting),
        ("In errore", "ChartErr", s => s.Errors),
    ];

    private const double PadL = 34, PadR = 46, PadT = 10, PadB = 20;
    private double _shownMax = 4, _shownWindow = 30;
    private Point? _mouse;
    private TimeSpan _lastFrame;
    private readonly Typeface _font = new("Segoe UI");

    public LiveChart()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
        MouseMove += (_, e) => { _mouse = e.GetPosition(this); InvalidateVisual(); };
        MouseLeave += (_, _) => { _mouse = null; InvalidateVisual(); };
        Theme.ThemeManager.Changed += _ => InvalidateVisual();
    }

    /// <summary>~30 fps: abbastanza per uno scorrimento continuo senza costo apprezzabile.</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        var t = ((RenderingEventArgs)e).RenderingTime;
        if ((t - _lastFrame).TotalMilliseconds < 33) return;
        var dt = Math.Min(0.1, (t - _lastFrame).TotalSeconds);
        _lastFrame = t;
        // scala verticale e finestra temporale convergono morbidamente verso il valore giusto
        _shownMax += (TargetMax() - _shownMax) * Math.Min(1, dt * 6);
        _shownWindow += (WindowMinutes - _shownWindow) * Math.Min(1, dt * 7);
        InvalidateVisual();
    }

    private IEnumerable<QueueSample> Visible(DateTime now) =>
        QueueHistory.All.Where(s => s.Time >= now.AddMinutes(-_shownWindow - 0.1));

    private double TargetMax()
    {
        var now = DateTime.Now;
        var max = Visible(now).Select(s => Series.Max(x => x.Value(s))).DefaultIfEmpty(0).Max();
        return NiceCeil(Math.Max(4, max * 1.15));
    }

    private static double NiceCeil(double v)
    {
        var p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 }) if (m * p >= v) return m * p;
        return 10 * p;
    }

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private FormattedText Text(string s, double size, Brush b, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(_font.FontFamily, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private bool _renderErrorLogged;

    /// <summary>Un errore di disegno non deve mai chiudere la pagina: si salta il fotogramma e si annota una volta.</summary>
    protected override void OnRender(DrawingContext dc)
    {
        try { Draw(dc); }
        catch (Exception ex)
        {
            if (_renderErrorLogged) return;
            _renderErrorLogged = true;
            AppHost.TryLog("ERR", "Grafico task: " + ex.Message);
        }
    }

    private void Draw(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 80 || h < 60) return;
        var now = DateTime.Now;
        var plot = new Rect(PadL, PadT, w - PadL - PadR, h - PadT - PadB);
        var from = now.AddMinutes(-_shownWindow);
        double X(DateTime t) => plot.Left + (t - from).TotalMilliseconds / (now - from).TotalMilliseconds * plot.Width;
        double Y(double v) => plot.Bottom - v / _shownMax * plot.Height;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // area sensibile al mouse

        // griglia a filo + etichette asse Y (numeri tondi)
        // niente Freeze(): i pennelli del tema sono animati (sfumano al cambio giorno/notte)
        // e una penna che li usa non e' congelabile
        var grid = new Pen(Res("SeparatorSoft"), 1);
        var muted = Res("LabelTertiary");
        var step = NiceCeil(_shownMax / 4);
        for (double v = 0; v <= _shownMax + 0.001; v += step)
        {
            var y = Math.Round(Y(v)) + 0.5;
            if (y < plot.Top - 1) break;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Text(v.ToString("0", CultureInfo.CurrentCulture), 10, muted);
            dc.DrawText(ft, new Point(plot.Left - ft.Width - 6, y - ft.Height / 2));
        }
        // etichette tempo
        var minutesStep = _shownWindow <= 6 ? 1 : _shownWindow <= 35 ? 5 : 30;
        var first = new DateTime(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0).AddMinutes(1);
        for (var t = first; t <= now; t = t.AddMinutes(1))
        {
            if (t.Minute % minutesStep != 0) continue;
            var x = X(t);
            var ft = Text(t.ToString("HH:mm"), 10, muted);
            if (x - ft.Width / 2 > plot.Left && x + ft.Width / 2 < plot.Right) dc.DrawText(ft, new Point(x - ft.Width / 2, plot.Bottom + 4));
        }

        var data = Visible(now).ToList();
        if (data.Count < 2)
        {
            var ft = Text("In attesa dei primi campioni...", 12, muted);
            dc.DrawText(ft, new Point(plot.Left + (plot.Width - ft.Width) / 2, plot.Top + (plot.Height - ft.Height) / 2));
            return;
        }

        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, 0, plot.Width + PadR, h)));
        // serie: area al 10% solo per "Completati", poi tutte le linee 2 px
        for (int si = Series.Length - 1; si >= 0; si--)
        {
            var (_, token, val) = Series[si];
            var brush = Res(token);
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(new Point(X(data[0].Time), Y(val(data[0]))), false, false);
                g.PolyLineTo(data.Skip(1).Select(s => new Point(X(s.Time), Y(val(s)))).Append(new Point(X(now), Y(val(data[^1])))).ToList(), true, true);
            }
            line.Freeze();
            if (si == 0)
            {
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(X(data[0].Time), plot.Bottom), true, true);
                    g.PolyLineTo(data.Select(s => new Point(X(s.Time), Y(val(s)))).Append(new Point(X(now), Y(val(data[^1])))).Append(new Point(X(now), plot.Bottom)).ToList(), true, true);
                }
                area.Freeze();
                var wash = brush.CloneCurrentValue(); // colore di questo istante, senza l'animazione
                wash.Opacity = 0.12;
                dc.DrawGeometry(wash, null, area);
            }
            var pen = new Pen(brush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, line);
        }

        // valori diretti a fine linea (testo nei colori del testo, pallino del colore della serie)
        var surface = Res("Panel");
        var ring = new Pen(surface, 2);
        var usedY = new List<double>();
        foreach (var (name, token, val) in Series.OrderByDescending(s => s.Value(data[^1])))
        {
            var v = val(data[^1]);
            var p = new Point(X(now) - 1, Y(v));
            dc.DrawEllipse(Res(token), ring, p, 4.5, 4.5);
            var ly = p.Y;
            while (usedY.Any(u => Math.Abs(u - ly) < 12)) ly += 12;
            usedY.Add(ly);
            var ft = Text(v.ToString(CultureInfo.CurrentCulture), 11, Res("Label"), bold: true);
            dc.DrawText(ft, new Point(plot.Right + 8, ly - ft.Height / 2));
        }
        dc.Pop();

        // mirino + tooltip
        if (_mouse is { } m && plot.Contains(m))
        {
            var t = from + TimeSpan.FromMilliseconds((m.X - plot.Left) / plot.Width * (now - from).TotalMilliseconds);
            var s = data.MinBy(d => Math.Abs((d.Time - t).TotalMilliseconds));
            var x = X(s.Time);
            var cross = new Pen(Res("LabelQuaternary"), 1);
            dc.DrawLine(cross, new Point(Math.Round(x) + 0.5, plot.Top), new Point(Math.Round(x) + 0.5, plot.Bottom));
            foreach (var (_, token, val) in Series) dc.DrawEllipse(Res(token), ring, new Point(x, Y(val(s))), 4.5, 4.5);

            var lines = Series.Select(se => (se.Token, Label: Text(se.Name, 11, Res("LabelSecondary")), Value: Text(se.Value(s).ToString(), 11, Res("Label"), true))).ToList();
            var head = Text(s.Time.ToString("HH:mm:ss") + $"  ·  {s.Total} task", 11, Res("Label"), true);
            var bw = Math.Max(head.Width, lines.Max(l => 14 + l.Label.Width + 18 + l.Value.Width)) + 20;
            var bh = 12 + head.Height + 6 + lines.Count * 17 + 6;
            var bx = x + 14 + bw > w ? x - 14 - bw : x + 14;
            var by = Math.Clamp(m.Y - bh / 2, 2, h - bh - 2);
            dc.DrawRoundedRectangle(Res("BgElevated"), new Pen(Res("Separator"), 1), new Rect(bx, by, bw, bh), 8, 8);
            dc.DrawText(head, new Point(bx + 10, by + 8));
            var ry = by + 8 + head.Height + 6;
            foreach (var l in lines)
            {
                dc.DrawEllipse(Res(l.Token), null, new Point(bx + 14, ry + 8), 4, 4);
                dc.DrawText(l.Label, new Point(bx + 24, ry));
                dc.DrawText(l.Value, new Point(bx + bw - 10 - l.Value.Width, ry));
                ry += 17;
            }
        }
    }
}

/// <summary>Anello di avanzamento: traccia neutra + arco del colore "completati", valore animato.</summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public ProgressRing() => Theme.ThemeManager.Changed += _ => InvalidateVisual();

    /// <summary>Porta il valore (0..1) a destinazione con una curva esponenziale.</summary>
    public void AnimateTo(double v)
    {
        v = Math.Clamp(v, 0, 1);
        if (Math.Abs(v - Value) < 0.0005) return;
        BeginAnimation(ValueProperty, new System.Windows.Media.Animation.DoubleAnimation(Value, v, TimeSpan.FromMilliseconds(900)) { EasingFunction = Fx.Expo() });
    }

    protected override void OnRender(DrawingContext dc)
    {
        const double thick = 10;
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < thick * 3) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = size / 2 - thick / 2;
        var track = new Pen(TryFindResource("SeparatorSoft") as Brush ?? Brushes.LightGray, thick);
        dc.DrawEllipse(null, track, c, r, r);
        var v = Math.Clamp(Value, 0, 1);
        if (v <= 0.001) return;
        var pen = new Pen(TryFindResource("ChartDone") as Brush ?? Brushes.Green, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        var a = v * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}
