using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DuckPrinterManager.Core.Services;
using DuckPrinterManager.Dialogs;
using DuckPrinterManager.Ui;

namespace DuckPrinterManager.Pages;

public partial class LogPage : UserControl, IPage
{
    private const int MaxRows = 5000;
    private readonly List<LogRow> _all = [];
    private readonly ObservableCollection<LogRow> _view = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private ulong _after;
    private string _level = "all";
    private int _warn, _err, _total;

    public LogPage()
    {
        InitializeComponent();
        LstLog.ItemsSource = _view;
        _timer.Tick += (_, _) => Poll();
        Loaded += (_, _) => { Poll(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    public void OnShown() => Poll();

    private void Poll()
    {
        var entries = AppHost.Core.GetLog(null, _after, 2000);
        if (entries.Count == 0) return;
        _after = entries[^1].Seq;
        foreach (var e in entries)
        {
            var row = new LogRow(e);
            _all.Add(row);
            _total++;
            if (e.Level == "WARN") _warn++;
            if (e.Level == "ERR") _err++;
            if (Visible(row)) _view.Add(row);
        }
        Fx.CountTo(StatAll, _total);
        Fx.CountTo(StatWarn, _warn);
        Fx.CountTo(StatErr, _err);
        TxtLast.Text = "ultimo " + entries[^1].Time.ToString("HH:mm:ss");
        UpdateEmpty();
        if (_all.Count > MaxRows) { _all.RemoveRange(0, _all.Count - MaxRows); Rebuild(); }
        if (ChkFollow.IsChecked == true && _view.Count > 0) LstLog.ScrollIntoView(_view[^1]);
    }

    private bool Visible(LogRow r)
    {
        var ok = _level switch
        {
            "task" => r.Level == "TASK" || r.Task.Length > 0 && r.Level != "DEBUG",
            "warn" => r.Level is "WARN" or "ERR",
            "err" => r.Level == "ERR",
            "debug" => true,
            _ => r.Level != "DEBUG",
        };
        var q = TxtSearch.Text.Trim();
        return ok && (q.Length == 0 || r.Msg.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Task.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateEmpty()
    {
        EmptyState.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _all.Count == 0 ? "Ancora niente" : "Niente da mostrare";
        EmptyText.Text = _all.Count == 0 ? "Le operazioni compariranno qui appena partono." : "Nessuna riga corrisponde al filtro scelto.";
    }

    private void OnLevel(object sender, RoutedEventArgs e)
    {
        _level = (string)((RadioButton)sender).Tag;
        if (!IsLoaded) return;
        Rebuild();
        Fx.Cascade(LstLog);
    }

    private void Rebuild()
    {
        _view.Clear();
        foreach (var r in _all.Where(Visible)) _view.Add(r);
        UpdateEmpty();
        if (ChkFollow.IsChecked == true && _view.Count > 0) LstLog.ScrollIntoView(_view[^1]);
    }

    private void OnFilter(object sender, RoutedEventArgs e) { if (IsLoaded) Rebuild(); }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var rows = LstLog.SelectedItems.Count > 0 ? LstLog.SelectedItems.Cast<LogRow>().OrderBy(r => r.Entry.Seq) : _view.AsEnumerable();
        Clipboard.SetText(string.Join(Environment.NewLine, rows.Select(r => r.ToString())));
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true }); }
        catch (Exception ex) { DuckDialog.Error(Window.GetWindow(this), "Log", ex.Message); }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _all.Clear();
        _view.Clear();
        _total = _warn = _err = 0;
        Fx.CountTo(StatAll, 0); Fx.CountTo(StatWarn, 0); Fx.CountTo(StatErr, 0);
        UpdateEmpty();
    }

    private void OnOpenTask(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is LogRow { Task.Length: > 0 } r) TaskLogWindow.Open(Window.GetWindow(this), r.Task);
    }
}
