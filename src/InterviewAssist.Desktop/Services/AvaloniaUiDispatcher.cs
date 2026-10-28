using Avalonia.Threading;
using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Desktop.Services;

/// <summary><see cref="IUiDispatcher"/> over Avalonia's UI thread dispatcher.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    private static Dispatcher UiThread => Dispatcher.UIThread;

    public bool IsUiThread => UiThread.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        UiThread.Post(action);
    }

    public void PostDelayed(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Avalonia 12 timers belong to the dispatcher of the thread that creates them, so create it on the UI thread.
        UiThread.Post(() =>
        {
            var timer = new DispatcherTimer(delay, DispatcherPriority.Normal, UiThread);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
        });
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (UiThread.CheckAccess())
        {
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

        return UiThread.InvokeAsync(action).GetTask();
    }

    public IUiTimer CreateTimer() => new AvaloniaUiTimer();

    private sealed class AvaloniaUiTimer : IUiTimer
    {
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Normal, Dispatcher.UIThread);

        public AvaloniaUiTimer()
        {
            _timer.Tick += OnTick;
        }

        public event EventHandler? Tick;

        public TimeSpan Interval
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        public bool IsRepeating { get; set; } = true;

        public bool IsRunning => _timer.IsEnabled;

        public void Start()
        {
            _timer.Stop();
            _timer.Start();
        }

        public void Stop() => _timer.Stop();

        private void OnTick(object? sender, EventArgs e)
        {
            if (!IsRepeating)
                _timer.Stop();

            Tick?.Invoke(this, EventArgs.Empty);
        }
    }
}
