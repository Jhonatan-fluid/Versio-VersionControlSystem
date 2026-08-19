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
    private bool _isPaused;
    private int _heldCount;

    public LogViewModel(InMemoryLogSink sink)
    {
        _sink = sink;

        Filters.Changed += Rebuild;

        foreach (var entry in sink.Snapshot())
        {
            Append(entry);
        }

        Filters.UpdateCounts(sink.Snapshot());

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

    /// <summary>Which levels and categories are on show.</summary>
    public LogFilterViewModel Filters { get; } = new();

    public RelayCommand ClearCommand { get; }

    public RelayCommand TogglePauseCommand { get; }

    public RelayCommand ToggleExpandedCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Frozen: entries keep arriving and keep being buffered, but nothing reaches the view.
    /// </summary>
    /// <remarks>
    /// This is a stronger promise than the tooltip used to make. It said "pause auto-scroll",
    /// which describes a much weaker feature — the list would still change under the reader,
    /// which is precisely what somebody reaching for pause is trying to stop. Nothing is
    /// dropped: <see cref="HeldCount"/> is what is waiting, and unpausing delivers all of it.
    /// </remarks>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (SetProperty(ref _isPaused, value) && !value)
            {
                // Deliver immediately rather than waiting up to 100 ms for the next tick.
                // Un-pausing is a direct request to see what was held.
                Drain();
            }

            UpdateHeldCount();
        }
    }

    /// <summary>
    /// How many entries are waiting behind the pause.
    /// </summary>
    /// <remarks>
    /// Shown in the header because a paused log and an idle app look identical, and the
    /// difference matters most in the case somebody paused to read something and then walked
    /// away. A number that climbs is the clearest possible statement that this is a freeze
    /// and not a silence.
    /// </remarks>
    public int HeldCount
    {
        get => _heldCount;
        private set => SetProperty(ref _heldCount, value);
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
            UpdateHeldCount();
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

        // Counted from the sink rather than from this batch: the badges describe what the
        // buffer holds and could be filtered back into view, not what has just arrived.
        Filters.UpdateCounts(_sink.Snapshot());
        UpdateHeldCount();

        if (appended)
        {
            RowsAppended?.Invoke();
        }
    }

    private void UpdateHeldCount()
    {
        lock (_gate)
        {
            HeldCount = _incoming.Count;
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

    private bool Passes(LogEvent entry) => Filters.Passes(entry);

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
        Filters.Changed -= Rebuild;
    }
}
