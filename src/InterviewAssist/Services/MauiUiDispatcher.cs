using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Services;

/// <summary><see cref="IUiDispatcher"/> over the MAUI dispatcher of the thread that creates it (the UI thread).</summary>
public sealed class MauiUiDispatcher : IUiDispatcher
{
    private readonly IDispatcher _dispatcher;

    public MauiUiDispatcher()
    {
        _dispatcher = Dispatcher.GetForCurrentThread()
            ?? Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("MauiUiDispatcher must be created on the UI thread.");
    }

    public bool IsUiThread => !_dispatcher.IsDispatchRequired;

    public void Post(Action action) => _dispatcher.Dispatch(action);

    public void PostDelayed(TimeSpan delay, Action action) => _dispatcher.DispatchDelayed(delay, action);

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!_dispatcher.IsDispatchRequired)
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

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcher.Dispatch(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        if (!queued)
            completion.TrySetException(new InvalidOperationException("The UI thread is no longer running."));

        return completion.Task;
    }

    public IUiTimer CreateTimer() => new MauiUiTimer(_dispatcher.CreateTimer());

    private sealed class MauiUiTimer : IUiTimer
    {
        private readonly IDispatcherTimer _timer;

        public MauiUiTimer(IDispatcherTimer timer)
        {
            _timer = timer;
            _timer.IsRepeating = true;
        }

        public TimeSpan Interval
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        public bool IsRepeating
        {
            get => _timer.IsRepeating;
            set => _timer.IsRepeating = value;
        }

        public bool IsRunning => _timer.IsRunning;

        public event EventHandler? Tick
        {
            add => _timer.Tick += value;
            remove => _timer.Tick -= value;
        }

        public void Start()
        {
            if (_timer.IsRunning)
                _timer.Stop();
            _timer.Start();
        }

        public void Stop() => _timer.Stop();
    }
}
