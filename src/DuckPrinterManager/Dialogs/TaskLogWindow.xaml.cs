using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DuckPrinterManager.Core.Models;
using DuckPrinterManager.Core.Native;
using DuckPrinterManager.Ui;
using Microsoft.Win32;
using TaskStatus = DuckPrinterManager.Core.Models.TaskStatus;

namespace DuckPrinterManager.Dialogs;

/// <summary>Log di un singolo task, aggiornato dal vivo (ring buffer per task in duck_core).</summary>
public partial class TaskLogWindow : Window
{
    private static readonly Dictionary<string, TaskLogWindow> Open_ = [];
    private readonly string _id;
    private readonly ObservableCollection<LogRow> _rows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ulong _after;

    public static void Open(Window owner, string taskId)
    {
        if (Open_.TryGetValue(taskId, out var w)) { w.Activate(); return; }
        w = new TaskLogWindow(owner, taskId);
        Open_[taskId] = w;
        w.Show();
    }

    private TaskLogWindow(Window owner, string id)
    {
        InitializeComponent();
        Owner = owner;
        _id = id;
        LstLog.ItemsSource = _rows;
        _timer.Tick += (_, _) => Poll();
        Loaded += (_, _) => { Poll(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); Open_.Remove(_id); };
    }

    private void Poll()
    {
        TaskInfo t;
        try { t = AppHost.Core.GetTask(_id); }
        catch (DuckCoreException)
        {
            TitleText.Text = $"Task {_id} (eliminato)";
            _timer.Stop();
            return;
        }
        var p = t.GetPayload();
        Title = $"Log {t.Host} - {_id}";
        TitleText.Text = $"{t.Host}  ·  {_id}";
        var (chip, fg) = Fmt.StatusVisual(t.Status);
        StatusChip.Style = (Style)FindResource(chip);
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, fg);
        StatusText.Text = TaskStatus.Display(t.Status);
        InfoOp.Text = t.Action == TaskActions.Deploy
            ? (p.DriverOnly ? $"Solo driver: {p.DriverName}" : $"Installa \"{p.PrinterName}\" su {p.PrinterAddress} (driver {p.DriverName}, predefinita: {p.DefaultMode})")
            : t.Label;
        InfoCreated.Text = Fmt.When(t.Created);
        InfoCounters.Text = $"{t.Attempts} tentativi · {t.Errors} errori · trovato spento {t.OfflineHits} volte";
        InfoNext.Text = new TaskRow(t).NextText;
        InfoLast.Text = string.IsNullOrEmpty(t.LastError) ? t.LastMessage : t.LastError;
        BtnCheck.IsEnabled = t.Status is TaskStatus.Waiting or TaskStatus.Retry;

        var entries = AppHost.Core.GetLog(_id, _after, 2000);
        if (entries.Count == 0) return;
        foreach (var e in entries) _rows.Add(new LogRow(e));
        _after = entries[^1].Seq;
        if (ChkFollow.IsChecked == true) LstLog.ScrollIntoView(_rows[^1]);
    }

    private string Text => string.Join(Environment.NewLine,
        (LstLog.SelectedItems.Count > 0 ? LstLog.SelectedItems.Cast<LogRow>().OrderBy(r => r.Entry.Seq) : _rows.AsEnumerable()).Select(r => r.ToString()));

    private void OnCopy(object sender, RoutedEventArgs e) => Clipboard.SetText(Text);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { FileName = $"task_{_id}.log", Filter = "Log (*.log)|*.log|Testo (*.txt)|*.txt" };
        if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, string.Join(Environment.NewLine, _rows.Select(r => r.ToString())));
    }

    private void OnCheckNow(object sender, RoutedEventArgs e)
    {
        AppHost.Core.CheckNow([_id]);
        Poll();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
