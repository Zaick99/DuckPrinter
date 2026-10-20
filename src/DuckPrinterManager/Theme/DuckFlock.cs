using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DuckPrinterManager.Theme;

/// <summary>
/// Tappeto di paperelle sullo sfondo: sagome duck.png tinte con DuckOrange (DynamicResource:
/// cambia da sola col tema) al 6% di opacita', in lenta deriva se l'animazione e' attiva.
/// </summary>
public sealed class DuckFlock
{
    private readonly Canvas _canvas;
    private readonly List<(Rectangle Shape, double Vx, double Vy, double Spin)> _ducks = [];
    private readonly Random _rnd = new(7);
    private bool _animate;
    private TimeSpan _last;

    public DuckFlock(Canvas canvas)
    {
        _canvas = canvas;
        _canvas.SizeChanged += (_, _) => { if (_ducks.Count == 0) Build(); };
    }

    public bool Animate
    {
        get => _animate;
        set
        {
            if (_animate == value) return;
            _animate = value;
            if (value) CompositionTarget.Rendering += OnRender;
            else CompositionTarget.Rendering -= OnRender;
            _last = TimeSpan.Zero;
        }
    }

    private void Build()
    {
        if (_canvas.ActualWidth < 10) return;
        var mask = new ImageBrush(new BitmapImage(new Uri("pack://application:,,,/Assets/duck.png"))) { Stretch = Stretch.Uniform };
        mask.Freeze();
        for (int i = 0; i < 16; i++)
        {
            var size = 34 + _rnd.NextDouble() * 50;
            var r = new Rectangle { Width = size, Height = size, OpacityMask = mask, Opacity = 0.06, IsHitTestVisible = false };
            r.SetResourceReference(Shape.FillProperty, "DuckOrange");
            r.RenderTransformOrigin = new Point(0.5, 0.5);
            var flip = _rnd.Next(2) == 0 ? -1 : 1;
            r.RenderTransform = new TransformGroup { Children = { new ScaleTransform(flip, 1), new RotateTransform(_rnd.Next(-12, 12)) } };
            Canvas.SetLeft(r, _rnd.NextDouble() * _canvas.ActualWidth);
            Canvas.SetTop(r, _rnd.NextDouble() * _canvas.ActualHeight);
            _canvas.Children.Add(r);
            _ducks.Add((r, (_rnd.NextDouble() * 10 + 4) * -flip, _rnd.NextDouble() * 4 - 2, _rnd.NextDouble() * 4 - 2));
        }
    }

    private void OnRender(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (_last == TimeSpan.Zero) { _last = t; return; }
        var dt = Math.Min((t - _last).TotalSeconds, 0.1);
        _last = t;
        if (dt <= 0) return;
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        foreach (var (s, vx, vy, spin) in _ducks)
        {
            var x = Canvas.GetLeft(s) + vx * dt;
            var y = Canvas.GetTop(s) + vy * dt;
            if (x < -s.Width) x = w; else if (x > w) x = -s.Width;
            if (y < -s.Height) y = h; else if (y > h) y = -s.Height;
            Canvas.SetLeft(s, x);
            Canvas.SetTop(s, y);
            if (s.RenderTransform is TransformGroup g && g.Children[1] is RotateTransform rt)
                rt.Angle = 10 * Math.Sin((t.TotalSeconds + spin * 10) / (3 + Math.Abs(spin)));
        }
    }
}
