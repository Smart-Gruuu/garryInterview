using InterviewAssist.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace InterviewAssist.Services;

/// <summary>
/// API keys in <see cref="SecureStorage"/>. When secure storage throws (some unpackaged Windows setups), keys are
/// kept in <see cref="Preferences"/> under the same names instead; a key found only there moves into secure storage
/// once it works again.
/// </summary>
public sealed class MauiSecretStore(ILogger<MauiSecretStore> logger) : ISecretStore
{
    private int _fallbackLogged;
    private volatile bool _useFallbackStorage;

    public async Task<string?> GetAsync(string key)
    {
        if (!_useFallbackStorage)
        {
            try
            {
                var value = await SecureStorage.Default.GetAsync(key).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            catch (Exception ex)
            {
                SwitchToFallbackStorage(ex);
            }
        }

        string plain;
        try
        {
            plain = Preferences.Default.Get(key, "");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the API key '{Key}'", key);
            return null;
        }

        if (string.IsNullOrWhiteSpace(plain))
            return null;

        plain = plain.Trim();
        if (!_useFallbackStorage)
        {
            try
            {
                await SetAsync(key, plain).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not move the API key '{Key}' into secure storage", key);
            }
        }

        return plain;
    }

    public async Task SetAsync(string key, string value)
    {
        if (!_useFallbackStorage)
        {
            try
            {
                if (value.Length == 0)
                    SecureStorage.Default.Remove(key);
                else
                    await SecureStorage.Default.SetAsync(key, value).ConfigureAwait(false);

                RemovePlainTextCopy(key);
                return;
            }
            catch (Exception ex)
            {
                SwitchToFallbackStorage(ex);
            }
        }

        if (value.Length == 0)
            Preferences.Default.Remove(key);
        else
            Preferences.Default.Set(key, value);
    }

    private void RemovePlainTextCopy(string key)
    {
        try
        {
            if (Preferences.Default.ContainsKey(key))
                Preferences.Default.Remove(key);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not remove the plain-text copy of '{Key}'", key);
        }
    }

    private void SwitchToFallbackStorage(Exception ex)
    {
        _useFallbackStorage = true;
        if (Interlocked.Exchange(ref _fallbackLogged, 1) == 0)
            logger.LogWarning(ex, "Secure storage is unavailable; API keys are kept in app preferences instead.");
    }
}
