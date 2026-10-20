using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DuckPrinterManager.Ui;

/// <summary>Indicatore di attivita': arco d'accento che ruota e "respira" (si allunga e accorcia).</summary>
public sealed class Spinner : FrameworkElement
{
    public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(
        nameof(Sweep), typeof(double), typeof(Spinner), new FrameworkPropertyMetadata(90.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }

    public double Thickness { get; set; } = 5;

    private readonly RotateTransform _rot = new();

    public Spinner()
    {
        RenderTransform = _rot;
        RenderTransformOrigin = new Point(0.5, 0.5);
        IsVisibleChanged += (_, _) => Run(IsVisible);
        Loaded += (_, _) => Run(IsVisible);
        Unloaded += (_, _) => Run(false);
    }

    private void Run(bool on)
    {
        if (!on)
        {
            _rot.BeginAnimation(RotateTransform.AngleProperty, null);
            BeginAnimation(SweepProperty, null);
            return;
        }
        _rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(950)) { RepeatBehavior = RepeatBehavior.Forever });
        BeginAnimation(SweepProperty, new DoubleAnimation(40, 260, TimeSpan.FromMilliseconds(1100))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < Thickness * 3) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = size / 2 - Thickness / 2;
        dc.DrawEllipse(null, new Pen(TryFindResource("SeparatorSoft") as Brush ?? Brushes.LightGray, Thickness), c, r, r);
        var a = Math.Clamp(Sweep, 1, 359) * Math.PI / 180;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
            ctx.ArcTo(new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a)), new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true, true);
        }
        dc.DrawGeometry(null, new Pen(TryFindResource("Accent") as Brush ?? Brushes.Orange, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
    }
}
