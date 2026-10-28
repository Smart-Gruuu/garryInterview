using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;
using InterviewAssist.Presentation.Services;
using InterviewAssist.Presentation.Theming;

namespace InterviewAssist.Desktop.Platform;

/// <summary>
/// Window behaviour for the Avalonia main window: always-on-top, see-through opacity, stealth (Windows only),
/// global hotkeys (X11), Enter/Escape interception and Ctrl + mouse wheel font sizing.
/// </summary>
public sealed class DesktopWindowService : IPlatformWindowService, IDisposable
{
    private const int AskHotkeyId = 1;
    private const int ToggleHotkeyId = 2;
    private const double MinOpacity = 0.3;
    private const int MaxUndeliveredWarnings = 10;

    private const string WaylandHotkeyWarning =
        "On Wayland, global hotkeys only work while an X11/XWayland window has focus. Enter in this window always works.";

    private readonly AppSettings _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly HotkeySlot _askHotkey = new(AskHotkeyId);
    private readonly HotkeySlot _toggleHotkey = new(ToggleHotkeyId);
    private readonly object _warningLock = new();
    private readonly List<string> _undeliveredWarnings = [];
    private readonly HashSet<Key> _keysDown = [];
    private EventHandler<string>? _warning;

    private Window? _window;
    private Visual? _fadedVisual;
    private IReadOnlyList<WindowTransparencyLevel>? _originalTransparencyHint;
    private IBrush? _originalBackground; // only when the window had a local Background; otherwise its style's applies
    private bool _transparencyRequested;
    private bool _compositorWarningShown;
    private bool _waylandWarningShown;
    private X11GlobalHotkeys? _hotkeys;
    private double _zoomWheelDelta; // Ctrl+wheel movement not yet turned into a font step (touchpads send fractions)
    private bool _disposed;

    public DesktopWindowService(ISettingsService settings, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _settings = settings.Settings;
        _dispatcher = dispatcher;
        SupportsGlobalHotkeys = X11GlobalHotkeys.IsAvailable;
    }

    public bool SupportsAlwaysOnTop => true;

    public bool SupportsOpacity => true;

    /// <summary>Only Windows can exclude a window from screen capture; X11/Wayland have no such API.</summary>
    public bool SupportsStealth => OperatingSystem.IsWindows();

    public bool SupportsGlobalHotkeys { get; }

    public bool InterceptEnterKey { get; set; }

    public event EventHandler<WindowCommand>? CommandReceived;

    /// <summary>
    /// Warnings raised before anyone subscribed (e.g. a hotkey conflict found while attaching at startup)
    /// are kept and delivered to the first subscriber.
    /// </summary>
    public event EventHandler<string>? Warning
    {
        add
        {
            string[] backlog;
            lock (_warningLock)
            {
                _warning += value;
                backlog = [.. _undeliveredWarnings];
                _undeliveredWarnings.Clear();
            }

            if (backlog.Length > 0)
            {
                // Deliver after the subscriber has finished wiring itself up.
                _dispatcher.Post(() =>
                {
                    foreach (var message in backlog)
                    {
                        RaiseWarning(message);
                    }
                });
            }
        }
        remove
        {
            lock (_warningLock)
            {
                _warning -= value;
            }
        }
    }

    /// <summary>
    /// Called on the UI thread with the main window (before or after it is shown). Applies the current settings
    /// and keeps applying them whenever they change, until the window closes.
    /// </summary>
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ReferenceEquals(window, _window))
        {
            return;
        }

        if (_window is not null)
        {
            Detach();
        }

        _window = window;
        window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        // handledEventsToo: a key-up swallowed by a control must still end the "held" state used to drop repeats.
        window.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
        window.PropertyChanged += OnWindowPropertyChanged;
        window.Opened += OnWindowOpened;
        window.Deactivated += OnWindowDeactivated;
        window.Closed += OnWindowClosed;

        if (SupportsGlobalHotkeys)
        {
            _hotkeys = new X11GlobalHotkeys(_dispatcher);
            _hotkeys.Pressed += OnHotkeyPressed;
            _hotkeys.Warning += OnHotkeyWarning;
        }

        ApplyAll();
        _settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    public void ToggleVisibility() => RunOnUi(ToggleVisibilityCore);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_dispatcher.IsUiThread)
        {
            Detach();
        }
        else
        {
            // E.g. the DI container disposing at shutdown: leave the window alone, but release the X grabs.
            _settings.PropertyChanged -= OnSettingsPropertyChanged;
            DisposeHotkeys();
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.AlwaysOnTop):
                RunOnUi(ApplyAlwaysOnTop);
                break;
            case nameof(AppSettings.Opacity):
                RunOnUi(ApplyOpacity);
                break;
            case nameof(AppSettings.StealthMode):
                RunOnUi(ApplyStealth);
                break;
            case nameof(AppSettings.AskHotkey):
            case nameof(AppSettings.ToggleWindowHotkey):
                RunOnUi(ApplyHotkeys);
                break;
            case null or "":
                RunOnUi(ApplyAll);
                break;
        }
    }

    private void ApplyAll()
    {
        // Each step is isolated so one failure doesn't skip the others.
        Execute(ApplyAlwaysOnTop);
        Execute(ApplyOpacity);
        Execute(ApplyStealth);
        Execute(ApplyHotkeys);
    }

    private void ApplyAlwaysOnTop()
    {
        if (_window is not null)
        {
            _window.Topmost = _settings.AlwaysOnTop;
        }
    }

    /// <summary>
    /// Makes the whole window see-through, not just dimmer: the window asks for a transparent surface, its own
    /// background is cleared and its content (which paints the app background) is faded, so the desktop shows through.
    /// At 1.0 the original transparency hint and background come back.
    /// </summary>
    private void ApplyOpacity()
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        var opacity = _settings.Opacity;
        opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, MinOpacity, 1.0) : 1.0;

        if (opacity < 1.0)
        {
            if (!_transparencyRequested)
            {
                _originalTransparencyHint = window.TransparencyLevelHint;
                _originalBackground = window.IsSet(TemplatedControl.BackgroundProperty) ? window.Background : null;
                window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
                window.Background = Brushes.Transparent;
                _transparencyRequested = true;
                _dispatcher.Post(() => Execute(CheckTransparencySupport));
            }
        }
        else if (_transparencyRequested)
        {
            RestoreOpaqueWindow(window);
        }

        SetFadedOpacity(window.Content as Visual, opacity);
    }

    private void RestoreOpaqueWindow(Window window)
    {
        window.TransparencyLevelHint = _originalTransparencyHint ?? [];
        if (_originalBackground is not null)
        {
            window.Background = _originalBackground;
        }
        else
        {
            window.ClearValue(TemplatedControl.BackgroundProperty);
        }

        _originalTransparencyHint = null;
        _originalBackground = null;
        _transparencyRequested = false;
    }

    private void SetFadedOpacity(Visual? target, double opacity)
    {
        if (_fadedVisual is not null && !ReferenceEquals(_fadedVisual, target))
        {
            _fadedVisual.Opacity = 1.0;
        }

        _fadedVisual = target;
        if (target is not null)
        {
            target.Opacity = opacity;
        }
    }

    private void CheckTransparencySupport()
    {
        // On X11 a see-through window needs a compositing window manager (normal on GNOME/KDE, not on bare WMs).
        if (_window is { } window
            && _transparencyRequested
            && window.ActualTransparencyLevel == WindowTransparencyLevel.None
            && !_compositorWarningShown)
        {
            _compositorWarningShown = true;
            RaiseWarning("Window opacity needs a compositing window manager; without one the window only gets dimmer.");
        }
    }

    private void ApplyStealth()
    {
        var window = _window;
        if (window is null || !OperatingSystem.IsWindows())
        {
            return;
        }

        var stealth = _settings.StealthMode;
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd != 0)
        {
            if (stealth)
            {
                if (!Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE))
                {
                    if (Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_MONITOR))
                    {
                        RaiseWarning("Stealth: this Windows version can only black out the window in captures");
                    }
                    else
                    {
                        RaiseWarning($"Stealth: Windows refused to hide this window from screen capture (error {Marshal.GetLastWin32Error()}).");
                    }
                }
            }
            else if (!Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_NONE))
            {
                Debug.WriteLine($"[WindowService] SetWindowDisplayAffinity(WDA_NONE) failed ({Marshal.GetLastWin32Error()}).");
            }
        }

        window.ShowInTaskbar = !stealth;
    }

    private void ApplyHotkeys()
    {
        if (_hotkeys is null)
        {
            return;
        }

        var askText = _settings.AskHotkey?.Trim() ?? "";
        var toggleText = _settings.ToggleWindowHotkey?.Trim() ?? "";

        var duplicate = HotkeyParser.TryParse(askText, out var ask)
            && HotkeyParser.TryParse(toggleText, out var toggle)
            && ask == toggle;

        var askChanged = !_askHotkey.IsAppliedAs(askText, suppressed: false);
        var toggleChanged = !_toggleHotkey.IsAppliedAs(toggleText, duplicate);

        // Unregister everything that changes first, so swapping the two combinations can't collide with ourselves.
        if (askChanged)
        {
            UnregisterHotkey(_askHotkey);
        }

        if (toggleChanged)
        {
            UnregisterHotkey(_toggleHotkey);
        }

        if (askChanged)
        {
            RegisterHotkey(_askHotkey, askText, suppressed: false);
        }

        if (toggleChanged)
        {
            RegisterHotkey(_toggleHotkey, toggleText, duplicate);
        }

        if (!_waylandWarningShown
            && (_askHotkey.Registered || _toggleHotkey.Registered)
            && string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase))
        {
            _waylandWarningShown = true;
            RaiseWarning(WaylandHotkeyWarning);
        }
    }

    private void RegisterHotkey(HotkeySlot slot, string text, bool suppressed)
    {
        slot.AppliedText = text;
        slot.AppliedSuppressed = suppressed;
        slot.Registered = false;

        if (HotkeyParser.IsNone(text) || _hotkeys is null)
        {
            return;
        }

        if (!HotkeyParser.TryParse(text, out var hotkey))
        {
            RaiseWarning($"Hotkey \"{text}\" isn't a valid key combination. Pick another in Settings.");
            return;
        }

        if (suppressed)
        {
            RaiseWarning($"The Ask and Show/Hide hotkeys are both {text}, so only Ask will work. Pick a different Show/Hide hotkey in Settings.");
            return;
        }

        if (!HotkeyParser.IsSafeGlobal(hotkey))
        {
            RaiseWarning($"Hotkey {text} needs Ctrl, Alt or Super/Win (or an F-key) to work as a global hotkey. Pick another in Settings.");
            return;
        }

        // The grab happens on the X thread; a conflict comes back through the Warning event.
        _hotkeys.Register(slot.Id, hotkey, text);
        slot.Registered = true;
    }

    private void UnregisterHotkey(HotkeySlot slot)
    {
        if (slot.Registered)
        {
            _hotkeys?.Unregister(slot.Id);
        }

        slot.Reset();
    }

    private void OnHotkeyPressed(object? sender, int id)
    {
        switch (id)
        {
            case AskHotkeyId:
                RaiseCommand(WindowCommand.Ask);
                break;
            case ToggleHotkeyId:
                Execute(ToggleVisibilityCore);
                break;
        }
    }

    private void OnHotkeyWarning(object? sender, string message) => RaiseWarning(message);

    private void ToggleVisibilityCore()
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        var minimized = window.WindowState == WindowState.Minimized;
        if (window.IsVisible && !minimized)
        {
            window.Hide();
            return;
        }

        // A minimized window counts as "hidden" too: with stealth on it has no taskbar button to restore it from.
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        WindowCommand command;
        if (e.Key == Key.Enter)
        {
            command = WindowCommand.Ask;
        }
        else if (e.Key == Key.Escape)
        {
            command = WindowCommand.Cancel;
        }
        else
        {
            return;
        }

        // Avalonia doesn't flag auto-repeats; a key-down without a key-up in between is one.
        var isRepeat = !_keysDown.Add(e.Key);

        if (!InterceptEnterKey || e.Handled || IsTextInputFocused(e))
        {
            return;
        }

        // Swallow auto-repeats too, so a held Enter can't keep invoking the focused button.
        e.Handled = true;
        if (!isRepeat)
        {
            RaiseCommand(command);
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e) => _keysDown.Remove(e.Key);

    private void OnWindowDeactivated(object? sender, EventArgs e) => _keysDown.Clear(); // key-ups go elsewhere now

    private bool IsTextInputFocused(KeyEventArgs e)
    {
        // Walk up from the focused element: focus often sits on a template part inside the actual control.
        var node = _window?.FocusManager?.GetFocusedElement() as Visual ?? e.Source as Visual;
        while (node is not null)
        {
            switch (node)
            {
                case TextBox or AutoCompleteBox:
                    return true;
                case ComboBoxItem:
                    return true; // focus is inside an open drop-down
                case ComboBox comboBox:
                    return comboBox.IsDropDownOpen;
            }

            node = node.GetVisualParent();
        }

        return false;
    }

    /// <summary>Ctrl + mouse wheel changes the app-wide font size (Settings.FontSize), one point per wheel notch.</summary>
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _zoomWheelDelta = 0;
            return;
        }

        var delta = e.Delta.Y;
        if (delta == 0 || !double.IsFinite(delta))
        {
            return;
        }

        e.Handled = true;

        // Accumulate partial notches (one notch = 1.0); start over when the direction flips.
        _zoomWheelDelta = Math.Sign(delta) == Math.Sign(_zoomWheelDelta) ? _zoomWheelDelta + delta : delta;
        var steps = Math.Truncate(_zoomWheelDelta);
        if (steps == 0)
        {
            return;
        }

        _zoomWheelDelta -= steps;
        var current = double.IsFinite(_settings.FontSize) ? Math.Round(_settings.FontSize) : FontSizeLimits.Min;
        var size = Math.Clamp(current + steps, FontSizeLimits.Min, FontSizeLimits.Max);
        if (size != _settings.FontSize)
        {
            _settings.FontSize = size;
        }
    }

    // The content is what gets faded, so a replaced content needs the opacity too.
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ContentControl.ContentProperty)
        {
            Execute(ApplyOpacity);
        }
    }

    // Some platforms only create or accept native window attributes once the window is shown.
    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (_settings.StealthMode)
        {
            Execute(ApplyStealth);
        }

        Execute(ApplyOpacity);
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Detach();

    private void Detach()
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        _settings.PropertyChanged -= OnSettingsPropertyChanged;

        try
        {
            window.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            window.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
            window.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged);
            window.PropertyChanged -= OnWindowPropertyChanged;
            window.Opened -= OnWindowOpened;
            window.Deactivated -= OnWindowDeactivated;
            window.Closed -= OnWindowClosed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowService] Unhooking window events failed: {ex}");
        }

        DisposeHotkeys();
        _askHotkey.Reset();
        _toggleHotkey.Reset();
        _keysDown.Clear();
        _fadedVisual = null;
        _originalTransparencyHint = null;
        _originalBackground = null;
        _transparencyRequested = false;
        _window = null;
    }

    private void DisposeHotkeys()
    {
        var hotkeys = Interlocked.Exchange(ref _hotkeys, null);
        if (hotkeys is not null)
        {
            hotkeys.Pressed -= OnHotkeyPressed;
            hotkeys.Warning -= OnHotkeyWarning;
            hotkeys.Dispose(); // ungrabs everything and closes its X connection
        }
    }

    /// <summary>Runs on the UI thread: inline when already there, otherwise queued.</summary>
    private void RunOnUi(Action action)
    {
        if (_dispatcher.IsUiThread)
        {
            Execute(action);
        }
        else
        {
            _dispatcher.Post(() => Execute(action));
        }
    }

    private void Execute(Action action)
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowService] {action.Method.Name} failed: {ex}");
        }
    }

    private void RaiseCommand(WindowCommand command)
    {
        try
        {
            CommandReceived?.Invoke(this, command);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowService] CommandReceived({command}) handler failed: {ex}");
        }
    }

    private void RaiseWarning(string message)
    {
        Debug.WriteLine($"[WindowService] {message}");

        EventHandler<string>? handler;
        lock (_warningLock)
        {
            handler = _warning;
            if (handler is null)
            {
                if (_undeliveredWarnings.Count < MaxUndeliveredWarnings)
                {
                    _undeliveredWarnings.Add(message);
                }

                return;
            }
        }

        try
        {
            handler(this, message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowService] Warning handler failed: {ex}");
        }
    }

    /// <summary>What is currently applied for one global hotkey id, so unrelated setting changes don't re-register it.</summary>
    private sealed class HotkeySlot(int id)
    {
        public int Id { get; } = id;

        /// <summary>Null until the slot has been applied (or after it was reset).</summary>
        public string? AppliedText { get; set; }

        public bool AppliedSuppressed { get; set; }

        public bool Registered { get; set; }

        public bool IsAppliedAs(string text, bool suppressed) =>
            AppliedText is not null
            && string.Equals(AppliedText, text, StringComparison.Ordinal)
            && AppliedSuppressed == suppressed;

        public void Reset()
        {
            AppliedText = null;
            AppliedSuppressed = false;
            Registered = false;
        }
    }

    /// <summary>user32 window display affinity (winuser.h), used for stealth when the desktop app runs on Windows.</summary>
    private static class Win32
    {
        public const uint WDA_NONE = 0x0;
        public const uint WDA_MONITOR = 0x1;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
    }
}
