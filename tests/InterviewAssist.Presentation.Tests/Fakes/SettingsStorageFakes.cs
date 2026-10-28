using System.Collections.Concurrent;
using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Presentation.Tests.Fakes;

/// <summary>A fresh, empty directory under %TEMP% that is deleted on dispose.</summary>
public sealed class TempSettingsLocation : ISettingsLocation, IDisposable
{
    public TempSettingsLocation()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "InterviewAssist.Presentation.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp folder must not fail a test.
        }
    }
}

/// <summary>
/// Thread-safe secret store. <see cref="GetGate"/> can hold reads open to simulate a slow store, and
/// <see cref="WaitUntilAsync"/> waits for the writes the settings service makes in the background.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, string> _values = new();
    private readonly object _gate = new();
    private TaskCompletionSource _changed = NewSignal();
    private int _setCount;

    /// <summary>When set, reads wait for this task before returning.</summary>
    public Task? GetGate { get; set; }

    public int SetCount => Volatile.Read(ref _setCount);

    public IReadOnlyDictionary<string, string> Values => _values;

    public void Seed(string key, string value) => _values[key] = value;

    public async Task<string?> GetAsync(string key)
    {
        if (GetGate is { } gate)
        {
            await gate.ConfigureAwait(false);
        }

        return _values.TryGetValue(key, out var value) ? value : null;
    }

    public Task SetAsync(string key, string value)
    {
        if (value.Length == 0)
        {
            _values.TryRemove(key, out _);
        }
        else
        {
            _values[key] = value;
        }

        Interlocked.Increment(ref _setCount);
        TaskCompletionSource changed;
        lock (_gate)
        {
            changed = _changed;
            _changed = NewSignal();
        }

        changed.SetResult();
        return Task.CompletedTask;
    }

    /// <summary>Completes once <paramref name="condition"/> holds; fails the test if it never does.</summary>
    public async Task WaitUntilAsync(Func<IReadOnlyDictionary<string, string>, bool> condition)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                changed = _changed.Task;
            }

            if (condition(_values))
            {
                return;
            }

            await changed.WaitAsync(WaitLimit).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
