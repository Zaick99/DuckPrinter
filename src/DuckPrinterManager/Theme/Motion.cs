using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DuckPrinterManager.Theme;

public static class Motion
{
    public static IEasingFunction Ease(string kind = "Cubic", EasingMode mode = EasingMode.EaseOut, double amplitude = 0.4)
    {
        EasingFunctionBase ease = kind switch
        {
            "Back" => new BackEase { Amplitude = amplitude },
            "Quint" => new QuinticEase(),
            _ => new CubicEase()
        };

        ease.EasingMode = mode;
        return ease;
    }

    public static DoubleAnimation Slide(double from, double to, int milliseconds, IEasingFunction? ease = null) =>
        new(from, to, new Duration(TimeSpan.FromMilliseconds(milliseconds))) { EasingFunction = ease };

    public static readonly DependencyProperty OffsetProperty =
        DependencyProperty.RegisterAttached(
            "Offset",
            typeof(double),
            typeof(Motion),
            new PropertyMetadata(0.0, OnOffsetChanged));

    private static void OnOffsetChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is ScrollViewer view)
        {
            view.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    public static void ScrollTo(ScrollViewer view, double offset, int milliseconds = 420)
    {
        double from = view.VerticalOffset;

        if (Math.Abs(offset - from) < 2)
        {
            return;
        }

        view.BeginAnimation(OffsetProperty, null);
        view.SetValue(OffsetProperty, from);
        view.BeginAnimation(OffsetProperty, Slide(from, offset, milliseconds, Ease("Quint")));
    }

    public static void MovePill(TranslateTransform? slide, ScaleTransform? squash, double x, bool immediate = false)
    {
        if (slide is null)
        {
            return;
        }

        if (immediate)
        {
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            slide.X = x;
            return;
        }

        slide.BeginAnimation(
            TranslateTransform.XProperty,
            Slide(slide.X, x, 340, Ease("Back", EasingMode.EaseOut, 0.28)));

        if (squash is null)
        {
            return;
        }

        DoubleAnimationUsingKeyFrames pinch = new()
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(340))
        };

        foreach ((int at, double value) in ((int, double)[])[(0, 1.0), (140, 0.88), (340, 1.0)])
        {
            pinch.KeyFrames.Add(new EasingDoubleKeyFrame(
                value,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at)),
                Ease("Cubic", EasingMode.EaseInOut)));
        }

        squash.BeginAnimation(ScaleTransform.ScaleXProperty, pinch);
    }

    public static void OpenDialog(FrameworkElement? root)
    {
        if (root?.RenderTransform is not TransformGroup group
            || group.Children.Count < 2
            || group.Children[0] is not ScaleTransform scale
            || group.Children[1] is not TranslateTransform move)
        {
            if (root is not null)
            {
                root.Opacity = 1;
            }
            return;
        }

        IEasingFunction ease = Ease();
        root.BeginAnimation(UIElement.OpacityProperty, Slide(0, 1, 200, ease));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Slide(0.96, 1, 260, ease));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Slide(0.96, 1, 260, ease));
        move.BeginAnimation(TranslateTransform.YProperty, Slide(12, 0, 260, ease));
    }

    public static void CloseDialog(Window dialog, FrameworkElement? root, bool? result)
    {
        if (root is null || dialog.Tag as string == "chiudendo")
        {
            Finish(dialog, result);
            return;
        }

        dialog.Tag = "chiudendo";
        IEasingFunction ease = Ease(mode: EasingMode.EaseIn);

        if (root.RenderTransform is TransformGroup { Children.Count: > 0 } group
            && group.Children[0] is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Slide(1, 0.97, 150, ease));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Slide(1, 0.97, 150, ease));
        }

        DoubleAnimation fade = Slide(root.Opacity, 0, 150, ease);
        fade.Completed += (_, _) => Finish(dialog, result);
        root.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static void Finish(Window dialog, bool? result)
    {
        try
        {
            dialog.DialogResult = result;
        }
        catch (InvalidOperationException)
        {
        }

        dialog.Close();
    }

    public const double WorkingScale = 1.45;

    public static ScaleTransform Scalable(FrameworkElement element)
    {
        if (element.RenderTransform is ScaleTransform existing)
        {
            return existing;
        }

        ScaleTransform scale = new(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        return scale;
    }

    public static void Grow(ScaleTransform? scale, double to, int milliseconds)
    {
        if (scale is null)
        {
            return;
        }

        IEasingFunction ease = Ease("Cubic", EasingMode.EaseInOut);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Slide(scale.ScaleX, to, milliseconds, ease));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Slide(scale.ScaleY, to, milliseconds, ease));
    }

    public static DoubleAnimation Spin(int milliseconds, double from = 0, double to = 360) =>
        new(from, to, new Duration(TimeSpan.FromMilliseconds(milliseconds)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

    public static DoubleAnimation Pulse(double from, double to, int milliseconds) =>
        new(from, to, new Duration(TimeSpan.FromMilliseconds(milliseconds)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Ease("Cubic", EasingMode.EaseInOut)
        };

    public static void Shake(TranslateTransform? target)
    {
        if (target is null)
        {
            return;
        }

        DoubleAnimationUsingKeyFrames shake = new()
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(320))
        };

        foreach ((int at, double offset) in
                 ((int, double)[])[(0, 0), (70, -9), (140, 8), (210, -5), (270, 3), (320, 0)])
        {
            shake.KeyFrames.Add(new EasingDoubleKeyFrame(
                offset, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at))));
        }

        target.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    public static void Enter(UIElement view, TranslateTransform? shift, double from)
    {
        view.BeginAnimation(UIElement.OpacityProperty, Slide(0, 1, 220, Ease()));
        shift?.BeginAnimation(TranslateTransform.XProperty, Slide(from, 0, 300, Ease()));
    }
}
