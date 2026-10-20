using System.Windows;
using System.Windows.Input;
using DuckPrinterManager.Core.Models;
using DuckPrinterManager.Core.Native;
using DuckPrinterManager.Core.Services;
using DuckPrinterManager.Theme;
using TaskStatus = DuckPrinterManager.Core.Models.TaskStatus;

namespace DuckPrinterManager.Dialogs;

public partial class TaskEditDialog : Window
{
    private readonly TaskInfo _task;
    private readonly TaskPayload _p;

    public TaskEditDialog(Window owner, TaskInfo task)
    {
        InitializeComponent();
        Owner = owner;
        _task = task;
        _p = task.GetPayload();
        var deploy = task.Action == TaskActions.Deploy;
        TitleText.Text = $"Modifica task {task.Id}";
        SubText.Text = deploy ? $"Installazione · driver {_p.DriverName}" : "Disinstallazione";
        DeployPanel.Visibility = deploy ? Visibility.Visible : Visibility.Collapsed;
        RemovePanel.Visibility = deploy ? Visibility.Collapsed : Visibility.Visible;

        TxtHost.Text = task.Host;
        TxtPrinter.Text = _p.PrinterName;
        TxtAddress.Text = _p.PrinterAddress;
        CmbDefault.ItemsSource = DefaultModes.All.Select(m => new { m.Key, m.Label }).ToList();
        CmbDefault.SelectedValue = _p.DefaultMode;
        ChkDriverOnly.IsChecked = _p.DriverOnly;
        TxtTargets.Text = string.Join(", ", _p.RemoveTargets);
        ChkRemoveAll.IsChecked = _p.RemoveAllNetwork;
        ChkRemovePorts.IsChecked = _p.RemovePorts;
        ChkSetPrt.IsChecked = _p.RestartSetPrt;
        if (task.Status is TaskStatus.Done or TaskStatus.Expired) ChkReopen.Visibility = Visibility.Visible;

        Loaded += (_, _) => Motion.OpenDialog(Root);
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var host = DuckCore.ParseHosts(TxtHost.Text);
        if (host.Valid.Count != 1) { ShowError("Indicare un solo PC valido"); return; }

        string label;
        if (_task.Action == TaskActions.Deploy)
        {
            _p.DriverOnly = ChkDriverOnly.IsChecked == true;
            if (!_p.DriverOnly)
            {
                var a = DuckCore.ParseHosts(TxtAddress.Text);
                if (a.Valid.Count != 1) { ShowError("Indirizzo stampante non valido"); return; }
                var name = Naming.RenderPrinterName(TxtPrinter.Text, _p.DriverName, a.Valid[0]);
                if (name.Length == 0) { ShowError("Nome stampante vuoto"); return; }
                _p.PrinterAddress = a.Valid[0];
                _p.PrinterName = name;
            }
            _p.DefaultMode = CmbDefault.SelectedValue as string ?? DefaultModes.Auto;
            label = _p.DriverOnly ? $"Solo driver {_p.DriverName}" : $"{_p.PrinterName} -> {_p.PrinterAddress}";
        }
        else
        {
            var t = DuckCore.ParseHosts(TxtTargets.Text);
            _p.RemoveAllNetwork = ChkRemoveAll.IsChecked == true;
            if (!_p.RemoveAllNetwork && t.Valid.Count == 0) { ShowError("Indicare almeno un indirizzo da rimuovere"); return; }
            _p.RemoveTargets = t.Valid;
            _p.RemovePorts = ChkRemovePorts.IsChecked == true;
            label = _p.RemoveAllNetwork ? "Rimuovi tutte le stampanti di rete" : "Rimuovi porte " + string.Join(", ", _p.RemoveTargets);
        }
        _p.RestartSetPrt = ChkSetPrt.IsChecked == true;

        var patch = new Dictionary<string, object?>
        {
            ["host"] = host.Valid[0],
            ["label"] = label,
            ["payload"] = _p.ToJson(),
            ["reopen"] = ChkReopen.IsChecked == true,
            ["dedupe_key"] = _task.Action == TaskActions.Deploy
                ? Naming.DedupeKey(_p.DriverName, _p.PrinterName, _p.PrinterAddress, _p.DriverOnly.ToString())
                : Naming.DedupeKey(_p.RemoveAllNetwork ? "*" : string.Join(",", _p.RemoveTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))),
        };
        if (ChkNow.IsChecked == true) patch["next_check"] = DuckCore.Now;
        try
        {
            AppHost.Core.EditTask(_task.Id, patch);
            Motion.CloseDialog(this, Root, true);
        }
        catch (DuckCoreException ex) { ShowError(ex.Message); }
    }

    private void ShowError(string msg)
    {
        ErrText.Text = msg;
        ErrBox.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Motion.CloseDialog(this, Root, false);
}
