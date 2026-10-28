using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;
using InterviewAssist.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace InterviewAssist.Presentation.Settings;

/// <summary>
/// Owns the single <see cref="AppSettings"/> instance. The constructor loads settings.json (from
/// <see cref="ISettingsLocation"/>) synchronously, <see cref="LoadAsync"/> loads the API keys from the
/// <see cref="ISecretStore"/>, and every change is saved automatically: settings.json 600 ms after the last change,
/// API keys as soon as they change.
/// </summary>
/// <remarks>
/// The JSON is read and written with a property table built from <see cref="AppSettings"/>'s public
/// read/write properties rather than <c>AppJsonContext</c>: the System.Text.Json source generator cannot see
/// properties produced by the MVVM Toolkit generator, so that context serializes AppSettings as "{}".
/// The file format is what the context would produce (camelCase names, enums as strings, NaN as "NaN").
/// </remarks>
public sealed class SettingsService : ISettingsService
{
    private const string FileName = "settings.json";
    private const string DeepgramKeyName = "deepgram_api_key";
    private const string OpenAiKeyName = "openai_api_key";

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan FlushLockTimeout = TimeSpan.FromSeconds(2);

    private static readonly PersistedProperty[] s_properties = DiscoverProperties(typeof(AppSettings));

    private static readonly Dictionary<string, PersistedProperty> s_byJsonName =
        s_properties.ToDictionary(p => p.JsonName, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> s_propertyNames =
        s_properties.Select(p => p.Property.Name).ToHashSet(StringComparer.Ordinal);

    private readonly ISecretStore _secrets;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<SettingsService>? _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _saveLock = new(1, 1); // settings.json
    private readonly SemaphoreSlim _keyLock = new(1, 1); // secret store, so Flush never waits on its I/O
    private readonly Timer _saveTimer;
    private readonly KeySlot _deepgramKey = new(DeepgramKeyName, s => s.DeepgramApiKey);
    private readonly KeySlot _openAiKey = new(OpenAiKeyName, s => s.OpenAiApiKey);

    private int _jsonDirty;
    private bool _loading; // UI thread only

    public SettingsService(
        ISettingsLocation location,
        ISecretStore secrets,
        IUiDispatcher dispatcher,
        ILogger<SettingsService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger;
        _path = Path.Combine(location.DirectoryPath, FileName);
        _saveTimer = new Timer(OnSaveTimerElapsed);

        LoadFile();
        Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    public AppSettings Settings { get; } = new();

    public async Task LoadAsync()
    {
        string? deepgram, openAi;

        // Held while reading so a key typed meanwhile is written after anything the store does on read
        // (the MAUI store may move a key between storages), not before.
        await _keyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            deepgram = await ReadKeyAsync(DeepgramKeyName).ConfigureAwait(false);
            openAi = await ReadKeyAsync(OpenAiKeyName).ConfigureAwait(false);
        }
        finally
        {
            _keyLock.Release();
        }

        await _dispatcher.InvokeAsync(() =>
        {
            _loading = true;
            try
            {
                // A key the user typed while this was loading wins over the stored one.
                if (deepgram is not null && Settings.DeepgramApiKey.Length == 0)
                    Settings.DeepgramApiKey = deepgram;
                if (openAi is not null && Settings.OpenAiApiKey.Length == 0)
                    Settings.OpenAiApiKey = openAi;
            }
            finally
            {
                _loading = false;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Writes settings.json now (off the calling thread). Failures are logged, not thrown.</summary>
    public Task SaveAsync()
    {
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.Run(() => SaveJsonAsync(force: true));
    }

    /// <summary>
    /// Synchronously writes settings.json if a change has not been saved yet. For app shutdown, where a
    /// background save might never get to run. API keys are already written as soon as they change.
    /// </summary>
    public void Flush()
    {
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (Volatile.Read(ref _jsonDirty) == 0)
            return;

        if (!_saveLock.Wait(FlushLockTimeout))
        {
            _logger?.LogWarning("Settings were not flushed: a save is still running.");
            return;
        }

        try
        {
            if (Interlocked.Exchange(ref _jsonDirty, 0) == 1)
                WriteFile();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not save settings to {Path}", _path);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Sets every persisted setting back to its default except the properties named in <paramref name="keep"/>
    /// (C# property names). API keys are never persisted in settings.json, so they are never reset.
    /// Call on the UI thread; changes apply live and are auto-saved like any other edit.
    /// </summary>
    public static void ResetToDefaults(AppSettings settings, IReadOnlyCollection<string> keep)
    {
        var defaults = new AppSettings();
        foreach (var p in s_properties)
        {
            if (!keep.Contains(p.Property.Name))
                p.Property.SetValue(settings, p.Property.GetValue(defaults));
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading)
            return;

        switch (e.PropertyName)
        {
            case nameof(AppSettings.DeepgramApiKey):
                QueueKeyWrite(_deepgramKey);
                break;
            case nameof(AppSettings.OpenAiApiKey):
                QueueKeyWrite(_openAiKey);
                break;
            default:
                // Computed properties (HasDeepgramKey, HasProfile, ...) are not persisted.
                if (string.IsNullOrEmpty(e.PropertyName) || s_propertyNames.Contains(e.PropertyName))
                {
                    Interlocked.Exchange(ref _jsonDirty, 1);
                    _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
                }
                break;
        }
    }

    private void OnSaveTimerElapsed(object? state) => _ = SaveJsonAsync(force: false);

    // ---- settings.json ----

    private void LoadFile()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            using var document = JsonDocument.Parse(
                File.ReadAllText(_path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("The root element is not an object.");

            Settings.SettingsVersion = 0; // files written before versioning have no version field
            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (!s_byJsonName.TryGetValue(member.Name, out var property))
                    continue; // a setting from an older/newer version

                if (property.TryRead(member.Value, out var value))
                    property.Property.SetValue(Settings, value);
                else
                    _logger?.LogWarning("Ignoring invalid value for setting '{Name}' in {Path}", member.Name, _path);
            }

            MigrateLoadedSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Settings.SettingsVersion = AppSettings.CurrentVersion;
            _logger?.LogWarning(ex, "Could not read {Path}; using default settings", _path);
        }
    }

    /// <summary>
    /// Upgrades values saved by older versions. Runs before <see cref="OnSettingsPropertyChanged"/> is subscribed,
    /// so it schedules the save itself.
    /// </summary>
    private void MigrateLoadedSettings()
    {
        if (Settings.SettingsVersion >= AppSettings.CurrentVersion)
            return;

        // v2: answers default to conversational speech; "Hybrid" was the old default, not a choice.
        if (Settings.SettingsVersion < 2 && string.Equals(Settings.AnswerFormat, "Hybrid", StringComparison.OrdinalIgnoreCase))
            Settings.AnswerFormat = "Script";

        Settings.SettingsVersion = AppSettings.CurrentVersion;
        Interlocked.Exchange(ref _jsonDirty, 1);
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private async Task SaveJsonAsync(bool force)
    {
        try
        {
            await _saveLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Interlocked.Exchange(ref _jsonDirty, 0) == 0 && !force)
                    return;

                try
                {
                    WriteFile();
                }
                catch
                {
                    // Retry with the next change, save or flush.
                    Interlocked.Exchange(ref _jsonDirty, 1);
                    throw;
                }
            }
            finally
            {
                _saveLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not save settings to {Path}", _path);
        }
    }

    // Callers hold _saveLock.
    private void WriteFile()
    {
        // A local file, not HTML: keep "+" in hotkeys and non-ASCII resume text readable.
        var options = new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            writer.WriteStartObject();
            foreach (var property in s_properties)
                property.Write(writer, Settings);
            writer.WriteEndObject();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp";
        File.WriteAllBytes(tempPath, buffer.ToArray());
        File.Move(tempPath, _path, overwrite: true);
    }

    // ---- API keys ----

    private void QueueKeyWrite(KeySlot slot)
    {
        // While a write is queued it will pick up the newest value, so typing a key causes few writes.
        if (Interlocked.Exchange(ref slot.WriteQueued, 1) == 1)
            return;

        _ = Task.Run(() => WriteKeyAsync(slot));
    }

    private async Task WriteKeyAsync(KeySlot slot)
    {
        try
        {
            await _keyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                Volatile.Write(ref slot.WriteQueued, 0);
                var value = slot.Read(Settings).Trim();
                await _secrets.SetAsync(slot.StorageKey, value).ConfigureAwait(false);
            }
            finally
            {
                _keyLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not save the API key '{Key}'", slot.StorageKey);
        }
    }

    private async Task<string?> ReadKeyAsync(string storageKey)
    {
        try
        {
            var value = await _secrets.GetAsync(storageKey).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read the API key '{Key}'", storageKey);
            return null;
        }
    }

    // ---- property table ----

    private static PersistedProperty[] DiscoverProperties(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true }
                        && p.SetMethod is { IsPublic: true }
                        && p.GetIndexParameters().Length == 0
                        && p.GetCustomAttribute<JsonIgnoreAttribute>() is null
                        && PersistedProperty.IsSupported(p.PropertyType))
            .Select(p => new PersistedProperty(p))
            .ToArray();

    private sealed class KeySlot(string storageKey, Func<AppSettings, string> read)
    {
        public string StorageKey { get; } = storageKey;

        public Func<AppSettings, string> Read { get; } = read;

        // 1 while a write is queued but has not read the value yet (Interlocked).
        public int WriteQueued;
    }

    private sealed class PersistedProperty(PropertyInfo property)
    {
        public PropertyInfo Property { get; } = property;

        public string JsonName { get; } = JsonNamingPolicy.CamelCase.ConvertName(property.Name);

        public static bool IsSupported(Type type) =>
            type == typeof(string) || type == typeof(bool) || type == typeof(int) || type == typeof(double) || type.IsEnum;

        public void Write(Utf8JsonWriter writer, AppSettings settings)
        {
            switch (Property.GetValue(settings))
            {
                case string s:
                    writer.WriteString(JsonName, s);
                    break;
                case bool b:
                    writer.WriteBoolean(JsonName, b);
                    break;
                case int i:
                    writer.WriteNumber(JsonName, i);
                    break;
                case double d when double.IsFinite(d):
                    writer.WriteNumber(JsonName, d);
                    break;
                case double d:
                    // JSON has no NaN/Infinity: write the named literal as a string ("NaN", "Infinity").
                    writer.WriteString(JsonName, d.ToString(CultureInfo.InvariantCulture));
                    break;
                case Enum e:
                    writer.WriteString(JsonName, e.ToString());
                    break;
            }
        }

        public bool TryRead(JsonElement element, out object? value)
        {
            var type = Property.PropertyType;
            value = null;

            if (type == typeof(string))
            {
                if (element.ValueKind != JsonValueKind.String)
                    return false;
                value = element.GetString();
                return true;
            }

            if (type == typeof(bool))
            {
                if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return false;
                value = element.GetBoolean();
                return true;
            }

            if (type == typeof(int))
            {
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var i))
                    return false;
                value = i;
                return true;
            }

            if (type == typeof(double))
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var d))
                {
                    value = d;
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String
                    && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                {
                    value = d;
                    return true;
                }

                return false;
            }

            if (type.IsEnum)
            {
                if (element.ValueKind == JsonValueKind.String
                    && Enum.TryParse(type, element.GetString(), ignoreCase: true, out var parsed)
                    && parsed is not null
                    && Enum.IsDefined(type, parsed))
                {
                    value = parsed;
                    return true;
                }

                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
                {
                    var candidate = Enum.ToObject(type, number);
                    if (Enum.IsDefined(type, candidate))
                    {
                        value = candidate;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
