using System.Windows;

namespace DuckPrinterManager.Ui;

/// <summary>
/// Proprieta' allegata: lega Foreground/Fill a una chiave di token scelta a runtime
/// (es. il colore del livello di log) mantenendo il legame DynamicResource col tema.
/// </summary>
public static class Tok
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.RegisterAttached(
        "Foreground", typeof(string), typeof(Tok), new PropertyMetadata(null, (d, e) =>
        {
            if (e.NewValue is not string key || key.Length == 0) return;
            switch (d)
            {
                case System.Windows.Controls.TextBlock tb: tb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, key); break;
                case System.Windows.Controls.Control c: c.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, key); break;
                case System.Windows.Shapes.Shape s: s.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key); break;
            }
        }));

    public static void SetForeground(DependencyObject d, string value) => d.SetValue(ForegroundProperty, value);
    public static string GetForeground(DependencyObject d) => (string)d.GetValue(ForegroundProperty);
}
