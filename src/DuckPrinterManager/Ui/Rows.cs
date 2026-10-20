using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using DuckPrinterManager.Core.Models;
using TaskStatus = DuckPrinterManager.Core.Models.TaskStatus;

namespace DuckPrinterManager.Ui;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public static class Fmt
{
    public static string When(long unix)
    {
        if (unix <= 0) return "";
        var t = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        return t.Date == DateTime.Today ? t.ToString("HH:mm:ss") : t.ToString("dd/MM HH:mm");
    }

    public static string Relative(long unix)
    {
        if (unix <= 0) return "";
        var d = DateTimeOffset.FromUnixTimeSeconds(unix) - DateTimeOffset.UtcNow;
        if (d.TotalSeconds <= 5) return "ora";
        if (d.TotalMinutes < 1) return $"tra {(int)d.TotalSeconds} s";
        if (d.TotalHours < 1) return $"tra {(int)Math.Ceiling(d.TotalMinutes)} min";
        if (d.TotalDays < 1) return $"tra {(int)d.TotalHours} h {d.Minutes} min";
        return When(unix);
    }

    /// <summary>Stile chip + colore testo per uno stato di task (tinta, mai riempimento pieno).</summary>
    public static (string Chip, string Fg) StatusVisual(string status) => status switch
    {
        TaskStatus.Waiting => ("ChipOrange", "Orange"),
        TaskStatus.Running => ("ChipBlue", "Blue"),
        TaskStatus.Retry => ("ChipRed", "Red"),
        TaskStatus.Paused => ("ChipPurple", "Purple"),
        TaskStatus.Done => ("ChipGreen", "Green"),
        _ => ("Chip", "LabelTertiary"),
    };

    public static string LevelColor(string level) => level switch
    {
        "ERR" => "Red",
        "WARN" => "Orange",
        "TASK" => "Blue",
        "DEBUG" => "LabelTertiary",
        _ => "LabelSecondary",
    };
}

/// <summary>Riga della griglia task: aggiornata sul posto, cosi' la selezione non salta.</summary>
public sealed class TaskRow : Observable
{
    public TaskInfo Info { get; private set; }
    public TaskRow(TaskInfo info) => Info = info;

    public void Update(TaskInfo info) { Info = info; RaiseAll(); }
    public void Tick() => Raise(nameof(NextText));

    public string Id => Info.Id;
    public string Host => Info.Host;
    public string ActionText => Info.Action == TaskActions.Deploy ? (Info.GetPayload().DriverOnly ? "Solo driver" : "Installa") : "Rimuovi";
    public string ActionGlyph => Info.Action == TaskActions.Deploy ? "" : "";
    public string Label => Info.Label;
    public string StatusText => TaskStatus.Display(Info.Status);
    public Style ChipStyle => (Style)Application.Current.FindResource(Fmt.StatusVisual(Info.Status).Chip);
    public string StatusFgKey => Fmt.StatusVisual(Info.Status).Fg;
    public string Counters => $"{Info.Attempts} / {Info.Errors} / {Info.OfflineHits}";
    public string NextText => Info.Status switch
    {
        TaskStatus.Done => "completato " + Fmt.When(Info.CompletedAt),
        TaskStatus.Running => "in corso dalle " + Fmt.When(Info.LastCheck),
        TaskStatus.Paused => "in pausa",
        TaskStatus.Expired => "scaduto",
        _ => Fmt.Relative(Info.NextCheck),
    };
    public string LastText => string.IsNullOrEmpty(Info.LastError) ? Info.LastMessage : Info.LastError;
    public string Created => Fmt.When(Info.Created);
}

public sealed class LogRow(LogEntry e)
{
    public LogEntry Entry { get; } = e;
    public string Time => Entry.Time.ToString("dd/MM HH:mm:ss");
    public string Level => Entry.Level;
    public string Task => Entry.Task ?? "";
    public string Msg => Entry.Msg;
    public string FgKey => Fmt.LevelColor(Entry.Level);
    public override string ToString() => Entry.ToString();
}

public static class Busy
{
    /// <summary>Disabilita il bottone e ne cambia il testo finche' il lavoro non e' finito.</summary>
    public static async Task Run(System.Windows.Controls.Button button, string busyText, Func<Task> work)
    {
        var old = button.Content;
        button.IsEnabled = false;
        button.Content = busyText;
        try { await work(); }
        finally
        {
            button.Content = old;
            button.IsEnabled = true;
        }
    }
}
