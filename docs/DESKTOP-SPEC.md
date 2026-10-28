# Linux desktop app (Avalonia) + shared presentation layer — implementation spec

Read `docs/ARCHITECTURE.md` first for the product and the existing MAUI app. This document describes:

- **Part A** — extracting a UI-framework-neutral presentation layer (`src/InterviewAssist.Presentation`) out of the
  MAUI app, so the Windows/Android app and the new Linux app run the *same* view models, settings persistence,
  markdown parser and theme palettes.
- **Part B** — the Avalonia desktop app (`src/InterviewAssist.Desktop`), whose primary target is **Linux x64**
  (X11, and Wayland desktops through XWayland). It also runs on Windows, which is how it is tested on this machine.

Behaviour must stay identical to the MAUI app. When in doubt, the current MAUI code is the reference.

## Projects after this work

```
src/InterviewAssist.Core/           (unchanged) net9.0 — Deepgram, OpenAI, transcript, prompt, documents
src/InterviewAssist.Presentation/   NEW net9.0 — view models, SettingsService, ThemePalettes, MarkdownParser, UI service interfaces
src/InterviewAssist/                MAUI app (Windows + Android) — views, MAUI adapters for the Presentation interfaces, platform services
src/InterviewAssist.Desktop/        NEW Avalonia 12.1.4 app (Linux; also runs on Windows) — views, Avalonia adapters, Linux platform services
tests/InterviewAssist.Core.Tests/   (unchanged)
tests/InterviewAssist.Presentation.Tests/  NEW xunit — view model + settings tests with fakes
```

`InterviewAssist.sln` contains all of them. `global.json` pins SDK 9.0.314.

---

## Part A — shared presentation layer

### Contracts (already written — do not change without a strong reason)

`src/InterviewAssist.Presentation/Services/`:
- `IUiDispatcher` / `IUiTimer` — UI thread posting, delayed posting, `InvokeAsync`, timers (replace MAUI `MainThread`, `IDispatcher`, `IDispatcherTimer`).
- `INavigationService` + `AppPage {Live, Setup, Settings}` (replaces `Shell.Current.GoToAsync("//live")` etc.).
- `IClipboardService`, `IFilePickerService` + `PickedFile`, `IDialogService` (Confirm / Alert / Choose — replaces
  `DisplayActionSheet` and the `ConfirmAsync`/`AlertAsync` `Func` properties the pages used to inject).
- `ILauncherService` (opens the API key sign-up links in the browser; replaces `Launcher.Default`).
- `IAppEnvironment` + `AppPlatform` (replaces `DeviceInfo.Current.Platform` and `AppInfo` in view models).
- `IPlatformWindowService` + `WindowCommand`, `IThemeService` — moved here from the MAUI project **without** `Attach(Window)`.
- `ISettingsLocation`, `ISecretStore` — what `SettingsService` needs from the host.

### What moves into `InterviewAssist.Presentation`

| From (MAUI project) | To (Presentation) | Namespace |
|---|---|---|
| `ViewModels/MainViewModel.cs` (+ `StatusKind`) | `ViewModels/MainViewModel.cs` | `InterviewAssist.Presentation.ViewModels` |
| `ViewModels/SetupViewModel.cs` | `ViewModels/SetupViewModel.cs` | same |
| `ViewModels/SettingsViewModel.cs` (+ its option records) | `ViewModels/SettingsViewModel.cs` | same |
| `Services/SettingsService.cs` | `Settings/SettingsService.cs` | `InterviewAssist.Presentation.Settings` |
| palette records/data in `Services/ThemeService.cs` | `Theming/ThemePalettes.cs` | `InterviewAssist.Presentation.Theming` |
| `ThemeService.MinFontSize/MaxFontSize` | `Theming/FontSizeLimits.cs` (`Min = 11`, `Max = 22`, `Default = 14`) | same |
| `MarkdownParser`, `MarkdownRun`, `RunStyle` (in `Controls/MarkdownLabel.cs`) | `Markdown/MarkdownParser.cs` (make them `public`) | `InterviewAssist.Presentation.Markdown` |
| `Services/IPlatformWindowService.cs`, `Services/IThemeService.cs` | deleted (replaced by the contracts above) | — |

Rules for the moved code:
- **No MAUI types.** Replace: `MainThread.*`/`IDispatcher`/`Dispatcher.GetForCurrentThread()` → `IUiDispatcher`;
  `IDispatcherTimer` → `IUiTimer`; `Shell.Current.GoToAsync(...)` → `INavigationService`; `page.DisplayActionSheet` →
  `IDialogService.ChooseAsync`; page-injected `ConfirmAsync`/`AlertAsync` funcs → `IDialogService` (remove the func properties);
  `Clipboard.Default` → `IClipboardService`; `FilePicker`/`FilePickerFileType`/`DevicePlatform` → `IFilePickerService`
  (pass `IDocumentTextExtractor.SupportedExtensions`; the MAUI adapter maps them to MIME types on Android);
  `DeviceInfo`/`AppInfo` → `IAppEnvironment`; `ThemeService.MinFontSize` → `FontSizeLimits`.
- **Public API of the view models stays the same** (property, command and event names), so the MAUI XAML keeps
  working after the namespace change and the Avalonia views can bind to the same names. Constructors gain the
  new service parameters.
- `SettingsViewModel.StealthCaption` must describe what stealth does on the current platform: Windows → hidden from
  screen sharing/recordings, taskbar and Alt+Tab; Android → blocks screenshots and screen recording; Linux → (row is
  hidden because `SupportsStealth` is false, but return a sensible string anyway).
- `SettingsService` keeps every behaviour it has today (sync load of settings.json in the ctor incl. the v2 migration,
  debounced atomic saves, `Flush()`, `ResetToDefaults`, keys never written to the JSON, "a key typed while loading
  wins", writes coalesced per key) but gets its directory from `ISettingsLocation`, its secrets from `ISecretStore`, and
  UI-thread work from `IUiDispatcher`. The SecureStorage→Preferences fallback and migration logic that is MAUI-specific
  moves into the MAUI `ISecretStore` adapter.
- `ThemePalettes`: `public sealed record ThemePalette(bool IsDark, string AppBackground, string Surface, string SurfaceAlt,
  string Border, string Text, string SubtleText, string Accent, string OnAccent, string InterimText, string QuestionText,
  string Success, string Warning, string Danger, string CodeBackground, string CodeText)` (hex strings, exact current values)
  and `public static class ThemePalettes { IReadOnlyList<string> Names; ThemePalette Get(string? name) /* unknown → Dark */;
  IReadOnlyList<(string Key, string Hex)> ColorResources(ThemePalette p) /* "AppBackgroundColor" … "CodeTextColor" */ }`.

### MAUI app after the move

- New adapters in `src/InterviewAssist/Services/`: `MauiUiDispatcher` (wraps the app `IDispatcher`, timers via
  `CreateTimer`), `MauiNavigationService` (`//live`, `//setup`, `//settings`), `MauiClipboardService`,
  `MauiFilePickerService`, `MauiLauncherService`, `MauiDialogService` (uses `Shell.Current.CurrentPage`/`Application.Current.Windows[0].Page`
  `DisplayAlert`/`DisplayActionSheet`), `MauiAppEnvironment`, `MauiSettingsLocation` (`FileSystem.AppDataDirectory` —
  **the same folder as today**, so existing settings and keys are kept), `MauiSecretStore` (SecureStorage with the
  existing Preferences fallback + migration).
- `ThemeService` (MAUI) uses `ThemePalettes`; `MarkdownLabel` uses the shared parser; `WindowsWindowService` /
  `AndroidWindowService` implement the shared `IPlatformWindowService` and keep a public `Attach(Window)` that
  `App.xaml.cs` calls through the concrete type (register each as itself and as the interface — same singleton).
- Pages/converters/XAML: update namespaces (`xmlns:vm="clr-namespace:InterviewAssist.Presentation.ViewModels;assembly=InterviewAssist.Presentation"`),
  remove the code that injected `ConfirmAsync`/`AlertAsync`. `MauiProgram` registers everything.
- The MAUI app must behave exactly as before and build for Windows and Android.

### Presentation tests (`tests/InterviewAssist.Presentation.Tests`)

xunit, net9.0, references Presentation + Core. Fakes: inline `IUiDispatcher` (Post runs queued work on `Drain()`,
timers manual), fake audio/transcription/chat/window/dialog/clipboard/navigation/file picker, temp-dir settings location,
in-memory secret store. Cover at least: Start/Stop listening happy path + failures (missing key, audio start fails →
transcription stopped); captions → `QuestionDraft` appended; Ask takes the edited draft + interim and streams the answer
into a new `QaItem`; double-Enter guard; Cancel keeps partial text; missing OpenAI key error item; Regenerate;
interview type chooser via `IDialogService.ChooseAsync`; Esc/Enter window commands; settings load/save round-trip,
v2 migration, keys go to the secret store not the JSON, ResetToDefaults keeps profile + interview type;
SettingsViewModel option mappings; SetupViewModel import success/error paths via fake picker + real extractor.

---

## Part B — Avalonia desktop app (`src/InterviewAssist.Desktop`)

### Project

- `InterviewAssist.Desktop.csproj`: `net9.0`, `OutputType` WinExe, `AssemblyName` `interview-assist`,
  `RootNamespace` `InterviewAssist.Desktop`, `Nullable` enable, `AvaloniaUseCompiledBindingsByDefault` true,
  `ApplicationIcon` optional. Packages: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`
  (all **12.1.4**), `CommunityToolkit.Mvvm` 8.4.2, `Microsoft.Extensions.DependencyInjection` 9.0.8,
  `NAudio.Wasapi` 2.2.1 (only used when running on Windows). References Core + Presentation.
  Links `..\InterviewAssist\Platforms\Windows\WindowsAudioCaptureService.cs` (it has no MAUI dependency) for Windows runs.
- Avalonia 12 has breaking changes from 11 — check https://docs.avaloniaui.net/docs/avalonia12-breaking-changes and
  verify everything by compiling. Do not use `Avalonia.Diagnostics` (removed in 12).

### Layout

```
Program.cs                      AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace(); StartWithClassicDesktopLifetime
App.axaml(.cs)                  FluentTheme + Styles/AppStyles.axaml; builds the DI container in OnFrameworkInitializationCompleted
Composition.cs                  ServiceCollection registrations (see below)
MainWindow.axaml(.cs)           TabControl: Live / Setup / Settings (compact tab strip); window bounds from settings; attaches DesktopWindowService
ViewModels/ShellViewModel.cs    selected tab index (used by AvaloniaNavigationService)
Views/LiveView.axaml(.cs)       = MAUI MainPage (Live tab)
Views/SetupView.axaml(.cs)      = MAUI SetupPage
Views/SettingsView.axaml(.cs)   = MAUI SettingsPage
Views/Dialogs/*.axaml           small modal dialogs for IDialogService (confirm, alert, choose-from-list)
Controls/MarkdownTextBlock.cs   SelectableTextBlock rendering MarkdownParser runs into Inlines (throttled 60 ms like MarkdownLabel)
Converters/*.cs                 Avalonia IValueConverter equivalents of the MAUI converters
Styles/AppStyles.axaml          control styles using the theme resources (CardBorder, SectionTitle, CaptionLabel, PrimaryButton, ChipButton …)
Services/AvaloniaUiDispatcher.cs, AvaloniaNavigationService.cs, AvaloniaClipboardService.cs, AvaloniaFilePickerService.cs,
         AvaloniaDialogService.cs, AvaloniaLauncherService.cs, DesktopAppEnvironment.cs, DesktopSettingsLocation.cs, FileSecretStore.cs, AvaloniaThemeService.cs
Platform/DesktopWindowService.cs     IPlatformWindowService for an Avalonia Window (+ public Attach(Window))
Platform/X11GlobalHotkeys.cs         XGrabKey-based global hotkeys (Linux/X11 only)
Platform/PulseAudioCaptureService.cs IAudioCaptureService via libpulse-simple (Linux; PulseAudio and PipeWire-pulse)
Platform/UnsupportedAudioCaptureService.cs  for other OSes (throws a friendly InvalidOperationException on Start)
packaging/linux/interview-assist.desktop, packaging/linux/README.txt, packaging/publish-linux.ps1
```

### Composition (all singletons)

Core services exactly as in the MAUI app (`TranscriptStore`, `DeepgramTranscriptionClient`, `OpenAiChatClient`,
`PromptBuilder`, `DocumentTextExtractor`); `SettingsService` (Presentation) with `DesktopSettingsLocation` +
`FileSecretStore`; `AvaloniaThemeService`; the Avalonia adapters; `DesktopWindowService` (as itself and as
`IPlatformWindowService`); audio by OS: Linux → `PulseAudioCaptureService`, Windows → `WindowsAudioCaptureService`,
else `UnsupportedAudioCaptureService`; the three Presentation view models; `ShellViewModel`; `MainWindow`.
On exit (`desktop.ShutdownRequested`/`Exit`): save window bounds, `SettingsService.Flush()`, dispose audio.

### Settings & keys

- `DesktopSettingsLocation`: Linux `$XDG_CONFIG_HOME/interview-assist` (default `~/.config/interview-assist`); Windows
  `%APPDATA%\InterviewAssist.Desktop`; macOS `~/Library/Application Support/InterviewAssist`.
- `FileSecretStore`: `secrets.json` in the same directory, written atomically; on Unix create/chmod it **0600**
  (`File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite)`) and the directory 0700.

### Theming

`AvaloniaThemeService : IThemeService`: for the selected `ThemePalette` set, in `Application.Current.Resources`,
both `"{Name}Color"` (`Color`) and `"{Name}Brush"` (`SolidColorBrush`) for every palette key (e.g. `AppBackgroundColor`
/ `AppBackgroundBrush`), plus `BaseFontSize`, `SmallFontSize`, `LargeFontSize`, `AnswerFontSize` (double, same formulas as
MAUI), and `Application.Current.RequestedThemeVariant` = Dark/Light from `IsDark`. Views use `{DynamicResource …Brush}`
and `{DynamicResource …FontSize}`. `App.axaml` defines the Dark defaults so the designer/first frame resolve.

### Views — parity with the MAUI pages

Recreate `MainPage`, `SetupPage`, `SettingsPage` faithfully (same sections, texts, commands, visibility rules,
compact sizing that works from 340 px wide). Live tab specifics:
- Question box (`TextBox`, `AcceptsReturn`, wrapping, fills the transcript card, two-way bound to `QuestionDraft`):
  **Enter sends** (`AskCommand`), **Shift+Enter** inserts a newline, Esc cancels generation while generating.
  Before/after `DraftAppending`/`DraftAppended`, save and restore `SelectionStart`/`SelectionEnd`/`CaretIndex` when the
  box has focus, and keep it scrolled to the end unless the user scrolled up (same behaviour as the MAUI page).
- Live line (italic, `InterimTextBrush`), answer card with `MarkdownTextBlock`, footer (◀ n/m ▶, Stop, Copy, ↻, 🗑),
  bottom row (typed question `TextBox`, interview-type chip `{InterviewTypeLabel} ▾`, `Ask ⏎`), toast overlay, top bar
  (status dot by `StatusKind`, status text, level meter, elapsed, Start/Stop, 📌, 👁 (hidden when stealth unsupported), 💬).
- Call `OnPageAppearing()`/`OnPageDisappearing()` when the Live tab becomes selected / unselected.
- Answer `ScrollViewer` scrolls to top on `AnswerReset`.

### `DesktopWindowService` (Platform/)

Implements `IPlatformWindowService`; `public void Attach(Window window)`; subscribes to settings changes (marshal to UI).
- Always on top: `window.Topmost`.
- Opacity (0.3–1.0): the whole window must become see-through (desktop visible), not just dimmer content. Use
  `TransparencyLevelHint = [WindowTransparencyLevel.Transparent]`, a transparent window background, and the root
  content's `Opacity`; at 1.0 restore opaque settings. (On X11 this needs a compositing window manager — normal on GNOME/KDE.)
- Stealth: not available on Linux (no API to exclude a window from capture) → `SupportsStealth = false` there.
  On Windows, implement it with `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` via `window.TryGetPlatformHandle()`
  plus `ShowInTaskbar = false`, so the desktop app behaves like the MAUI one when run on Windows.
- Enter / Escape: `window.AddHandler(InputElement.KeyDownEvent, …, RoutingStrategies.Tunnel)`; same rules as
  `WindowsWindowService` (only when `InterceptEnterKey`, not when focus is in a `TextBox`/open `ComboBox`/`AutoCompleteBox`;
  mark handled; ignore repeats).
- Ctrl + mouse wheel: `AddHandler(PointerWheelChangedEvent, …, RoutingStrategies.Tunnel)`: when Ctrl is held, change
  `Settings.FontSize` by ±1 per notch (accumulate fractional deltas, reset on direction change), clamp to `FontSizeLimits`,
  and mark handled so nothing scrolls.
- `ToggleVisibility`: `Hide()` / `Show()` + `Activate()`.
- Global hotkeys: on Linux with X11 available (`DISPLAY` set and libX11 loadable) use `X11GlobalHotkeys`; parse hotkeys
  with the same grammar as `WindowsWindowService.TryParseHotkey` (copy the parser into a shared helper in the desktop project).
  On a Wayland session (`XDG_SESSION_TYPE=wayland`) raise a one-time `Warning`: global hotkeys only fire while an X11/XWayland
  window has focus; Enter in the window always works. Elsewhere (Windows desktop run) `SupportsGlobalHotkeys` may be false.

### `X11GlobalHotkeys`

P/Invoke `libX11.so.6`: `XInitThreads`, `XOpenDisplay(null)` (own connection), `XDefaultRootWindow`, `XKeysymToKeycode`,
`XStringToKeysym`, `XGrabKey` for each modifier combination × {none, LockMask, Mod2Mask(NumLock), both} (no `XSelectInput`:
grabbed events arrive regardless of the event mask; match the event state exactly, ignoring only Lock/Mod2), `XPending`/`XNextEvent` on a dedicated background thread (wake/stop via `ConnectionNumber` + `poll` with a timeout or a
short sleep loop), `XUngrabKey`, `XCloseDisplay`. Install an `XSetErrorHandler` so a `BadAccess` (key already grabbed
by another app) is reported as a `Warning` instead of crashing. Fire callbacks through `IUiDispatcher`.

### `PulseAudioCaptureService`

- Capture with `libpulse-simple.so.0` (`pa_simple_new` with `PA_STREAM_RECORD`, sample spec `PA_SAMPLE_S16LE` 16000 Hz
  1 channel — the server resamples/down-mixes; buffer attributes `fragsize` ≈ 100 ms = 3200 bytes), `pa_simple_read` in a
  dedicated thread, `pa_simple_free`, `pa_strerror` for messages. Raise `ChunkAvailable(pcm, PcmConverter.ComputeLevel(pcm))`.
- Device for SystemAudio: the chosen sink's monitor source (the sink's "Monitor Source:" from `pactl list sinks`, else
  `<sink>.monitor`); default `@DEFAULT_MONITOR@`. `pa_simple_read` has no timeout, so a watchdog raises `Error` when no
  audio has arrived for 5 s (e.g. a suspended source).
  Microphone: the chosen source; default `@DEFAULT_SOURCE@` (or null). Works on PipeWire via pipewire-pulse.
- `GetDevices()`: run `pactl list sinks` / `pactl list sources` (fallback: the `short` forms; 2 s timeout, and after one
  timeout skip the remaining calls; skip `.monitor` sources for microphones) and parse the defaults from `pactl info`.
  If `pactl` is missing, return just "Default output (what you hear)" and "Default microphone" entries. Never throw.
- `libpulse-simple` missing → `InvalidOperationException("Linux audio needs the PulseAudio client library. Install libpulse0 (Debian/Ubuntu) or pulseaudio-libs (Fedora).")`.
- `SupportsSystemAudio = true`. Loopback-silence filling isn't needed (monitor sources stream continuously).

### Packaging

`packaging/publish-linux.ps1`: `dotnet publish src/InterviewAssist.Desktop -c Release -r linux-x64 --self-contained
-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/linux-x64`, then a
`publish/InterviewAssist-1.0-linux-x64.tar.gz` containing `interview-assist` (executable bit set via tar entry mode 0755),
`interview-assist.desktop`, an icon PNG, and `README.txt` (requirements: x64 Linux, glibc, X11 or XWayland, libpulse;
how to run; where settings/keys are stored; Wayland hotkey limitation; no stealth on Linux).

## Build & test

```
dotnet build src/InterviewAssist/InterviewAssist.csproj -f net9.0-windows10.0.19041.0
dotnet build src/InterviewAssist/InterviewAssist.csproj -f net9.0-android -p:EnableAndroid=true
dotnet build src/InterviewAssist.Desktop
dotnet publish src/InterviewAssist.Desktop -c Release -r linux-x64 --self-contained   (cross-compiles fine on Windows)
dotnet test tests/InterviewAssist.Core.Tests
dotnet test tests/InterviewAssist.Presentation.Tests
```
