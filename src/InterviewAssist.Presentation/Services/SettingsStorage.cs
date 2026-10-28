namespace InterviewAssist.Presentation.Services;

/// <summary>Where settings.json lives (the directory is created on first save).</summary>
public interface ISettingsLocation
{
    string DirectoryPath { get; }
}

/// <summary>
/// Storage for the API keys, kept out of settings.json. MAUI: SecureStorage (with its own fallback);
/// desktop: an owner-only file. Implementations must be thread-safe and must not throw for "not found".
/// </summary>
public interface ISecretStore
{
    /// <summary>The stored value, or null when there is none.</summary>
    Task<string?> GetAsync(string key);

    /// <summary>Stores <paramref name="value"/>; an empty value removes the key.</summary>
    Task SetAsync(string key, string value);
}
