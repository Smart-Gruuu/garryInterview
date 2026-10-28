using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Presentation.Tests.Fakes;

/// <summary>
/// A deterministic UI thread. Posted work runs only when the test calls <see cref="Drain"/> or <see cref="Advance"/>,
/// and time (delayed posts and timers) moves only through <see cref="Advance"/>. <see cref="InvokeAsync"/> runs inline.
/// </summary>
public sealed class ManualUiDispatcher : IUiDispatcher
{
    private readonly object _gate = new();
    private readonly Queue<Action> _queue = new();
    private readonly List<DelayedAction> _delayed = [];
    private readonly List<ManualUiTimer> _timers = [];

    public bool IsUiThread { get; set; } = true;

    /// <summary>The manual clock, starting at zero.</summary>
    public TimeSpan Now { get; private set; }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            _queue.Enqueue(action);
        }
    }

    public void PostDelayed(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            var dueAt = Now + (delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
            _delayed.Add(new DelayedAction(dueAt, action));
        }
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    public IUiTimer CreateTimer()
    {
        var timer = new ManualUiTimer(this);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Runs the posted work, including work posted while draining. Does not move the clock.</summary>
    public void Drain()
    {
        while (true)
        {
            Action? action;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out action))
                {
                    return;
                }
            }

            action();
        }
    }

    /// <summary>
    /// Moves the clock forward by <paramref name="duration"/>, running delayed posts and timer ticks in the order they
    /// fall due and draining posted work after each one.
    /// </summary>
    public void Advance(TimeSpan duration)
    {
        Drain();
        var target = Now + duration;
        while (TryTakeNextDue(target, out var dueAt, out var run))
        {
            Now = dueAt;
            run();
            Drain();
        }

        Now = target;
    }

    private bool TryTakeNextDue(TimeSpan target, out TimeSpan dueAt, out Action run)
    {
        lock (_gate)
        {
            DelayedAction? delayed = null;
            foreach (var candidate in _delayed)
            {
                if (candidate.DueAt <= target && (delayed is null || candidate.DueAt < delayed.DueAt))
                {
                    delayed = candidate;
                }
            }

            ManualUiTimer? timer = null;
            foreach (var candidate in _timers)
            {
                if (candidate.IsRunning && candidate.DueAt <= target && (timer is null || candidate.DueAt < timer.DueAt))
                {
                    timer = candidate;
                }
            }

            if (timer is not null && (delayed is null || timer.DueAt < delayed.DueAt))
            {
                dueAt = timer.DueAt;
                run = timer.Fire;
                return true;
            }

            if (delayed is not null)
            {
                _delayed.Remove(delayed);
                dueAt = delayed.DueAt;
                run = delayed.Action;
                return true;
            }

            dueAt = default;
            run = () => { };
            return false;
        }
    }

    // Ties run in the order they were posted: the earliest entry wins the strict "<" comparisons.
    private sealed record DelayedAction(TimeSpan DueAt, Action Action);

    /// <summary>A timer on the manual clock of its <see cref="ManualUiDispatcher"/>.</summary>
    public sealed class ManualUiTimer(ManualUiDispatcher owner) : IUiTimer
    {
        public TimeSpan Interval { get; set; }

        public bool IsRepeating { get; set; } = true;

        public bool IsRunning { get; private set; }

        public int TickCount { get; private set; }

        internal TimeSpan DueAt { get; private set; }

        public event EventHandler? Tick;

        public void Start()
        {
            // A repeating zero interval would make Advance spin forever.
            if (IsRepeating && Interval <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("A repeating timer needs a positive interval.");
            }

            IsRunning = true;
            DueAt = owner.Now + Interval;
        }

        public void Stop() => IsRunning = false;

        internal void Fire()
        {
            if (IsRepeating)
            {
                DueAt += Interval;
            }
            else
            {
                IsRunning = false;
            }

            TickCount++;
            Tick?.Invoke(this, EventArgs.Empty);
        }
    }
}
