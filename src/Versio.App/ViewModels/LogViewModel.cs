using System.Collections.ObjectModel;
using System.Windows.Threading;
using Versio.App.Mvvm;
using Versio.Core.Logging;

namespace Versio.App.ViewModels;

/// <summary>
/// One row in the log panel.
/// </summary>
/// <remarks>
/// The entry is settable so a heartbeat can update its row in place rather than appending a
/// new one every two seconds — 300 identical lines would bury the entries that matter.
/// </remarks>
public sealed class LogRow(LogEvent entry) : ObservableObject
{
    private LogEvent _entry = entry;

    public LogEvent Entry
    {
        get => _entry;
        set
        {
            _entry = value;
            OnPropertyChanged(nameof(Time));
            OnPropertyChanged(nameof(Level));
            OnPropertyChanged(nameof(Category));
            OnPropertyChanged(nameof(Message));
        }
    }

    public string Time => _entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    public string Level => _entry.Level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "???",
    };

    public string Category => _entry.Category;

    public string Message => _entry.OperationId is null
        ? _entry.Message
        : $"{{{_entry.OperationId}}} {_entry.Message}";

    public string LevelBrushKey => _entry.Level switch
    {
        LogLevel.Warning => "Brush.Status.Modified",
        LogLevel.Error => "Brush.Status.Deleted",
        LogLevel.Debug => "Brush.Text.Disabled",
        _ => "Brush.Status.Neutral",
    };
}

/// <summary>
/// The collapsible log console.
/// </summary>
/// <remarks>
/// <para>
/// Entries are <b>batched onto the dispatcher every 100 ms</b> rather than marshalled one
/// at a time. Without that, a chatty export floods the dispatcher queue and freezes the
/// window — the logger becomes the thing that breaks the UI it exists to explain.
/// </para>
/// <para>
/// Heartbeat lines collapse into a single updating row. Every beat still reaches the log
/// file, but a ten-minute operation would otherwise push 300 identical lines through the
/// view and bury everything that matters.
/// </para>
/// </remarks>
public sealed class LogViewModel : ObservableObject, IDisposable
{
    private const int MaxRows = 5000;

    private readonly InMemoryLogSink _sink;
    private readonly DispatcherTimer _pump;
    private readonly Queue<LogEvent> _incoming = new();
    private readonly Lock _gate = new();

    private LogRow? _heartbeatRow;
    // Collapsed at startup. The log is a diagnostic, not the main event: opening on a wall
    // of scrolling startup lines pushes the diff and the panels into the top half of the
    // window and reads as though something is wrong. The header, its count badge and the
    // chevron stay visible, so it is one click away and never hidden.
    private bool _isExpanded;
    private bool _autoScroll = true;
    private bool _isPaused;
    private LogLevel _minimumLevel = LogLevel.Debug;
    private string _filter = string.Empty;

    public LogViewModel(InMemoryLogSink sink)
    {
        _sink = sink;

        foreach (var entry in sink.Snapshot())
        {
            Append(entry);
        }

        sink.Emitted += OnEmitted;

        _pump = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _pump.Tick += (_, _) => Drain();
        _pump.Start();

        ClearCommand = new RelayCommand(() =>
        {
            Rows.Clear();
            _heartbeatRow = null;
        });

        TogglePauseCommand = new RelayCommand(() => IsPaused = !IsPaused);
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public ObservableCollection<LogRow> Rows { get; } = [];

    public RelayCommand ClearCommand { get; }

    public RelayCommand TogglePauseCommand { get; }

    public RelayCommand ToggleExpandedCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public bool IsPaused
    {
        get => _isPaused;
        set => SetProperty(ref _isPaused, value);
    }

    public LogLevel MinimumLevel
    {
        get => _minimumLevel;
        set
        {
            if (SetProperty(ref _minimumLevel, value))
            {
                Rebuild();
            }
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                Rebuild();
            }
        }
    }

    /// <summary>Raised after rows are appended so the view can scroll to the end.</summary>
    public event Action? RowsAppended;

    private void OnEmitted(LogEvent entry)
    {
        lock (_gate)
        {
            _incoming.Enqueue(entry);
        }
    }

    private void Drain()
    {
        if (IsPaused)
        {
            return;
        }

        List<LogEvent> batch;
        lock (_gate)
        {
            if (_incoming.Count == 0)
            {
                return;
            }

            batch = [.. _incoming];
            _incoming.Clear();
        }

        var appended = false;
        foreach (var entry in batch)
        {
            appended |= Append(entry);
        }

        while (Rows.Count > MaxRows)
        {
            Rows.RemoveAt(0);
        }

        if (appended)
        {
            RowsAppended?.Invoke();
        }
    }

    private bool Append(LogEvent entry)
    {
        if (!Passes(entry))
        {
            return false;
        }

        if (entry.IsHeartbeat)
        {
            // One row, updated in place. Every beat is still in the file.
            if (_heartbeatRow is not null && Rows.Contains(_heartbeatRow))
            {
                _heartbeatRow.Entry = entry;
                return false;
            }

            _heartbeatRow = new LogRow(entry);
            Rows.Add(_heartbeatRow);
            return true;
        }

        _heartbeatRow = null;
        Rows.Add(new LogRow(entry));
        return true;
    }

    private bool Passes(LogEvent entry) =>
        entry.Level >= _minimumLevel &&
        (_filter.Length == 0 ||
         entry.Message.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
         entry.Category.Contains(_filter, StringComparison.OrdinalIgnoreCase));

    private void Rebuild()
    {
        Rows.Clear();
        _heartbeatRow = null;
        foreach (var entry in _sink.Snapshot())
        {
            Append(entry);
        }
    }

    public void Dispose()
    {
        _pump.Stop();
        _sink.Emitted -= OnEmitted;
    }
}
