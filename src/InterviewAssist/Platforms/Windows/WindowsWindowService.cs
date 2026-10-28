using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;
using InterviewAssist.Presentation.Services;
using InterviewAssist.Presentation.Theming;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;
using MauiWindow = Microsoft.Maui.Controls.Window;
using VirtualKey = Windows.System.VirtualKey;
using XamlControls = Microsoft.UI.Xaml.Controls;
using XamlWindow = Microsoft.UI.Xaml.Window;

namespace InterviewAssist.Native;

/// <summary>
/// WinUI 3 window behaviour: always-on-top, opacity, capture exclusion ("stealth"), global hotkeys
/// (RegisterHotKey + comctl32 subclass for WM_HOTKEY) and Enter/Escape interception.
/// </summary>
public sealed class WindowsWindowService : IPlatformWindowService
{
    private const int AskHotkeyId = 0xA001;
    private const int ToggleHotkeyId = 0xA002;
    private const nuint SubclassId = 0xA17;
    private const double MinOpacity = 0.3;
    private const int MaxUndeliveredWarnings = 10;

    private static readonly Dictionary<string, uint> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = NativeMethods.MOD_CONTROL,
        ["Control"] = NativeMethods.MOD_CONTROL,
        ["Alt"] = NativeMethods.MOD_ALT,
        ["Shift"] = NativeMethods.MOD_SHIFT,
        ["Win"] = NativeMethods.MOD_WIN,
        ["Windows"] = NativeMethods.MOD_WIN,
    };

    private static readonly Dictionary<string, uint> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = 0x0D,
        ["Return"] = 0x0D,
        ["Space"] = 0x20,
        ["Tab"] = 0x09,
        ["Esc"] = 0x1B,
        ["Escape"] = 0x1B,
        ["Backspace"] = 0x08,
        ["Insert"] = 0x2D,
        ["Ins"] = 0x2D,
        ["Delete"] = 0x2E,
        ["Del"] = 0x2E,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PgUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["PgDn"] = 0x22,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["Pause"] = 0x13,
    };

    private readonly AppSettings _settings;
    private readonly NativeMethods.SubclassProc _subclassProc; // held in a field so the GC can't collect the native thunk
    private readonly HotkeySlot _askHotkey = new(AskHotkeyId);
    private readonly HotkeySlot _toggleHotkey = new(ToggleHotkeyId);
    private readonly object _warningLock = new();
    private readonly List<string> _undeliveredWarnings = [];
    private EventHandler<string>? _warning;

    private XamlWindow? _nativeWindow;
    private AppWindow? _appWindow;
    private volatile DispatcherQueue? _dispatcher;
    private nint _hwnd;
    private Microsoft.UI.Xaml.UIElement? _keyTarget;
    private PointerEventHandler? _wheelHandler;
    private int _zoomWheelDelta; // Ctrl+wheel movement not yet turned into a font step (precision touchpads send small deltas)
    private bool _subclassInstalled;
    private volatile bool _attached;

    public WindowsWindowService(ISettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        _settings = settingsService.Settings;
        _subclassProc = WindowSubclassProc;
    }

    public bool SupportsAlwaysOnTop => true;
    public bool SupportsOpacity => true;
    public bool SupportsStealth => true;
    public bool SupportsGlobalHotkeys => true;

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
                // Deliver after the subscriber has finished wiring itself up, on the UI thread.
                _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () =>
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
    /// Called by App, on the UI thread, once the MAUI window has its native handler. Applies the current settings
    /// and keeps applying them whenever they change.
    /// </summary>
    public void Attach(MauiWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.Handler?.PlatformView is not XamlWindow nativeWindow)
        {
            Debug.WriteLine("[WindowService] Attach called before the window has a native handler; ignored.");
            return;
        }

        if (ReferenceEquals(nativeWindow, _nativeWindow))
        {
            return;
        }

        if (_nativeWindow is not null)
        {
            Detach();
        }

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
        _nativeWindow = nativeWindow;
        _dispatcher = nativeWindow.DispatcherQueue;
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd));

        nativeWindow.Closed += OnNativeWindowClosed;
        // Activated also covers the case where Content isn't set yet (and re-hooks if it is ever replaced).
        nativeWindow.Activated += OnNativeWindowActivated;
        HookKeyboard();

        _subclassInstalled = NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, SubclassId, 0);
        _attached = true;

        if (!_subclassInstalled)
        {
            RaiseWarning("Global hotkeys are unavailable: the window could not be hooked.");
        }

        ApplyAll();
        _settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    public void ToggleVisibility() => RunOnUi(ToggleVisibilityCore);

    /// <summary>
    /// Parses hotkey text such as "Ctrl+Shift+Enter", "alt + q" or "F9" into RegisterHotKey modifiers
    /// (MOD_ALT/MOD_CONTROL/MOD_SHIFT/MOD_WIN) and a Win32 virtual-key code. Case-insensitive; accepts
    /// Ctrl/Control, Alt, Shift, Win/Windows and exactly one key: Enter/Return, Space, Tab, Esc/Escape,
    /// Backspace, Insert, Delete, Home, End, PageUp, PageDown, arrow keys, Pause, F1–F24, A–Z, 0–9.
    /// Returns false (with both outputs 0) for "None", empty text, or anything unrecognised.
    /// </summary>
    public static bool TryParseHotkey(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (IsNoneHotkey(text))
        {
            return false;
        }

        uint parsedModifiers = 0;
        uint parsedKey = 0;

        foreach (var rawPart in text.Split('+'))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
            {
                return false;
            }

            if (ModifierNames.TryGetValue(part, out var modifier))
            {
                parsedModifiers |= modifier;
                continue;
            }

            // Exactly one non-modifier key is allowed.
            if (parsedKey != 0 || !TryParseKey(part, out parsedKey))
            {
                return false;
            }
        }

        if (parsedKey == 0)
        {
            return false;
        }

        modifiers = parsedModifiers;
        vk = parsedKey;
        return true;
    }

    private static bool TryParseKey(string name, out uint vk)
    {
        if (KeyNames.TryGetValue(name, out vk))
        {
            return true;
        }

        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c;
                return true;
            }
        }

        if (name.Length is 2 or 3
            && (name[0] == 'F' || name[0] == 'f')
            && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= 24)
        {
            vk = NativeMethods.VK_F1 + (uint)(number - 1);
            return true;
        }

        vk = 0;
        return false;
    }

    private static bool IsNoneHotkey(string? text) =>
        string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), "None", StringComparison.OrdinalIgnoreCase);

    // A global hotkey without Ctrl/Alt/Win would steal an everyday key (Enter, Shift+A, ...) from every app.
    private static bool IsSafeGlobalHotkey(uint modifiers, uint vk) =>
        (modifiers & (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_WIN)) != 0
        || vk is >= NativeMethods.VK_F1 and <= NativeMethods.VK_F24;

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
        // Each step is isolated so one failing native call doesn't skip the others.
        Execute(ApplyAlwaysOnTop);
        Execute(ApplyOpacity);
        Execute(ApplyStealth);
        Execute(ApplyHotkeys);
    }

    private void ApplyAlwaysOnTop()
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _settings.AlwaysOnTop;
        }
    }

    private void ApplyOpacity()
    {
        if (_hwnd == 0)
        {
            return;
        }

        var opacity = _settings.Opacity;
        opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, MinOpacity, 1.0) : 1.0;

        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        if (opacity < 1.0)
        {
            if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
            {
                NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_LAYERED);
            }

            var alpha = (byte)Math.Round(opacity * 255);
            if (!NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, alpha, NativeMethods.LWA_ALPHA))
            {
                Debug.WriteLine($"[WindowService] SetLayeredWindowAttributes failed ({Marshal.GetLastWin32Error()}).");
            }
        }
        else if ((exStyle & NativeMethods.WS_EX_LAYERED) != 0)
        {
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle & ~NativeMethods.WS_EX_LAYERED);
        }
    }

    private void ApplyStealth()
    {
        if (_hwnd == 0)
        {
            return;
        }

        var stealth = _settings.StealthMode;
        if (stealth)
        {
            if (!NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
            {
                if (NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_MONITOR))
                {
                    RaiseWarning("Stealth: this Windows version can only black out the window in captures");
                }
                else
                {
                    RaiseWarning($"Stealth: Windows refused to hide this window from screen capture (error {Marshal.GetLastWin32Error()}).");
                }
            }
        }
        else if (!NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_NONE))
        {
            Debug.WriteLine($"[WindowService] SetWindowDisplayAffinity(WDA_NONE) failed ({Marshal.GetLastWin32Error()}).");
        }

        if (_appWindow is not null)
        {
            try
            {
                _appWindow.IsShownInSwitchers = !stealth;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowService] IsShownInSwitchers failed: {ex}");
                if (stealth)
                {
                    RaiseWarning("Stealth: couldn't hide the window from the taskbar and Alt+Tab on this Windows version.");
                }
            }
        }
    }

    private void ApplyHotkeys()
    {
        if (_hwnd == 0 || !_subclassInstalled)
        {
            return;
        }

        var askText = _settings.AskHotkey?.Trim() ?? "";
        var toggleText = _settings.ToggleWindowHotkey?.Trim() ?? "";

        var duplicate = TryParseHotkey(askText, out var askModifiers, out var askVk)
            && TryParseHotkey(toggleText, out var toggleModifiers, out var toggleVk)
            && askModifiers == toggleModifiers
            && askVk == toggleVk;

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
    }

    private void RegisterHotkey(HotkeySlot slot, string text, bool suppressed)
    {
        slot.AppliedText = text;
        slot.AppliedSuppressed = suppressed;
        slot.Registered = false;

        if (IsNoneHotkey(text))
        {
            return;
        }

        if (!TryParseHotkey(text, out var modifiers, out var vk))
        {
            RaiseWarning($"Hotkey \"{text}\" isn't a valid key combination. Pick another in Settings.");
            return;
        }

        if (suppressed)
        {
            RaiseWarning($"The Ask and Show/Hide hotkeys are both {text}, so only Ask will work. Pick a different Show/Hide hotkey in Settings.");
            return;
        }

        if (!IsSafeGlobalHotkey(modifiers, vk))
        {
            RaiseWarning($"Hotkey {text} needs Ctrl, Alt or Win (or an F-key) to work as a global hotkey. Pick another in Settings.");
            return;
        }

        if (NativeMethods.RegisterHotKey(_hwnd, slot.Id, modifiers | NativeMethods.MOD_NOREPEAT, vk))
        {
            slot.Registered = true;
        }
        else
        {
            Debug.WriteLine($"[WindowService] RegisterHotKey({text}) failed ({Marshal.GetLastWin32Error()}).");
            RaiseWarning($"Hotkey {text} is already used by another app. Pick another in Settings.");
        }
    }

    private void UnregisterHotkey(HotkeySlot slot)
    {
        if (slot.Registered && _hwnd != 0)
        {
            NativeMethods.UnregisterHotKey(_hwnd, slot.Id);
        }

        slot.Reset();
    }

    private void ToggleVisibilityCore()
    {
        if (_appWindow is null || _hwnd == 0)
        {
            return;
        }

        var presenter = _appWindow.Presenter as OverlappedPresenter;
        var minimized = presenter?.State == OverlappedPresenterState.Minimized;

        if (_appWindow.IsVisible && !minimized)
        {
            _appWindow.Hide();
            return;
        }

        // A minimized window counts as "hidden" too: with stealth on it has no taskbar button to restore it from.
        if (!_appWindow.IsVisible)
        {
            _appWindow.Show();
        }

        if (minimized)
        {
            presenter?.Restore();
        }

        NativeMethods.SetForegroundWindow(_hwnd);
    }

    private void HookKeyboard()
    {
        var content = _nativeWindow?.Content;
        if (content is null || ReferenceEquals(content, _keyTarget))
        {
            return;
        }

        UnhookKeyboard();
        content.PreviewKeyDown += OnPreviewKeyDown;
        // handledEventsToo: scroll viewers mark wheel events handled before they bubble up to the root.
        _wheelHandler ??= OnPointerWheelChanged;
        content.AddHandler(Microsoft.UI.Xaml.UIElement.PointerWheelChangedEvent, _wheelHandler, handledEventsToo: true);
        _keyTarget = content;
    }

    private void UnhookKeyboard()
    {
        if (_keyTarget is not null)
        {
            _keyTarget.PreviewKeyDown -= OnPreviewKeyDown;
            if (_wheelHandler is not null)
            {
                _keyTarget.RemoveHandler(Microsoft.UI.Xaml.UIElement.PointerWheelChangedEvent, _wheelHandler);
            }
            _keyTarget = null;
        }
    }

    /// <summary>Ctrl + mouse wheel changes the app-wide font size (Settings.FontSize), one point per wheel notch.</summary>
    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(global::Windows.System.VirtualKeyModifiers.Control))
        {
            _zoomWheelDelta = 0;
            return;
        }

        var properties = e.GetCurrentPoint(sender as Microsoft.UI.Xaml.UIElement).Properties;
        if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0)
        {
            return;
        }

        e.Handled = true;

        // Accumulate partial notches; start over when the direction flips.
        var delta = properties.MouseWheelDelta;
        _zoomWheelDelta = Math.Sign(delta) == Math.Sign(_zoomWheelDelta) ? _zoomWheelDelta + delta : delta;
        var steps = _zoomWheelDelta / NativeMethods.WHEEL_DELTA;
        if (steps == 0)
        {
            return;
        }

        _zoomWheelDelta -= steps * NativeMethods.WHEEL_DELTA;
        var current = double.IsFinite(_settings.FontSize) ? Math.Round(_settings.FontSize) : FontSizeLimits.Min;
        var size = Math.Clamp(current + steps, FontSizeLimits.Min, FontSizeLimits.Max);
        if (size != _settings.FontSize)
        {
            _settings.FontSize = size;
        }
    }

    private void OnNativeWindowActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args) => HookKeyboard();

    private void OnNativeWindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        // Handled == true means another handler cancelled the close; the window stays alive.
        if (!args.Handled)
        {
            Detach();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!InterceptEnterKey || e.Handled)
        {
            return;
        }

        WindowCommand command;
        if (e.Key == VirtualKey.Enter)
        {
            command = WindowCommand.Ask;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            command = WindowCommand.Cancel;
        }
        else
        {
            return;
        }

        if (IsTextInputFocused())
        {
            return;
        }

        // Swallow auto-repeats too, so a held Enter can't keep invoking the focused button.
        e.Handled = true;
        if (!e.KeyStatus.WasKeyDown)
        {
            RaiseCommand(command);
        }
    }

    private bool IsTextInputFocused()
    {
        var xamlRoot = _keyTarget?.XamlRoot ?? _nativeWindow?.Content?.XamlRoot;
        if (xamlRoot is null)
        {
            return false;
        }

        // Walk up from the focused element: focus often sits on a template part inside the actual control.
        var node = FocusManager.GetFocusedElement(xamlRoot) as Microsoft.UI.Xaml.DependencyObject;
        while (node is not null)
        {
            switch (node)
            {
                case XamlControls.TextBox or XamlControls.PasswordBox or XamlControls.RichEditBox or XamlControls.AutoSuggestBox:
                    return true;
                case XamlControls.ComboBoxItem:
                    return true; // focus is inside an open drop-down
                case XamlControls.ComboBox comboBox:
                    return comboBox.IsDropDownOpen;
            }

            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private nint WindowSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        // Exceptions must never unwind into native code.
        try
        {
            if (uMsg == NativeMethods.WM_HOTKEY)
            {
                switch ((long)wParam)
                {
                    case AskHotkeyId:
                        Post(() => RaiseCommand(WindowCommand.Ask));
                        return 0;
                    case ToggleHotkeyId:
                        Post(ToggleVisibilityCore);
                        return 0;
                }
            }
            else if (uMsg == NativeMethods.WM_NCDESTROY)
            {
                Detach();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowService] Window message 0x{uMsg:X4} failed: {ex}");
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void Detach()
    {
        if (_nativeWindow is null && _hwnd == 0)
        {
            return;
        }

        _attached = false;
        _settings.PropertyChanged -= OnSettingsPropertyChanged;

        try
        {
            if (_nativeWindow is not null)
            {
                _nativeWindow.Closed -= OnNativeWindowClosed;
                _nativeWindow.Activated -= OnNativeWindowActivated;
            }

            UnhookKeyboard();
        }
        catch (Exception ex)
        {
            // The XAML tree may already be tearing down.
            Debug.WriteLine($"[WindowService] Unhooking window events failed: {ex}");
        }

        if (_hwnd != 0)
        {
            UnregisterHotkey(_askHotkey);
            UnregisterHotkey(_toggleHotkey);

            if (_subclassInstalled)
            {
                NativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
                _subclassInstalled = false;
            }
        }

        _keyTarget = null;
        _nativeWindow = null;
        _appWindow = null;
        _hwnd = 0;
    }

    /// <summary>Runs on the window's UI thread: inline when already there, otherwise queued.</summary>
    private void RunOnUi(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.HasThreadAccess)
        {
            Execute(action);
        }
        else
        {
            dispatcher.TryEnqueue(() => Execute(action));
        }
    }

    /// <summary>Always queues, so work triggered from inside the window procedure runs after it returns.</summary>
    private void Post(Action action) => _dispatcher?.TryEnqueue(() => Execute(action));

    private void Execute(Action action)
    {
        if (!_attached)
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
}
