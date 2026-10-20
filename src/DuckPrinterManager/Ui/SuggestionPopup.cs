using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace DuckPrinterManager.Ui;

/// <summary>Suggerimenti sotto una casella di testo (reparti): frecce + Invio, oppure clic.</summary>
public static class SuggestionPopup
{
    public static void Attach(TextBox box, Func<string, IEnumerable<string>> source)
    {
        var list = new ListBox { MaxHeight = 220, MinWidth = 260 };
        list.SetResourceReference(FrameworkElement.StyleProperty, "WellList");
        var popup = new Popup
        {
            PlacementTarget = box,
            Placement = PlacementMode.Bottom,
            AllowsTransparency = true,
            StaysOpen = false,
            Child = new Border { Margin = new Thickness(0, 4, 0, 0), Child = list },
        };
        bool picking = false;

        void Pick()
        {
            if (list.SelectedItem is not string s) return;
            picking = true;
            box.Text = s;
            box.CaretIndex = s.Length;
            picking = false;
            popup.IsOpen = false;
            box.Focus();
        }

        box.TextChanged += (_, _) =>
        {
            if (picking || !box.IsKeyboardFocusWithin) return;
            var items = source(box.Text).ToList();
            list.ItemsSource = items;
            popup.Width = Math.Max(box.ActualWidth, 260);
            popup.IsOpen = items.Count > 0 && !(items.Count == 1 && string.Equals(items[0], box.Text, StringComparison.CurrentCultureIgnoreCase));
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (!popup.IsOpen) return;
            switch (e.Key)
            {
                case Key.Down: list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.Items.Count - 1); list.ScrollIntoView(list.SelectedItem); e.Handled = true; break;
                case Key.Up: list.SelectedIndex = Math.Max(list.SelectedIndex - 1, 0); list.ScrollIntoView(list.SelectedItem); e.Handled = true; break;
                case Key.Enter or Key.Tab when list.SelectedItem is not null: Pick(); e.Handled = true; break;
                case Key.Escape: popup.IsOpen = false; e.Handled = true; break;
            }
        };
        box.LostKeyboardFocus += (_, e) => { if (e.NewFocus is not DependencyObject d || !IsInside(d, list)) popup.IsOpen = false; };
        list.PreviewMouseLeftButtonUp += (_, _) => Pick();
    }

    private static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (var cur = d; cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur))
            if (cur == container) return true;
        return false;
    }

    /// <summary>MAC mentre si scrive: solo hex minuscoli, niente separatori, massimo 12 (core Rust).</summary>
    public static void AttachMacFilter(TextBox box)
    {
        box.TextChanged += (_, _) =>
        {
            var clean = Core.Native.DuckCore.SanitizeMacTyping(box.Text);
            if (clean == box.Text) return;
            var pos = box.CaretIndex;
            box.Text = clean;
            box.CaretIndex = Math.Min(pos, clean.Length);
        };
    }
}
