using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Abstractions;

public interface ISettingsService
{
    /// <summary>The single live settings instance. Never replaced, so it is safe to bind to and subscribe to.</summary>
    AppSettings Settings { get; }

    /// <summary>
    /// The implementation's constructor loads settings.json synchronously (so theme and window bounds are ready
    /// before the first window). LoadAsync then loads the API keys from secure storage into <see cref="Settings"/>.
    /// Call once at startup; UI bound to the keys updates when they arrive.
    /// </summary>
    Task LoadAsync();

    /// <summary>Persists immediately. Changes are also auto-saved shortly after any property changes.</summary>
    Task SaveAsync();
}
