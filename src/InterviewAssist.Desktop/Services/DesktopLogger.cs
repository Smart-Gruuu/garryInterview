using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace InterviewAssist.Desktop.Services;

/// <summary>
/// Minimal <see cref="ILogger{TCategoryName}"/> (registered as an open generic) that writes to stderr, where a desktop
/// launcher or a terminal shows it, and to the debugger output in Debug builds.
/// </summary>
public sealed class DesktopLogger<T> : ILogger<T>
{
    private static readonly string s_category = typeof(T).Name;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= DesktopLog.MinimumLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        ArgumentNullException.ThrowIfNull(formatter);
        DesktopLog.Write(logLevel, s_category, formatter(state, exception), exception);
    }
}

/// <summary>The shared sink behind <see cref="DesktopLogger{T}"/>; also used before the container exists.</summary>
public static class DesktopLog
{
    private static readonly object s_sync = new();

#if DEBUG
    public const LogLevel MinimumLevel = LogLevel.Debug;
#else
    public const LogLevel MinimumLevel = LogLevel.Information;
#endif

    public static void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {Abbreviate(level)} {category}: {message}";
        if (exception is not null)
            line += Environment.NewLine + exception;

        Debug.WriteLine(line);
        try
        {
            lock (s_sync)
                Console.Error.WriteLine(line);
        }
        catch (IOException)
        {
            // No usable stderr (e.g. closed by the launcher): logging must never take the app down.
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };
}
