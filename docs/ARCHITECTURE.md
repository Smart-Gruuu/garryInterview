# Interview Assist — Architecture & Implementation Spec

A lightweight always-on-top helper for live job interviews. It captions the interviewer's voice in real time
(Deepgram), and when the user presses **Enter** it sends the interviewer's latest question — together with the
user's resume and the job description — to OpenAI and streams a ready-to-say answer.

Windows first; Android later from the same code base (.NET MAUI).

## Stack

| Piece | Choice |
|---|---|
| UI / app shell | .NET MAUI 9 (`net9.0-windows10.0.19041.0`; `net9.0-android` opt-in with `-p:EnableAndroid=true`) |
| MVVM | CommunityToolkit.Mvvm 8.4 (`[ObservableProperty]` on **fields**, `[RelayCommand]`) |
| Speech-to-text | Deepgram live streaming WebSocket (`wss://api.deepgram.com/v1/listen`), model `nova-3` |
| Answers | OpenAI Chat Completions, streaming SSE (`POST {baseUrl}/chat/completions`) |
| Windows audio | NAudio.Wasapi 2.2.1 — `WasapiLoopbackCapture` (system audio) and `WasapiCapture` (mic) |
| Android audio | `Android.Media.AudioRecord`, 16 kHz mono PCM16 |
| PDF import | PdfPig 0.1.16 |
| SDK | pinned by `global.json` to .NET SDK 9.0.314 |

## Solution layout

```
InterviewAssist.sln
global.json
src/InterviewAssist.Core/            net9.0 class library — NO MAUI references
  Models/            AppSettings, SettingOptions, AudioModels, TranscriptModels, ChatModels, QaItem   (contracts, done)
  Abstractions/      IAudioCaptureService, ITranscriptionClient, ITranscriptStore, IChatCompletionClient,
                     IPromptBuilder, IDocumentTextExtractor, ISettingsService                          (contracts, done)
  Serialization/     AppJsonContext (source-generated JSON for AppSettings)                             (done)
  Audio/             PcmConverter.cs
  Transcription/     DeepgramTranscriptionClient.cs, DeepgramMessageParser.cs, TranscriptStore.cs
  Chat/              OpenAiChatClient.cs, OpenAiStreamParser.cs, PromptBuilder.cs
  Documents/         DocumentTextExtractor.cs
src/InterviewAssist.Presentation/    net9.0 class library — NO MAUI/Avalonia references (see docs/DESKTOP-SPEC.md)
  Services/          UI service contracts: IUiDispatcher, INavigationService, IDialogService, IClipboardService,
                     IFilePickerService, ILauncherService, IAppEnvironment, IPlatformWindowService, IThemeService,
                     ISettingsLocation, ISecretStore
  ViewModels/        MainViewModel.cs, SetupViewModel.cs, SettingsViewModel.cs
  Settings/          SettingsService.cs
  Theming/           ThemePalettes.cs, FontSizeLimits.cs
  Markdown/          MarkdownParser.cs
src/InterviewAssist/                 MAUI app
  MauiProgram.cs, App.xaml(.cs), AppShell.xaml(.cs), GlobalXmlns.cs
  Services/          ThemeService.cs + MAUI adapters for the Presentation contracts (MauiUiDispatcher,
                     MauiNavigationService, MauiDialogService, MauiSettingsLocation, MauiSecretStore, ...)
  Views/             MainPage.xaml(.cs), SetupPage.xaml(.cs), SettingsPage.xaml(.cs)
  Controls/          MarkdownLabel.cs
  Converters/        (whatever the views need)
  Resources/Styles/  Colors.xaml, Styles.xaml
  Platforms/Windows/ WindowsAudioCaptureService.cs, WindowsWindowService.cs, NativeMethods.cs
  Platforms/Android/ AndroidAudioCaptureService.cs, AndroidWindowService.cs, AndroidManifest.xml
tests/InterviewAssist.Core.Tests/    xunit tests for Core
```

Namespaces follow folders: `InterviewAssist.Core.Audio`, `InterviewAssist.Core.Transcription`,
`InterviewAssist.Core.Chat`, `InterviewAssist.Core.Documents`, `InterviewAssist.Services`,
`InterviewAssist.ViewModels`, `InterviewAssist.Views`, `InterviewAssist.Controls`, `InterviewAssist.Converters`.
**Exception:** everything under `Platforms/Windows` and `Platforms/Android` uses namespace `InterviewAssist.Native`
(a namespace named `...Windows` would shadow the WinRT `Windows.*` namespaces, and `...Platform` would shadow
MAUI's `Platform` class). MAUI's single-project build compiles `Platforms/Windows/**` only for Windows and
`Platforms/Android/**` only for Android, so no `#if` is needed inside those files.

The template's root `MainPage.xaml(.cs)` is deleted; pages live in `Views/`.

## Data flow

```
 [IAudioCaptureService] --PCM16 16k mono chunks--> [ITranscriptionClient (Deepgram WS)]
                                                          | ResultReceived (interim/final)
                                                          v
                                                  [ITranscriptStore] --Changed--> MainViewModel (throttled) --> transcript view
 Enter / hotkey / Ask button --> MainViewModel.Ask:
      FinalizeAsync(350 ms) -> store.TakePendingQuestion() -> PromptBuilder.Build(...) -> IChatCompletionClient.StreamAsync
      -> deltas appended to QaItem.Answer (UI thread, throttled) --> MarkdownLabel renders the answer
```

## Dependency injection (MauiProgram)

All singletons:

| Service | Implementation |
|---|---|
| `IUiDispatcher`, `INavigationService`, `IClipboardService`, `IFilePickerService`, `IDialogService`, `ILauncherService`, `IAppEnvironment`, `ISettingsLocation`, `ISecretStore` | `InterviewAssist.Services.Maui*` adapters |
| `ISettingsService` | `InterviewAssist.Presentation.Settings.SettingsService` |
| `IThemeService` | `InterviewAssist.Services.ThemeService` (ctor: `ISettingsService`) |
| `ITranscriptStore` | `InterviewAssist.Core.Transcription.TranscriptStore` (parameterless ctor) |
| `ITranscriptionClient` | `InterviewAssist.Core.Transcription.DeepgramTranscriptionClient` (parameterless ctor) |
| `IChatCompletionClient` | `InterviewAssist.Core.Chat.OpenAiChatClient` (ctor `HttpClient? httpClient = null`; register with a factory `_ => new OpenAiChatClient()`) |
| `IPromptBuilder` | `InterviewAssist.Core.Chat.PromptBuilder` (parameterless) |
| `IDocumentTextExtractor` | `InterviewAssist.Core.Documents.DocumentTextExtractor` (parameterless) |
| `IAudioCaptureService` | Windows: `InterviewAssist.Native.WindowsAudioCaptureService`; Android: `InterviewAssist.Native.AndroidAudioCaptureService` (both parameterless) |
| `IPlatformWindowService` | Windows: `InterviewAssist.Native.WindowsWindowService`; Android: `InterviewAssist.Native.AndroidWindowService` (both ctor: `ISettingsService`; registered as themselves too, so `App` can call `Attach(Window)`) |
| `MainViewModel`, `SetupViewModel`, `SettingsViewModel` | singletons |
| `MainPage`, `SetupPage`, `SettingsPage` | singletons (ctor-inject their view model, set `BindingContext`) |

Platform registrations use `#if WINDOWS` / `#elif ANDROID` in `MauiProgram.cs` only.

## Startup sequence

1. `SettingsService` ctor synchronously reads `Path.Combine(FileSystem.AppDataDirectory, "settings.json")`
   with `AppJsonContext.Default.AppSettings` and copies values into its single `Settings` instance
   (ignore/log a corrupt file and keep defaults).
2. `App` ctor (`App(IServiceProvider services)` or injected services): `InitializeComponent()`, then
   `IThemeService.Initialize()`, then fire-and-forget `ISettingsService.LoadAsync()` (loads API keys; catch+log).
3. `App.CreateWindow` returns `new Window(new AppShell())` with `Title = "Interview Assist"`,
   `Width/Height` from settings (clamped ≥ 340×420), `X/Y` from settings when not NaN,
   `MinimumWidth = 340`, `MinimumHeight = 420`. It subscribes `window.HandlerChanged` → when `Handler != null`
   call `IPlatformWindowService.Attach(window)` once. On Windows it saves bounds back into settings on
   `SizeChanged` and `Destroying` (skip when minimized / non-finite).
4. `AppShell`: a `TabBar` with three tabs — **Live** (route `live`, MainPage), **Setup** (route `setup`,
   SetupPage), **Settings** (route `settings`, SettingsPage). Shell nav bar hidden (`Shell.NavBarIsVisible=False`)
   to save vertical space.

## Settings persistence (`SettingsService`, Presentation; storage via `MauiSettingsLocation` / `MauiSecretStore`)

- `settings.json` holds everything except the two API keys (`[JsonIgnore]`).
- API keys: `SecureStorage.Default` keys `"deepgram_api_key"` / `"openai_api_key"`. If SecureStorage throws
  (can happen for some unpackaged Windows setups), fall back to `Preferences.Default` with the same keys and
  log once. Loading must not trigger re-saving the same values (use a `_loading` flag).
- Auto-save: subscribe to `Settings.PropertyChanged`; debounce 600 ms, then write JSON to a temp file and
  `File.Move(temp, path, overwrite: true)`. When a key property changes, write that key to secure storage.
  Saves run off the UI thread; serialize concurrent saves with a `SemaphoreSlim`.
- The `Settings` instance is never replaced.

## Component specs

### PcmConverter (`Core/Audio/PcmConverter.cs`)

```csharp
public sealed class PcmConverter
{
    public PcmConverter(int inputSampleRate, int inputChannels);          // throws ArgumentOutOfRangeException on <= 0
    public int InputSampleRate { get; }
    public int InputChannels { get; }
    public byte[] ConvertFloat32(ReadOnlySpan<float> interleaved);        // → PCM16 LE mono 16 kHz
    public byte[] ConvertInt16(ReadOnlySpan<short> interleaved);
    public byte[] ConvertBytes(ReadOnlySpan<byte> data, AudioSampleFormat format); // helper for raw buffers
    public void Reset();
    public static float ComputeLevel(ReadOnlySpan<byte> pcm16);           // RMS 0..1
}
public enum AudioSampleFormat { Float32, Int16, Int24, Int32 }
```
- Down-mix: average all channels.
- Resample to 16 kHz with a stateful linear-interpolation resampler that keeps its fractional read position and
  the last input sample across calls (so chunk boundaries don't click or drift). When downsampling, apply a simple
  low-pass first (e.g. a moving average over `ceil(inRate/outRate)` samples, or a one-pole/biquad) to limit aliasing.
  Same rate → pass-through (still down-mixed/converted). Output length over many calls must track
  `totalInput * 16000 / inRate` within ±1 sample.
- Clamp to [-1, 1] before converting to Int16 (`(short)Math.Round(x * 32767)`).
- Tests: `tests/InterviewAssist.Core.Tests/PcmConverterTests.cs` (length tracking across odd chunk sizes for 44.1k
  and 48k stereo, sine wave frequency preserved roughly, clipping, level of silence = 0, pass-through at 16 kHz).

### WindowsAudioCaptureService (`Platforms/Windows/WindowsAudioCaptureService.cs`)

- `SupportsSystemAudio = true`.
- `GetDevices()`: `MMDeviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)` →
  `AudioSourceKind.SystemAudio`; `DataFlow.Capture` → `Microphone`. `IsDefault` from
  `GetDefaultAudioEndpoint(Render, Role.Multimedia)` / `(Capture, Role.Communications)` (fall back to
  `Role.Console`). `Id = device.ID`, `Name = device.FriendlyName`. Never throws (return what you have).
- `StartAsync(kind, deviceId)`: stop any running capture; resolve the device (by id, else default; if none →
  `InvalidOperationException("No audio output device found" / "No microphone found")`). SystemAudio →
  `new WasapiLoopbackCapture(device)`; Microphone → `new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50)`.
  Use the device mix format (`capture.WaveFormat`); detect sample format: IEEE float 32, PCM 16/24/32, including
  `WaveFormatExtensible` (check `SubFormat` against `AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT` / `MEDIASUBTYPE_PCM`).
  Create a `PcmConverter(rate, channels)`. `DataAvailable` → convert → raise `ChunkAvailable(pcm, level)` (skip empty).
  `RecordingStopped` with `e.Exception != null` → raise `Error("Audio capture stopped: ...")` and mark not capturing.
  Wrap COM failures in `InvalidOperationException` with a friendly message.
- **Silence fill (loopback only):** WASAPI loopback delivers no packets while nothing is playing. Run a
  `System.Threading.Timer` every 100 ms: if no real data arrived for ≥ 150 ms, emit zero-filled PCM for the gap
  (in 100 ms blocks, max 1 s per tick) so the downstream stream stays continuous and Deepgram's endpointing works.
  Stop filling as soon as real data resumes. Lock around converter/timestamps.
- `StopAsync()`: stop timer, `StopRecording()`, dispose capture and enumerator objects; idempotent.
- Capture objects are created on the calling (UI) thread; NAudio raises `DataAvailable` on its own thread.

### AndroidAudioCaptureService (`Platforms/Android/AndroidAudioCaptureService.cs`)

- `SupportsSystemAudio = false`. `GetDevices()` → one entry `new("default", "Microphone", Microphone, true)`.
- `StartAsync`: request `Permissions.Microphone` on the main thread (`MainThread.InvokeOnMainThreadAsync`);
  denied → `InvalidOperationException("Microphone permission is required")`. SystemAudio requested → use the mic
  anyway (the UI never offers it). `AudioRecord(AudioSource.VoiceRecognition, 16000, ChannelIn.Mono,
  Encoding.Pcm16bit, max(minBuffer, 3200*2))`; read 640-sample (40 ms) blocks on a background `Task` until
  cancelled; raise `ChunkAvailable` with the bytes as-is (already the target format). Read errors → `Error`.
- Add `<uses-permission android:name="android.permission.RECORD_AUDIO" />` to `Platforms/Android/AndroidManifest.xml`.

### DeepgramTranscriptionClient (`Core/Transcription/`)

- URL: `wss://api.deepgram.com/v1/listen?model={Model}&language={Language}&encoding=linear16&sample_rate=16000&channels=1`
  `&interim_results=true&smart_format=true&punctuate=true&endpointing={EndpointingMs}&utterance_end_ms=1000&vad_events=true`
  plus, for models starting with `nova-3`, one `&keyterm={Uri.EscapeDataString(term)}` per key term (max 50,
  skip blanks). Use `Uri.EscapeDataString` for every value.
- Auth header: `Authorization: Token {ApiKey}` via `ClientWebSocket.Options.SetRequestHeader`.
  Set `Options.CollectHttpResponseDetails = true` and `KeepAliveInterval = 20 s`. Connect timeout 10 s.
  On handshake failure map `ws.HttpStatusCode` 401/403 → `InvalidOperationException("Deepgram rejected the API key (401). Check it in Settings.")`,
  other → `"Could not connect to Deepgram: {message}"`.
- `StreamId` starts at 0 and increments on every successful connect (first stream = 1).
- **Send loop:** a bounded `Channel<ReadOnlyMemory<byte>>` (capacity ~600 chunks, `DropOldest`) fed by
  `SendAudio` (copy the memory — callers may reuse buffers). The loop coalesces queued chunks into frames of up to
  ~100 ms (3200 bytes) and sends them as binary messages. It waits while reconnecting (audio stays queued).
  If no audio has been sent for 4 s, send text `{"type":"KeepAlive"}`. Only one `SendAsync` may be in flight
  at a time (guard control messages — KeepAlive, Finalize, CloseStream — with the same `SemaphoreSlim`).
- **Receive loop:** accumulate message fragments until `EndOfMessage`; parse with `DeepgramMessageParser`:
  - `"type":"Results"` → text = `channel.alternatives[0].transcript`; `is_final`, `speech_final`,
    `from_finalize` (default false), `start`, `duration`. Raise `ResultReceived` only for non-empty trimmed text.
    Any result with `from_finalize == true` completes a pending `FinalizeAsync` (even if empty).
  - `"type":"UtteranceEnd"` → `UtteranceEnd`. `Metadata` / `SpeechStarted` → ignore.
  - `"type":"Error"` or anything with an `err_msg`/`description` → raise `Error` with the message.
- `DeepgramMessageParser` is an `internal static class` with a pure method
  `DeepgramMessage Parse(string json, int streamId)` (returns a small record: Kind {Results, UtteranceEnd, Metadata, SpeechStarted, Error, Unknown},
  `TranscriptResult? Result`, `bool FromFinalize`, `string? ErrorMessage`), tolerant of missing fields. Use `JsonDocument`.
- **FinalizeAsync(timeout):** if not Connected → false. Send `{"type":"Finalize"}`, await a
  `TaskCompletionSource` completed by the next `from_finalize` result, or the timeout → false.
- **Reconnect:** if the socket closes/faults while started and not stopping, State = Reconnecting and retry with
  backoff 0.5 s, 1 s, 2 s, 4 s, 8 s (5 tries). An auth failure (401/403) stops retrying. After giving up:
  State = Error, raise `Error("Lost connection to Deepgram: ...")`. A successful reconnect → Connected.
- **StopAsync:** mark stopping; stop accepting audio; send `{"type":"CloseStream"}`; wait up to 2 s for the
  server to close (keep raising results meanwhile); then close/abort and dispose; State = Disconnected.
  Idempotent. `DisposeAsync` = StopAsync.
- `TestApiKeyAsync`: `GET https://api.deepgram.com/v1/projects` with the same auth header (static HttpClient,
  10 s timeout). 200 → `(true, "Deepgram key works.")`; 401/403 → `(false, "Deepgram rejected this key.")`;
  else `(false, $"Deepgram returned {code}.")`; network error → `(false, "Network error: ...")`.
- Events are raised from background threads; never hold a lock while raising them.
- Tests: `DeepgramMessageParserTests.cs` (results interim/final/from_finalize, empty transcript, UtteranceEnd, error, garbage JSON).

### TranscriptStore (`Core/Transcription/TranscriptStore.cs`)

Implements `ITranscriptStore`; all state under one `lock`; `Changed` raised after releasing the lock.

- State: `List<TranscriptSegment> _segments`, current interim `TranscriptResult? _interim`,
  marker `(int StreamId, double Time)` initialised to `(0, 0)`.
- `Apply(r)`: final → append `TranscriptSegment(r.StreamId, r.Start, r.End, r.Text.Trim(), now)` and clear the
  interim if it belongs to the same stream and `interim.Start < r.End`; interim → replace `_interim`
  (an interim from an older stream than the current max is ignored). Ignore whitespace-only text.
- **Pending rule:** a segment/interim with `(streamId, start)` is *pending* iff
  `streamId > marker.StreamId || (streamId == marker.StreamId && start >= marker.Time - 0.05)`.
- `TakePendingQuestion()`: pending text = pending finals joined with spaces + pending interim text; then set the marker
  to `(latestStreamId, max(end of last final in latest stream, interim end))` where latestStreamId is the highest
  stream id seen. Return the joined, whitespace-normalised text.
- `GetSnapshot(maxHistoryChars)`: History = non-pending finals (plus a non-pending interim, if any) joined; keep only
  the last `maxHistoryChars` chars, cut at a word boundary, prefixed with "… " when cut. Pending = pending finals.
  Interim = pending interim text or "".
- `GetRecentContext(maxChars)`: tail of the non-pending finals, word-boundary cut, no ellipsis. "" when none.
- Keep at most 2000 segments (drop the oldest).
- `Clear()`: drop everything; keep the marker at the latest stream/time seen so old audio isn't re-counted.
- Tests: `TranscriptStoreTests.cs` — interim replaced by final; take includes interim; final arriving after take for
  the same audio is NOT pending; next question only contains new speech; new stream id after reconnect is pending;
  snapshot history/pending split; recent context; clear.

### OpenAiChatClient (`Core/Chat/`)

- One `HttpClient` (injected or created with `Timeout = Timeout.InfiniteTimeSpan`). URL = `BaseUrl.TrimEnd('/') + "/chat/completions"`.
  Header `Authorization: Bearer {ApiKey}`.
- Body (write with `Utf8JsonWriter`, no reflection): `model`, `messages` [{role, content}], `stream: true`,
  token limit: `max_completion_tokens` when the base URL host is `api.openai.com`, else `max_tokens`;
  `temperature` only when `request.Temperature` is not null; `reasoning_effort` only when non-empty.
- `SendAsync(..., HttpCompletionOption.ResponseHeadersRead, ct)` with a linked 30 s timeout that covers only
  reaching the response headers. Non-success → read body, extract `error.message` if JSON, throw
  `ChatClientException` with friendly text: 401 → "OpenAI rejected the API key (401). Check it in Settings.",
  404 → "Model '{model}' was not found or you don't have access (404).", 429 → "OpenAI rate limit or quota exceeded (429): {msg}",
  else "OpenAI error {code}: {msg}". Network failures → `ChatClientException("Could not reach OpenAI: ...")`.
  Cancellation via `ct` must surface as `OperationCanceledException`, not `ChatClientException`.
- Stream: read lines (`StreamReader.ReadLineAsync(ct)`); `OpenAiStreamParser.ParseLine(line)` (internal static, pure) →
  `(Kind {Delta, Done, Error, Ignore}, string? Text)`: `data: [DONE]` → Done; `data: {json}` →
  `choices[0].delta.content` (string, may be absent/null → Ignore) or `error.message` → Error; blank/comment/other → Ignore.
  Yield non-empty deltas; stop at Done or end of stream; Error → throw `ChatClientException`.
- `ListModelsAsync`: `GET {base}/models` → `data[].id` sorted ordinal-ignore-case; errors as above.
- `public static bool IsReasoningModel(string model)` in `PromptBuilder` or a shared static: true for ids starting with
  `o1`, `o3`, `o4`, `gpt-5` (case-insensitive), except ids containing `-chat`.
- Tests: `OpenAiStreamParserTests.cs`.

### PromptBuilder (`Core/Chat/PromptBuilder.cs`)

The system prompt is assembled in this order (later parts win where they differ):

1. **Persona** — the candidate's own style prompt: senior-level candidate, conversational American English, casual
   words/slang, short sentences, occasional "haha" / "you know", plain-language technical answers with no code, answer
   confidently as a senior when the resume doesn't cover it, light humor, under ~1 minute.
2. **How this works** — live speech-to-text may be imperfect; answer every question asked, in order; reply with only the
   words to say; use real resume examples when they fit; short reply to small talk. Then one line with the length rule
   (Short / Medium = under a minute / Detailed = up to ~2 min), the format rule (Script = spoken sentences (default) /
   Hybrid / Bullets) and an optional "Answer in {AnswerLanguage}."
3. **`## This interview: {type}`** — guidance for the selected `InterviewTypes` option (`Core/Models/InterviewTypes.cs`):
   who is asking, what they evaluate, tone (how casual / how much humor) and how to reason for that round. Live coding
   is the only type that allows code.
4. **`## Interview coaching notes`** — rules distilled from the candidate's interview-advice articles.
5. Profile sections, each only if non-empty and truncated to 12 000 chars: Role, Candidate resume, Job description,
   Candidate's notes, then **Extra instructions from the candidate** (override everything above).

Messages: system; the last `HistoryDepth` completed Q&A pairs (`user: "Interviewer: {Question}"` / `assistant: {Answer}`);
then the final user message (optional recent transcript + `"Interviewer's question: {question}"`).
`BuildRequest`: reasoning models (o1/o3/o4/gpt-5, not `-chat`) → no temperature, `reasoning_effort` (default "low"),
`MaxTokens + 2000`; others → temperature, `MaxTokens`. MaxAnswerTokens clamped to 100..4000. Tests: `PromptBuilderTests.cs`.

Settings versioning: `AppSettings.SettingsVersion` (current 2). `SettingsService` migrates older files once — v2 moves the
old default answer format "Hybrid" to "Script".

### DocumentTextExtractor (`Core/Documents/`)

- `.txt`, `.md`: read with BOM detection (UTF-8 default).
- `.docx`: `ZipArchive` → `word/document.xml` → `XmlReader`: text from `w:t`, `\t` for `w:tab`, newline for `w:br`/`w:cr`
  and at the end of each `w:p`. Missing entry → `InvalidDataException("This .docx file has no document body.")`.
- `.pdf`: PdfPig `PdfDocument.Open(bytes)`; per page use `ContentOrderTextExtractor.GetText(page)`
  (`UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor`), fall back to `page.Text`; pages separated by blank lines.
  No text at all → `InvalidDataException("No selectable text found — this PDF may be a scanned image. Paste the text instead.")`.
  Encrypted/corrupt → `InvalidDataException` with a friendly message.
- Normalise: `\r\n` → `\n`, trim trailing spaces per line, collapse 3+ newlines to 2, trim.
- Copy the input stream to memory first (MAUI file streams may not be seekable). Run CPU work on `Task.Run`.
- Tests: `DocumentTextExtractorTests.cs` (build a tiny .docx in-memory with ZipArchive; txt with BOM; unsupported ext).

### WindowsWindowService (`Platforms/Windows/WindowsWindowService.cs` + `NativeMethods.cs`)

- `Attach(window)`: `var nw = (Microsoft.UI.Xaml.Window)window.Handler!.PlatformView!;`
  `hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nw)`; `appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd))`.
  Apply all settings, subscribe to `Settings.PropertyChanged` (marshal to UI thread with `nw.DispatcherQueue` /
  `MainThread` if needed), hook keyboard, install the window subclass for hotkeys. Unhook/unregister on `nw.Closed`.
- **Always on top:** `(appWindow.Presenter as OverlappedPresenter)!.IsAlwaysOnTop = value`.
- **Opacity:** clamp 0.3..1.0. `< 1.0` → add `WS_EX_LAYERED` to `GWL_EXSTYLE` and
  `SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Round(opacity * 255), LWA_ALPHA)`; `== 1.0` → remove `WS_EX_LAYERED`.
  Use `GetWindowLongPtrW`/`SetWindowLongPtrW` on 64-bit and `GetWindowLongW`/`SetWindowLongW` on 32-bit.
- **Stealth:** on → `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE = 0x11)` (if it fails, try `WDA_MONITOR = 0x1`
  and raise `Warning("Stealth: this Windows version can only black out the window in captures")`), and
  `appWindow.IsShownInSwitchers = false` (hides from taskbar and Alt+Tab). Off → `WDA_NONE`, `IsShownInSwitchers = true`.
- **Global hotkeys** (`AskHotkey`, `ToggleWindowHotkey`): parse strings like `"Ctrl+Shift+Enter"`, `"Alt+Q"`, `"F9"`,
  `"None"` (modifiers Ctrl/Alt/Shift/Win; keys Enter, Space, Tab, Esc, F1–F24, A–Z, 0–9). Parse helper is a
  `public static bool TryParseHotkey(string text, out uint modifiers, out uint vk)`. `RegisterHotKey(hwnd, id,
  mods | MOD_NOREPEAT, vk)` with ids 0xA001 (ask) and 0xA002 (toggle); unregister before re-registering on change.
  Failure → `Warning($"Hotkey {text} is already used by another app. Pick another in Settings.")`.
  Receive `WM_HOTKEY (0x0312)` through `SetWindowSubclass` (comctl32) — keep the `SUBCLASSPROC` delegate in a field
  so it isn't collected; call `DefSubclassProc` for everything else. Ask → raise `CommandReceived(Ask)`;
  toggle → `ToggleVisibility()`.
- **Enter / Escape:** add a `PreviewKeyDown` handler (tunnelling, fires before the focused control) to the native
  window's root content (`nw.Content`; if null at Attach time, attach on the first `nw.Activated`). When
  `InterceptEnterKey` is true and the key is Enter or Escape, and the focused element
  (`FocusManager.GetFocusedElement(nw.Content.XamlRoot)`) is NOT a `TextBox`, `PasswordBox`, `RichEditBox`,
  `AutoSuggestBox` or an open `ComboBox`, set `e.Handled = true` and raise `CommandReceived(Ask|Cancel)`.
  Ignore key repeats (`e.KeyStatus.WasKeyDown`).
- **ToggleVisibility:** visible → `appWindow.Hide()`; hidden → `appWindow.Show()` + `SetForegroundWindow(hwnd)`.
- All P/Invoke in `NativeMethods` (`internal static partial class`, `[DllImport]` with `SetLastError = true` where useful).
- `Supports*` all true.

### AndroidWindowService (`Platforms/Android/AndroidWindowService.cs`)

- Only stealth is supported: `Platform.CurrentActivity?.Window` → `AddFlags(WindowManagerFlags.Secure)` /
  `ClearFlags(WindowManagerFlags.Secure)` on the main thread (blocks screenshots/recording). Apply on Attach and on
  `StealthMode` change. Everything else is a no-op; `Supports*` false except `SupportsStealth`. Events never fire.

### ThemeService (`Services/ThemeService.cs`)

Themes (`SettingOptions.Themes`): **Dark** (neutral charcoal, soft indigo accent), **Light**, **Midnight** (deep navy,
cyan accent), **Nord**, **Terminal** (near-black, green text/accent). Unknown name → Dark. Sets these app resources
(`Application.Current.Resources[key] = ...`) — every view uses them via `{DynamicResource key}`:

| Key | Type | Meaning |
|---|---|---|
| `AppBackgroundColor` | Color | page/window background |
| `SurfaceColor` | Color | cards |
| `SurfaceAltColor` | Color | inputs, chips, secondary buttons |
| `BorderColor` | Color | card/input borders |
| `TextColor` | Color | primary text |
| `SubtleTextColor` | Color | captions, placeholders, transcript history |
| `AccentColor` / `OnAccentColor` | Color | primary buttons, highlights / text on accent |
| `InterimTextColor` | Color | live (not final) transcript text |
| `QuestionTextColor` | Color | the question heading above an answer |
| `SuccessColor`, `WarningColor`, `DangerColor` | Color | status dot, errors |
| `CodeBackgroundColor`, `CodeTextColor` | Color | code in answers |
| `BaseFontSize` | double | Settings.FontSize (clamped 11..22) |
| `SmallFontSize` | double | Base − 2 |
| `LargeFontSize` | double | Base + 3 |
| `AnswerFontSize` | double | Base + 1 |

`Resources/Styles/Colors.xaml` defines the same keys with the Dark values so XAML resolves before the service runs.
On Android also set the status bar color if easy (optional).

### Styles (`Resources/Styles/Styles.xaml`)

Implicit styles use the keys above (ContentPage background; Label text color + `BaseFontSize`; Entry/Editor
background `SurfaceAltColor`, `TextColor`, placeholder `SubtleTextColor`; Button (= secondary look); Switch on-color
accent; Slider; Picker; Border; Shell colors incl. tab bar). Named styles (`x:Key`) other views may use:

| Key | TargetType | Look |
|---|---|---|
| `CardBorder` | Border | SurfaceColor fill, BorderColor stroke 1, RoundRectangle 10, Padding 10 |
| `SectionTitle` | Label | SmallFontSize, Bold, SubtleTextColor |
| `CaptionLabel` | Label | SmallFontSize, SubtleTextColor |
| `PrimaryButton` | Button | AccentColor bg, OnAccentColor text, CornerRadius 8 |
| `SecondaryButton` | Button | SurfaceAltColor bg, TextColor, CornerRadius 8 |
| `ChipButton` | Button | compact: SurfaceAltColor bg, SmallFontSize, Padding 10,4, CornerRadius 12, MinimumHeightRequest 28 |
| `DangerButton` | Button | DangerColor bg, white text |

Replace the template's purple styles entirely; keep it minimal. Remove template images that are no longer used
(`dotnet_bot.png`), keep fonts (OpenSans). Replace the app icon/splash SVG colors with the dark theme colors (simple
glyph is fine).

### MarkdownLabel (`Controls/MarkdownLabel.cs`)

`public class MarkdownLabel : Label` with bindable properties `Markdown` (string), `AccentColor`, `CodeBackgroundColor`,
`CodeTextColor` (Color; set from XAML with DynamicResource so theme changes re-render). Renders into
`FormattedText` spans; re-render is throttled to at most one per 60 ms while text streams in (always render the final
value). Supports: `**bold**`, `*italic*`/`_italic_`, `` `inline code` `` (monospace + code bg), fenced ``` code blocks
(monospace, code bg, kept verbatim), `#`/`##`/`###` headings (bold, slightly larger), `- `/`* ` bullets (render "•  "),
`1. ` numbered items, blank-line paragraphs. Unclosed markers during streaming render as plain text. Monospace font
family: `"Consolas"` on WinUI, `"monospace"` elsewhere. Must be fast for ~4 KB answers.

### MainViewModel + MainPage (Live tab)

MainViewModel ctor: `ISettingsService, IAudioCaptureService, ITranscriptionClient, ITranscriptStore,
IChatCompletionClient, IPromptBuilder, IPlatformWindowService`. Exposes `Settings` (the AppSettings instance).

State (observable): `IsListening`, `IsBusyStarting`, `StatusText`, `StatusKind` (enum Idle/Connecting/Listening/
Reconnecting/Error), `AudioLevel` (0..1, updated ≤ 10×/s), `ElapsedText` (mm:ss / h:mm:ss while listening),
`TranscriptHistory` (from `store.GetSnapshot()`), `QuestionDraft` (editable, two-way) and `LiveText` (via `QuestionDraft` in Core — see below), refreshed ≤ 10×/s on
`store.Changed` via `MainThread`), `HasPendingQuestion`, `TypedQuestion`, `ObservableCollection<QaItem> Answers`,
`CurrentItem`, `CurrentIndex`, `PositionText` ("2 / 5"), `IsGenerating`, `ToastText` (transient message, auto-clears after 3 s).

Commands:
- `ToggleListening`: Start → validate `Settings.HasDeepgramKey` (else StatusKind Error "Add your Deepgram API key in Settings");
  `transcription.StartAsync(new TranscriptionOptions(key, model, language, endpointing, keyTerms))` where key terms are
  `CustomVocabulary` split on `,`/`;`/newlines; then `audio.StartAsync(Settings.AudioSource, Settings.AudioDeviceId)`;
  if audio fails → stop transcription and show the error. Audio `ChunkAvailable` → `transcription.SendAudio` + level;
  `ResultReceived` → `store.Apply`. Stop → `audio.StopAsync()` then `transcription.StopAsync()`. Subscribe to the
  services' events once in the ctor; errors/state changes update status on the UI thread.
- `Ask` (Enter, global hotkey, Ask button): if listening and `Settings.FinalizeOnAsk` → `await transcription.FinalizeAsync(350 ms)`;
  `question = draft.Take(store.TakePending())` (the edited box + captions not in it yet + interim); empty → toast "Nothing new in the transcript yet". Else `StartAnswer(question, Transcript)`.
- `SubmitTyped` (Entry Completed / send button): non-empty `TypedQuestion` → `StartAnswer(text, Typed)` and clear the entry; empty → `Ask`.
- `StartAnswer`: missing OpenAI key → add an item with `ErrorMessage = "Add your OpenAI API key in Settings"`.
  Cancel any running generation. New `QaItem`, add to `Answers` (cap 100, drop oldest), make it current.
  `messages = promptBuilder.Build(Settings, question, Answers-before-this-one, store.GetRecentContext(1500))`;
  `request = promptBuilder.BuildRequest(Settings, messages)`; `await foreach` deltas → append to a `StringBuilder`;
  push to `item.Answer` on the UI thread at most every 50 ms and once at the end. `ChatClientException` →
  `item.ErrorMessage`; cancellation → keep partial answer. Always `IsStreaming=false` at the end.
- `Cancel` (Esc / Stop button), `Regenerate` (re-run the current item's question into the same item after clearing
  it), `CopyAnswer` (`Clipboard.Default.SetTextAsync`, toast "Copied"), `Previous`/`Next`, `ClearTranscript`
  (`store.Clear()` + empty the question box), `ClearAll` (also clears Answers), `TogglePin` (flip `Settings.AlwaysOnTop`),
  `ToggleStealth` (flip `Settings.StealthMode`), `ToggleTranscript` (flip `Settings.ShowTranscript`).
- `IPlatformWindowService.CommandReceived`: Ask → `Ask` (if the Live tab isn't visible, first
  `Shell.Current.GoToAsync("//live")`); Cancel → `Cancel`.
- MainPage `OnAppearing` → `windowService.InterceptEnterKey = true`; `OnDisappearing` → false.

MainPage layout (compact, readable at 460×720, also fine at 340 wide):
1. **Top bar** (single row): status dot (colored by StatusKind) + StatusText; tiny audio level bar; ElapsedText;
   right side chip buttons: Start/Stop (primary when idle), 📌 pin (highlighted when AlwaysOnTop),
   stealth 👁 (highlighted when on), transcript show/hide.
2. **Transcript card** (visible when `Settings.ShowTranscript`): section title "TRANSCRIPT" + "Clear" chip; the
   **editable question box** (`Editor` bound to `QuestionDraft`) fills the card; below it a live line (`LiveText` =
   the interim, italic InterimTextColor). Speech already sent with earlier questions is not shown.
   `Core/Transcription/QuestionDraft` appends new final captions to the box as they arrive — also while the user is
   editing — tracked by segment `Sequence`, so text the user deleted never comes back. On Windows the page saves the
   caret/selection before each append (`DraftAppending`) and restores it after (`DraftAppended`), and keeps the box
   scrolled to the newest words unless the user scrolled up. Enter in the box sends, Shift+Enter adds a line.
3. **Answer card** (fills the rest): question line (QuestionTextColor, bold, max 3 lines) + streaming indicator;
   `ScrollView` with `MarkdownLabel` bound to `CurrentItem.Answer` (font `AnswerFontSize`); error text in DangerColor;
   footer row: ◀ `PositionText` ▶, Copy, Regenerate, Stop (visible while generating). Empty state: short how-to
   (1. Setup: paste resume + job description, 2. Settings: add API keys, 3. Start and press Enter after each question).
4. **Bottom row**: `Entry` (placeholder "Type a question, or press Enter to answer the last one") bound to
   TypedQuestion with `ReturnCommand` = SubmitTyped; "Ask ⏎" PrimaryButton.
5. Toast: small overlay pill near the bottom when `ToastText` is non-empty.

### SetupViewModel + SetupPage (Setup tab)

- Fields bound straight to `Settings`: RoleTitle, CompanyName (Entries, side by side), JobDescription, Resume,
  ExtraNotes (Editors, fixed heights ~160/200/100, `AutoSize=Disabled`), each with a caption showing character count
  and an "Import…" chip (JD and Resume) and "Clear" chip.
- Import: `FilePicker.Default.PickAsync` with `FilePickerFileType` — WinUI: `.pdf .docx .txt .md`; Android:
  `application/pdf`, `application/vnd.openxmlformats-officedocument.wordprocessingml.document`, `text/plain`, `text/markdown`.
  `await using var s = await result.OpenReadAsync(); text = await extractor.ExtractAsync(s, result.FileName)`;
  errors → `DisplayAlert`-style message via a `StatusMessage` property on the page. Busy indicator while extracting.
- A readiness line at the top: "✓ Resume · ✓ Job description · ✓ API keys" (✗ in DangerColor when missing).

### SettingsViewModel + SettingsPage (Settings tab)

Scrollable sections (each in a `CardBorder`):
1. **API keys** — Deepgram key, OpenAI key (`IsPassword`, "Show" toggle), "Test" chip per key
   (`ITranscriptionClient.TestApiKeyAsync`, `IChatCompletionClient.ListModelsAsync`) showing ✓/✗ result text;
   OpenAI base URL (advanced, small caption "Any OpenAI-compatible endpoint").
2. **Transcription** — Audio source picker (System audio (interviewer) / Microphone; only Microphone when
   `!audio.SupportsSystemAudio`), device picker ("Default device" + `GetDevices()` of that kind, refresh chip),
   language picker (`SettingOptions.TranscriptionLanguages`), Deepgram model picker, custom vocabulary Entry
   (caption: "Comma-separated names/tech terms to recognise better"), endpointing slider 100–1000 ms.
3. **AI answers** — model (editable `Entry` + picker of `SuggestedOpenAiModels` / loaded models, "Load models"
   chip), answer length and format pickers, answer language Entry (empty = same as question), temperature slider 0–1,
   max tokens stepper/entry, history depth slider 0–10, reasoning effort picker, custom instructions Editor.
4. **Window & appearance** — Always on top switch, Opacity slider 30–100 % (live), Stealth switch (caption: "Hidden
   from screen sharing & recordings, taskbar and Alt+Tab"), Theme picker, Font size slider 11–22, Show transcript
   switch, Ask hotkey picker and Show/Hide hotkey picker (`SettingOptions.Hotkeys`; caption: "Global — work while
   Zoom/Teams has focus. Enter also works when this window is focused."). Hide rows whose `Supports*` is false.
5. **About** — version, "Reset settings" (keeps API keys & profile; confirm first).

Pickers bind to string/enum settings through view-model properties (e.g. `SelectedLanguage` ↔
`Settings.TranscriptionLanguage`). Changes apply immediately (no Save button); `SettingsService` auto-saves.

## Threading rules

- Service events arrive on background threads. View models marshal to the UI with `MainThread.BeginInvokeOnMainThread`
  (or `IDispatcher`) and throttle high-frequency updates (audio level, transcript, answer deltas).
- Never block the UI thread on async work (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`).
- `QaItem` and `ObservableCollection`s are mutated only on the UI thread.

## Build & test

```
dotnet build src/InterviewAssist/InterviewAssist.csproj -f net9.0-windows10.0.19041.0
dotnet test tests/InterviewAssist.Core.Tests
dotnet build src/InterviewAssist/InterviewAssist.csproj -f net9.0-android -p:EnableAndroid=true
```
