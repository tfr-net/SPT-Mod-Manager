using System.Collections.ObjectModel;
using Avalonia.Threading;
using SptModManager.Core.Logging;

namespace SptModManager.App.Services;

public sealed record LogEntry(DateTime Time, ActivityLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    public bool IsError => Level == ActivityLevel.Error;

    public bool IsWarning => Level == ActivityLevel.Warning;

    public bool IsSuccess => Level == ActivityLevel.Success;
}

/// <summary>Activity log that the UI binds to. Safe to write from any thread.</summary>
public sealed class UiActivityLog : IActivityLog
{
    private const int MaxEntries = 500;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public event Action<LogEntry>? EntryAdded;

    public void Write(ActivityLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);

        void Add()
        {
            Entries.Add(entry);
            while (Entries.Count > MaxEntries)
            {
                Entries.RemoveAt(0);
            }

            EntryAdded?.Invoke(entry);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Add();
        }
        else
        {
            Dispatcher.UIThread.Post(Add);
        }
    }
}
