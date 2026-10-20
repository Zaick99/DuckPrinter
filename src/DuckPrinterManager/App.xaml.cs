using System.Windows;
using System.Windows.Threading;
using DuckPrinterManager.Dialogs;
using DuckPrinterManager.Theme;

namespace DuckPrinterManager;

public partial class App : Application
{
    private Mutex? _single;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Due istanze eseguirebbero due volte la stessa coda di task sugli stessi PC
        _single = new Mutex(true, @"Local\DuckPrinterManager.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("Duck Printer Manager e' gia' aperto.", "Duck Printer Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUiException;
        TaskScheduler.UnobservedTaskException += (_, a) => { AppHost.TryLog("ERR", "Task non osservato: " + a.Exception.GetBaseException().Message); a.SetObserved(); };

        try
        {
            AppHost.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Avvio non riuscito:\n\n" + ex.Message + "\n\nVerificare che duck_core.dll sia accanto all'eseguibile.",
                "Duck Printer Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        ThemeManager.Initialize(this, AppHost.Config.WindowTranslucent);
        Ui.Fx.RegisterPress();
        Ui.Fx.EnableHighFrameRate();
        var w = new MainWindow();
        MainWindow = w;
        w.Show();
        await AppHost.LoadRepartiAsync();
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppHost.TryLog("ERR", "Errore interfaccia: " + e.Exception);
        var ex = e.Exception.GetBaseException();
        // dove: il primo frame dell'applicazione nello stack (non quelli di WPF)
        var where = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("DuckPrinterManager")) ?? "";
        DuckDialog.Error(MainWindow, "Errore inatteso", $"{ex.Message}\n\n{ex.GetType().Name}{(where.Length > 0 ? "\n" + where : "")}\n\nDettagli completi nel log.");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppHost.Stop();
        _single?.Dispose();
        base.OnExit(e);
    }
}
