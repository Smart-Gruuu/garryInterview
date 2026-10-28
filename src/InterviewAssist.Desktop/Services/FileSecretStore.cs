using System.Runtime.Versioning;
using System.Text.Json;
using InterviewAssist.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace InterviewAssist.Desktop.Services;

/// <summary>
/// API keys in secrets.json next to settings.json. Desktop Linux has no secret service that is guaranteed to exist, so
/// the file is protected by permissions instead: owner-only (0600) in an owner-only directory (0700) on Unix.
/// Every write replaces the file atomically.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private const string FileName = "secrets.json";

    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerAll = OwnerReadWrite | UnixFileMode.UserExecute;
    private const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly string _directory;
    private readonly string _path;
    private readonly ILogger<FileSecretStore>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, string>? _values; // under _lock

    public FileSecretStore(ISettingsLocation location, ILogger<FileSecretStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        _directory = location.DirectoryPath;
        _path = Path.Combine(_directory, FileName);
        _logger = logger;
    }

    public async Task<string?> GetAsync(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var values = await LoadAsync().ConfigureAwait(false);
            return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsync(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        value ??= "";

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var values = await LoadAsync().ConfigureAwait(false);
            var updated = new Dictionary<string, string>(values, StringComparer.Ordinal);
            if (value.Length == 0)
            {
                if (!updated.Remove(key))
                    return;
            }
            else
            {
                if (updated.TryGetValue(key, out var current) && current == value)
                    return;
                updated[key] = value;
            }

            await WriteAsync(updated).ConfigureAwait(false);
            _values = updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The stored keys. A missing file is empty; an unreadable one throws (and is retried next time) so a later write
    /// can't replace keys that merely failed to load; a corrupt one is logged and treated as empty.
    /// </summary>
    private async Task<Dictionary<string, string>> LoadAsync()
    {
        if (_values is not null)
            return _values;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(_path))
        {
            if (!OperatingSystem.IsWindows())
                RestrictToOwner(_path, OwnerReadWrite);

            var bytes = await File.ReadAllBytesAsync(_path).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                            values[property.Name] = property.Value.GetString() ?? "";
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "{Path} is not valid JSON; the API keys in it are ignored", _path);
            }
        }

        _values = values;
        return values;
    }

    private async Task WriteAsync(Dictionary<string, string> values)
    {
        EnsureDirectory();

        // Deleted first so the temp file is always created fresh, with the owner-only mode applied at creation.
        var tempPath = _path + ".tmp";
        File.Delete(tempPath);

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerReadWrite;

        await using (var stream = new FileStream(tempPath, options))
        {
            await using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var (key, value) in values.OrderBy(p => p.Key, StringComparer.Ordinal))
                    writer.WriteString(key, value);
                writer.WriteEndObject();
            }

            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, _path, overwrite: true);
        if (!OperatingSystem.IsWindows())
            RestrictToOwner(_path, OwnerReadWrite);
    }

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_directory);
            return;
        }

        if (Directory.Exists(_directory))
            RestrictToOwner(_directory, OwnerAll);
        else
            Directory.CreateDirectory(_directory, OwnerAll);
    }

    [UnsupportedOSPlatform("windows")]
    private void RestrictToOwner(string path, UnixFileMode mode)
    {
        try
        {
            if ((File.GetUnixFileMode(path) & GroupOrOther) != 0)
                File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not restrict the permissions of {Path}", path);
        }
    }
}
