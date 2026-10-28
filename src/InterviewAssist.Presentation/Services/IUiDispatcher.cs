namespace InterviewAssist.Presentation.Services;

/// <summary>The UI thread of whichever UI framework hosts the view models (MAUI, Avalonia, or a test fake).</summary>
public interface IUiDispatcher
{
    bool IsUiThread { get; }

    /// <summary>Queues <paramref name="action"/> on the UI thread. Always asynchronous, even when called on the UI thread.</summary>
    void Post(Action action);

    /// <summary>Queues <paramref name="action"/> on the UI thread after <paramref name="delay"/>.</summary>
    void PostDelayed(TimeSpan delay, Action action);

    /// <summary>Runs <paramref name="action"/> on the UI thread (inline when already on it) and completes when it has run.</summary>
    Task InvokeAsync(Action action);

    /// <summary>A stopped timer whose <see cref="IUiTimer.Tick"/> is raised on the UI thread.</summary>
    IUiTimer CreateTimer();
}

public interface IUiTimer
{
    TimeSpan Interval { get; set; }

    /// <summary>True (default) to keep ticking; false to tick once per <see cref="Start"/>.</summary>
    bool IsRepeating { get; set; }

    bool IsRunning { get; }

    /// <summary>Raised on the UI thread.</summary>
    event EventHandler? Tick;

    /// <summary>Starts, or restarts the interval if already running.</summary>
    void Start();

    void Stop();
}
