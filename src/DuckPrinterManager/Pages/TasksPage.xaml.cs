using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DuckPrinterManager.Core.Models;
using DuckPrinterManager.Dialogs;
using DuckPrinterManager.Ui;
using TaskStatus = DuckPrinterManager.Core.Models.TaskStatus;

namespace DuckPrinterManager.Pages;

public partial class TasksPage : UserControl, IPage
{
    private readonly ObservableCollection<TaskRow> _rows = [];
    private readonly Dictionary<string, TaskRow> _byId = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ulong _revision;
    private int _ticks;
    private List<TaskInfo> _all = [];
    private int _filter;

    private static readonly (string Label, Func<TaskInfo, bool> Match)[] Filters =
    [
        ("Tutti", _ => true),
        ("Attivi", t => t.Status is not (TaskStatus.Done or TaskStatus.Expired)),
        ("In attesa", t => t.Status == TaskStatus.Waiting),
        ("In esecuzione", t => t.Status == TaskStatus.Running),
        ("In errore", t => t.Status == TaskStatus.Retry),
        ("In pausa", t => t.Status == TaskStatus.Paused),
        ("Completati", t => t.Status == TaskStatus.Done),
        ("Scaduti", t => t.Status == TaskStatus.Expired),
    ];

    public TasksPage()
    {
        InitializeComponent();
        LstTasks.ItemsSource = _rows;
        _timer.Tick += (_, _) => Refresh(false);
        Loaded += (_, _) => { _timer.Start(); Refresh(true); };
        Unloaded += (_, _) => _timer.Stop();
    }

    public void OnShown() => Refresh(true);

    private Window Owner => Window.GetWindow(this);

    // ---------------------------------------------------------------- aggiornamento
    private void Refresh(bool force)
    {
        var rev = AppHost.Core.Revision();
        UpdateHeader(rev);
        if (force || rev.Revision != _revision)
        {
            _revision = rev.Revision;
            _all = AppHost.Core.ListTasks();
            ApplyFilter();
        }
        else if (++_ticks % 10 == 0)
        {
            foreach (var r in _rows) r.Tick(); // "tra 7 min" si aggiorna da solo
        }
    }

    private void ApplyFilter()
    {
        var match = Filters[_filter].Match;
        var q = TxtSearch.Text.Trim();
        var visible = _all.Where(match).Where(t => q.Length == 0
            || t.Host.Contains(q, StringComparison.OrdinalIgnoreCase)
            || t.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
            || t.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || t.LastError.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        // aggiornamento sul posto: niente Clear(), cosi' selezione e scroll restano dove sono
        var keep = visible.Select(t => t.Id).ToHashSet();
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (keep.Contains(_rows[i].Id)) continue;
            _byId.Remove(_rows[i].Id);
            _rows.RemoveAt(i);
        }
        for (int i = 0; i < visible.Count; i++)
        {
            var t = visible[i];
            if (_byId.TryGetValue(t.Id, out var row))
            {
                row.Update(t);
                var cur = _rows.IndexOf(row);
                if (cur != i) _rows.Move(cur, i);
            }
            else
            {
                row = new TaskRow(t);
                _byId[t.Id] = row;
                _rows.Insert(i, row);
            }
        }
        EmptyState.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionText();
    }

    private void UpdateHeader(QueueRevision r)
    {
        var s = r.Stats;
        if (ChkScheduler.IsChecked != !r.Paused) ChkScheduler.IsChecked = !r.Paused;
        ChkScheduler.Content = r.Paused ? "Scheduler in pausa" : "Scheduler attivo";
        Fx.CountTo(StatWait, s.Waiting);
        Fx.CountTo(StatRun, s.Running);
        Fx.CountTo(StatErr, s.Retry);
        Fx.CountTo(StatPause, s.Paused);
        Fx.CountTo(StatDone, s.Done);
        Fx.Pulse(RunDot, s.Running > 0);
        TxtPercent.Text = s.Total == 0 ? "" : $"{100 * s.Done / s.Total}%";
        var frac = s.Total == 0 ? 0 : (double)s.Done / s.Total;
        Ring.AnimateTo(frac);
        TxtRingPct.Text = $"{Math.Round(frac * 100)}%";
        TxtRingCount.Text = s.Total == 0 ? "Coda vuota" : $"{s.Done} di {s.Total} completati";
        var left = s.Waiting + s.Running + s.Retry;
        var rate = QueueHistory.Throughput();
        TxtEta.Text = left == 0 ? (s.Total > 0 ? "Tutto concluso" : "Nessun task da eseguire")
            : rate > 0.05 ? $"{rate:0.#} al minuto · fine stimata fra {FormatEta(left / rate)}"
            : r.Paused ? "Scheduler in pausa" : $"{left} da eseguire · in attesa dei PC";
        TxtSub.Text = s.Total == 0 ? "La coda e' vuota."
            : $"{s.Total} task in coda · {AppHost.Scheduler.RunningCount}/{AppHost.Config.TaskMaxParallel} in parallelo · PC spenti ricontrollati ogni {AppHost.Config.TaskCheckMinutes} min, senza scadenza" + (s.Expired > 0 ? $" · {s.Expired} scaduti" : "");
    }

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string t) _filter = int.Parse(t);
        foreach (var tile in new[] { TileWait, TileRun, TileErr, TilePause, TileDone })
            if (tile is not null) tile.Tag = tile.CommandParameter as string == _filter.ToString() ? "Active" : null;
        if (!IsLoaded) return;
        ApplyFilter();
        if (sender is RadioButton) Fx.Cascade(LstTasks); // cambio filtro: la lista si ricompone in cascata
    }

    private static string FormatEta(double minutes) =>
        minutes < 1 ? "meno di un minuto" : minutes < 60 ? $"{Math.Ceiling(minutes)} min" : $"{(int)(minutes / 60)} h {(int)(minutes % 60)} min";

    private void OnRange(object sender, RoutedEventArgs e)
    {
        if (Chart is not null && sender is RadioButton { Tag: string t }) Chart.WindowMinutes = double.Parse(t);
    }

    private void OnTile(object sender, RoutedEventArgs e)
    {
        var idx = ((Button)sender).CommandParameter as string;
        var target = idx == _filter.ToString() ? "0" : idx; // secondo clic: torna a "Tutti"
        foreach (var rb in FilterBar.Children.OfType<RadioButton>())
            if (rb.Tag as string == target) rb.IsChecked = true;
    }

    // ---------------------------------------------------------------- selezione
    private List<TaskRow> Selected => LstTasks.SelectedItems.Cast<TaskRow>().ToList();
    private List<string> SelectedIds => Selected.Select(r => r.Id).ToList();

    private void OnSelection(object sender, SelectionChangedEventArgs e) => UpdateSelectionText();

    private void UpdateSelectionText()
    {
        var n = LstTasks.SelectedItems.Count;
        TxtSelection.Text = n == 1 ? "1 selezionato" : $"{n} selezionati";
        Fx.SlideVisible(SelectionBar, n > 0);
    }

    private bool NeedSelection()
    {
        if (LstTasks.SelectedItems.Count > 0) return true;
        DuckDialog.Info(Owner, "Nessuna selezione", "Seleziona uno o piu' task nella lista (Ctrl/Maiusc per la selezione multipla).");
        return false;
    }

    // ---------------------------------------------------------------- azioni
    private void OnToggleScheduler(object sender, RoutedEventArgs e)
    {
        AppHost.Scheduler.SetPaused(ChkScheduler.IsChecked != true);
        Refresh(true);
    }

    private void OnCheckAll(object sender, RoutedEventArgs e)
    {
        var n = AppHost.Core.CheckNow();
        AppHost.Core.Log("TASK", $"Verifica immediata richiesta per {n} task");
        Refresh(true);
    }

    private void OnCheckSelected(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection()) return;
        var n = AppHost.Core.CheckNow(SelectedIds);
        AppHost.Core.Log("TASK", $"Verifica immediata di {n} task");
        Refresh(true);
    }

    private void OnPauseSelected(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection()) return;
        AppHost.Core.Pause(SelectedIds);
        Refresh(true);
    }

    private void OnResumeSelected(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection()) return;
        AppHost.Core.Resume(SelectedIds);
        Refresh(true);
    }

    private void OnCancelRun(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection()) return;
        var running = Selected.Where(r => r.Info.Status == TaskStatus.Running).ToList();
        if (running.Count == 0) { DuckDialog.Info(Owner, "Nessun task in esecuzione", "I task selezionati non sono in esecuzione."); return; }
        if (!DuckDialog.Confirm(Owner, "Interrompere?", $"Interrompere {running.Count} task in esecuzione? Verranno ritentati piu' tardi.", "Interrompi", danger: true)) return;
        foreach (var r in running) AppHost.Scheduler.Cancel(r.Id);
    }

    private void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection()) return;
        var ids = SelectedIds;
        var running = Selected.Count(r => r.Info.Status == TaskStatus.Running);
        var msg = $"Eliminare {ids.Count} task dalla coda?" + (running > 0 ? $"\n{running} sono in esecuzione e non verranno eliminati." : "");
        if (!DuckDialog.Confirm(Owner, "Elimina task", msg, "Elimina", danger: true)) return;
        var n = AppHost.Core.RemoveTasks(ids);
        AppHost.Core.Log("TASK", $"Eliminati {n} task");
        Refresh(true);
    }

    private void OnClearMenu(object sender, RoutedEventArgs e)
    {
        BtnClear.ContextMenu.PlacementTarget = BtnClear;
        BtnClear.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        BtnClear.ContextMenu.IsOpen = true;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        var mode = (string)((MenuItem)sender).Tag;
        var label = mode switch
        {
            "done" => "i task completati",
            "expired" => "i task scaduti",
            "errors" => "i task in errore",
            "duplicates" => "i task duplicati (stesso PC, stessa operazione: resta il primo)",
            _ => "TUTTA la coda (i task in esecuzione restano)",
        };
        if (!DuckDialog.Confirm(Owner, "Svuota coda", $"Eliminare {label}?", "Elimina", danger: mode == "all")) return;
        var n = AppHost.Core.ClearTasks(mode);
        AppHost.Core.Log("TASK", $"Svuotamento coda ({mode}): {n} task eliminati");
        Refresh(true);
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection()) return;
        var row = Selected[0];
        if (row.Info.Status == TaskStatus.Running) { DuckDialog.Info(Owner, "Task in esecuzione", "Attendere la fine dell'esecuzione per modificarlo."); return; }
        if (new TaskEditDialog(Owner, AppHost.Core.GetTask(row.Id)).ShowDialog() == true) Refresh(true);
    }

    private void OnShowLog(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection()) return;
        foreach (var r in Selected.Take(5)) TaskLogWindow.Open(Owner, r.Id);
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TaskRow row) TaskLogWindow.Open(Owner, row.Id);
    }
}
