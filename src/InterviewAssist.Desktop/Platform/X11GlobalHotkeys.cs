using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Desktop.Platform;

/// <summary>
/// System-wide hotkeys on X11: passive key grabs on the root window (XGrabKey). Uses its own display connection,
/// owned by one background thread that also performs every grab, so nothing is shared with Avalonia's connection.
/// Under Wayland this goes through XWayland, which only delivers the keys while an X11 window has focus.
/// </summary>
public sealed class X11GlobalHotkeys : IDisposable
{
    private const int PollTimeoutMs = 100;
    private const int FallbackSleepMs = 25;
    private const uint RepeatWindowMs = 2000; // longer than any usual auto-repeat delay

    // NumLock (usually Mod2) and CapsLock change the event state, so every hotkey is grabbed with each combination.
    private static readonly uint[] LockModifierCombinations =
        [0, X11Native.LockMask, X11Native.Mod2Mask, X11Native.LockMask | X11Native.Mod2Mask];

    // The eight core modifier bits of an event state, minus the lock modifiers every hotkey is grabbed with.
    private const uint MatchedModifierMask = 0xFF & ~(X11Native.LockMask | X11Native.Mod2Mask);

    // The X error handler is process-wide; it routes errors for our connections here and chains everything else.
    private static readonly ConcurrentDictionary<nint, X11GlobalHotkeys> Instances = new();
    private static readonly object HandlerLock = new();
    private static nint s_previousErrorHandler;
    private static bool s_errorHandlerInstalled;

    private static readonly Lazy<bool> Available = new(DetectAvailability);

    private readonly IUiDispatcher _dispatcher;
    private readonly ConcurrentQueue<Action> _requests = new();
    private readonly object _threadLock = new();

    // Owned by the X thread.
    private readonly Dictionary<int, Registration> _registrations = [];
    private readonly Dictionary<uint, uint> _pressTimes = []; // keycode -> X time of its last press while held
    private nint _display;
    private nuint _rootWindow;
    private uint _lastReleasedKeycode;
    private uint _lastReleaseTime;
    private byte _grabErrorCode;
    private bool _pollUnavailable;

    private Thread? _thread;
    private volatile bool _stopping;

    public X11GlobalHotkeys(IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    /// <summary>True on Linux when an X server is reachable by name (DISPLAY is set) and libX11 can be loaded.</summary>
    public static bool IsAvailable => Available.Value;

    /// <summary>A registered hotkey (its id) was pressed. Raised on the UI thread.</summary>
    public event EventHandler<int>? Pressed;

    /// <summary>A hotkey couldn't be registered, or the X connection failed. User-facing message; UI thread.</summary>
    public event EventHandler<string>? Warning;

    /// <summary>
    /// Grabs <paramref name="hotkey"/> under <paramref name="id"/>, replacing any earlier hotkey with that id.
    /// Failures (e.g. another app already grabbed it) are reported through <see cref="Warning"/> using
    /// <paramref name="displayText"/>. Requests are applied in order on the X thread.
    /// </summary>
    public void Register(int id, Hotkey hotkey, string displayText)
    {
        ArgumentNullException.ThrowIfNull(displayText);
        Enqueue(() =>
        {
            UngrabRegistration(id);
            GrabRegistration(id, hotkey, displayText);
        });
    }

    public void Unregister(int id) => Enqueue(() => UngrabRegistration(id));

    public void Dispose()
    {
        Thread? thread;
        lock (_threadLock)
        {
            _stopping = true;
            thread = _thread;
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            // The thread notices the flag within one poll timeout, then ungrabs and closes its connection.
            thread.Join(TimeSpan.FromSeconds(1));
        }
    }

    private static bool DetectAvailability()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return false;
        }

        return NativeLibrary.TryLoad(X11Native.LibraryName, out _);
    }

    private void Enqueue(Action request)
    {
        lock (_threadLock)
        {
            if (_stopping)
            {
                return;
            }

            _requests.Enqueue(request);

            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "X11 global hotkeys" };
                _thread.Start();
            }
        }
    }

    private void Run()
    {
        try
        {
            if (!Open())
            {
                return;
            }

            var connection = X11Native.XConnectionNumber(_display);
            while (!_stopping)
            {
                ProcessRequests();
                ProcessEvents();
                WaitForInput(connection);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Debug.WriteLine($"[X11GlobalHotkeys] {ex}");
            PostWarning("Global hotkeys are unavailable: the X11 client library (libX11.so.6) couldn't be loaded.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[X11GlobalHotkeys] Hotkey thread failed: {ex}");
            PostWarning($"Global hotkeys stopped working: {ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    private bool Open()
    {
        // Must precede other Xlib calls in the process; Avalonia's X11 backend has normally done it already (no-op then).
        X11Native.XInitThreads();
        InstallErrorHandler();

        _display = X11Native.XOpenDisplay(0);
        if (_display == 0)
        {
            PostWarning("Global hotkeys are unavailable: couldn't connect to the X server.");
            return false;
        }

        Instances[_display] = this;
        // No XSelectInput: grabbed key events reach the grabbing client whatever its event mask, and selecting
        // KeyPress on the root would also deliver ungrabbed keys that propagate up to it.
        _rootWindow = X11Native.XDefaultRootWindow(_display);

        // Held keys then repeat as KeyPress only (no fake KeyRelease in between), which makes repeats easy to drop.
        // Without XKB the timestamp check in OnKeyPress still catches them.
        try
        {
            unsafe
            {
                int supported;
                X11Native.XkbSetDetectableAutoRepeat(_display, 1, &supported);
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            Debug.WriteLine($"[X11GlobalHotkeys] XkbSetDetectableAutoRepeat unavailable: {ex.Message}");
        }

        return true;
    }

    private void Close()
    {
        if (_display == 0)
        {
            return;
        }

        try
        {
            foreach (var id in _registrations.Keys.ToArray())
            {
                UngrabRegistration(id);
            }

            X11Native.XCloseDisplay(_display);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[X11GlobalHotkeys] Closing the display failed: {ex}");
        }
        finally
        {
            Instances.TryRemove(_display, out _);
            _display = 0;
        }
    }

    private void ProcessRequests()
    {
        while (_requests.TryDequeue(out var request))
        {
            try
            {
                request();
            }
            catch (Exception ex) when (ex is not DllNotFoundException and not EntryPointNotFoundException)
            {
                Debug.WriteLine($"[X11GlobalHotkeys] Request failed: {ex}");
            }
        }
    }

    private unsafe void ProcessEvents()
    {
        X11Native.XEvent ev;
        while (!_stopping && X11Native.XPending(_display) > 0)
        {
            X11Native.XNextEvent(_display, &ev);
            switch (ev.type)
            {
                case X11Native.KeyPress:
                    OnKeyPress(ev.xkey);
                    break;
                case X11Native.KeyRelease:
                    _pressTimes.Remove(ev.xkey.keycode);
                    _lastReleasedKeycode = ev.xkey.keycode;
                    _lastReleaseTime = (uint)ev.xkey.time;
                    break;
            }
        }
    }

    private void OnKeyPress(in X11Native.XKeyEvent key)
    {
        // Exact match, as the server does for the grab (button and group bits are outside the core modifiers).
        var state = key.state & MatchedModifierMask;
        var keycode = key.keycode;
        int? matchedId = null;
        foreach (var (id, registration) in _registrations)
        {
            if (registration.Modifiers == state && registration.Keycodes.Contains((byte)keycode))
            {
                matchedId = id;
                break;
            }
        }

        if (matchedId is not { } hotkeyId)
        {
            return;
        }

        // Auto-repeat: another press shortly after the last one with no release in between (detectable
        // auto-repeat), or a press with the same timestamp as the release just before it (without it).
        // The time limit means a release we never saw can't block the hotkey for good. X time is 32-bit ms.
        var time = (uint)key.time;
        var repeat = (keycode == _lastReleasedKeycode && time == _lastReleaseTime)
            || (_pressTimes.TryGetValue(keycode, out var lastPress) && unchecked(time - lastPress) < RepeatWindowMs);
        _pressTimes[keycode] = time;

        if (!repeat)
        {
            _dispatcher.Post(() => RaisePressed(hotkeyId));
        }
    }

    private void WaitForInput(int connection)
    {
        if (!_pollUnavailable)
        {
            try
            {
                var fd = new X11Native.PollFd { fd = connection, events = X11Native.POLLIN };
                unsafe
                {
                    // EINTR and timeouts just go round the loop again.
                    X11Native.poll(&fd, 1, PollTimeoutMs);
                }

                return;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Debug.WriteLine($"[X11GlobalHotkeys] poll() unavailable, falling back to sleeping: {ex.Message}");
                _pollUnavailable = true;
            }
        }

        Thread.Sleep(FallbackSleepMs);
    }

    private void GrabRegistration(int id, Hotkey hotkey, string displayText)
    {
        var keysymNames = HotkeyParser.GetX11KeysymNames(hotkey.VirtualKey);
        var keycodes = new List<byte>(keysymNames.Count);
        foreach (var name in keysymNames)
        {
            var keysym = X11Native.XStringToKeysym(name);
            var keycode = keysym == X11Native.NoSymbol ? (byte)0 : X11Native.XKeysymToKeycode(_display, keysym);
            if (keycode != 0 && !keycodes.Contains(keycode))
            {
                keycodes.Add(keycode);
            }
            else if (keycode == 0 && keycodes.Count == 0)
            {
                // The main key isn't on this keyboard layout; optional extras (numpad Enter) may be missing silently.
                PostWarning($"Hotkey {displayText} isn't available on this keyboard layout. Pick another in Settings.");
                return;
            }
        }

        if (keycodes.Count == 0)
        {
            PostWarning($"Hotkey \"{displayText}\" isn't a valid key combination. Pick another in Settings.");
            return;
        }

        var registration = new Registration(HotkeyParser.ToX11Modifiers(hotkey.Modifiers), keycodes);

        // Errors arrive asynchronously; XSync makes the server answer now, calling OnXError on this thread.
        _grabErrorCode = 0;
        ForEachGrab(registration, (keycode, modifiers) => X11Native.XGrabKey(
            _display, keycode, modifiers, _rootWindow, 0, X11Native.GrabModeAsync, X11Native.GrabModeAsync));
        X11Native.XSync(_display, 0);

        if (_grabErrorCode != 0)
        {
            // Release the combinations that did succeed; XUngrabKey never touches other clients' grabs.
            ForEachGrab(registration, (keycode, modifiers) => X11Native.XUngrabKey(_display, keycode, modifiers, _rootWindow));
            X11Native.XSync(_display, 0);

            PostWarning(_grabErrorCode == X11Native.BadAccess
                ? $"Hotkey {displayText} is already used by another app. Pick another in Settings."
                : $"Hotkey {displayText} couldn't be registered (X error {_grabErrorCode}). Pick another in Settings.");
            return;
        }

        _registrations[id] = registration;
    }

    private void UngrabRegistration(int id)
    {
        if (!_registrations.Remove(id, out var registration))
        {
            return;
        }

        ForEachGrab(registration, (keycode, modifiers) => X11Native.XUngrabKey(_display, keycode, modifiers, _rootWindow));
        X11Native.XSync(_display, 0);
    }

    private static void ForEachGrab(Registration registration, Action<int, uint> action)
    {
        foreach (var keycode in registration.Keycodes)
        {
            foreach (var lockModifiers in LockModifierCombinations)
            {
                action(keycode, registration.Modifiers | lockModifiers);
            }
        }
    }

    private void OnGrabError(byte errorCode, byte requestCode)
    {
        if (requestCode == X11Native.X_GrabKey && _grabErrorCode == 0)
        {
            _grabErrorCode = errorCode;
        }
        else
        {
            Debug.WriteLine($"[X11GlobalHotkeys] X error {errorCode} for request {requestCode}.");
        }
    }

    private static unsafe void InstallErrorHandler()
    {
        lock (HandlerLock)
        {
            if (s_errorHandlerInstalled)
            {
                return;
            }

            delegate* unmanaged[Cdecl]<nint, X11Native.XErrorEvent*, int> handler = &OnXError;
            s_previousErrorHandler = X11Native.XSetErrorHandler((nint)handler);
            s_errorHandlerInstalled = true;
        }
    }

    // Called by Xlib on whichever thread received the error. Exceptions must never unwind into native code.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnXError(nint display, X11Native.XErrorEvent* error)
    {
        try
        {
            if (Instances.TryGetValue(display, out var instance))
            {
                instance.OnGrabError(error->error_code, error->request_code);
                return 0;
            }

            // Not our connection (e.g. Avalonia's): let the handler that was installed before us deal with it.
            var previous = s_previousErrorHandler;
            if (previous != 0)
            {
                return ((delegate* unmanaged[Cdecl]<nint, X11Native.XErrorEvent*, int>)previous)(display, error);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[X11GlobalHotkeys] X error handler failed: {ex}");
        }

        return 0;
    }

    private void RaisePressed(int id)
    {
        try
        {
            Pressed?.Invoke(this, id);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[X11GlobalHotkeys] Pressed handler failed: {ex}");
        }
    }

    private void PostWarning(string message)
    {
        Debug.WriteLine($"[X11GlobalHotkeys] {message}");
        _dispatcher.Post(() =>
        {
            try
            {
                Warning?.Invoke(this, message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[X11GlobalHotkeys] Warning handler failed: {ex}");
            }
        });
    }

    /// <summary>An applied grab: the X11 modifier mask and the keycodes (main key first) it covers.</summary>
    private sealed record Registration(uint Modifiers, IReadOnlyList<byte> Keycodes);
}

/// <summary>
/// Xlib interop for Linux x64 (LP64: C long / unsigned long are 64-bit). Declarations follow X11/Xlib.h,
/// X11/XKBlib.h and X11/X.h (libX11 / xorgproto); XID, Window, KeySym and Time are unsigned long (nuint),
/// Bool and Status are int, KeyCode is unsigned char.
/// </summary>
internal static class X11Native
{
    public const string LibraryName = "libX11.so.6";
    private const string LibC = "libc.so.6";

    // X11/X.h
    public const nuint NoSymbol = 0;
    public const int KeyPress = 2;
    public const int KeyRelease = 3;
    public const uint ShiftMask = 1 << 0;
    public const uint LockMask = 1 << 1;
    public const uint ControlMask = 1 << 2;
    public const uint Mod1Mask = 1 << 3;
    public const uint Mod2Mask = 1 << 4;
    public const uint Mod4Mask = 1 << 6;
    public const int GrabModeAsync = 1;
    public const byte BadAccess = 10;

    // X11/Xproto.h request opcode of GrabKey.
    public const byte X_GrabKey = 33;

    // poll.h
    public const short POLLIN = 0x001;

    /// <summary>XKeyEvent (Xlib.h). On LP64: state at offset 80, keycode at 84, size 96.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XKeyEvent
    {
        public int type;
        public nuint serial;
        public int send_event;
        public nint display;
        public nuint window;
        public nuint root;
        public nuint subwindow;
        public nuint time;
        public int x;
        public int y;
        public int x_root;
        public int y_root;
        public uint state;
        public uint keycode;
        public int same_screen;
    }

    /// <summary>The XEvent union (Xlib.h): its largest member is <c>long pad[24]</c>, i.e. 192 bytes on LP64.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24 * 8)]
    public struct XEvent
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(0)] public XKeyEvent xkey;
    }

    /// <summary>XErrorEvent (Xlib.h). On LP64: error_code at offset 32, request_code at 33.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XErrorEvent
    {
        public int type;
        public nint display;
        public nuint resourceid;
        public nuint serial;
        public byte error_code;
        public byte request_code;
        public byte minor_code;
    }

    /// <summary>struct pollfd (poll.h): int fd; short events; short revents.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    // Status XInitThreads(void);
    [DllImport(LibraryName)]
    public static extern int XInitThreads();

    // Display *XOpenDisplay(_Xconst char* display_name); NULL = $DISPLAY.
    [DllImport(LibraryName)]
    public static extern nint XOpenDisplay(nint displayName);

    // int XCloseDisplay(Display*);
    [DllImport(LibraryName)]
    public static extern int XCloseDisplay(nint display);

    // Window XDefaultRootWindow(Display*);
    [DllImport(LibraryName)]
    public static extern nuint XDefaultRootWindow(nint display);

    // KeySym XStringToKeysym(_Xconst char* string);
    [DllImport(LibraryName)]
    public static extern nuint XStringToKeysym([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    // KeyCode XKeysymToKeycode(Display*, KeySym);
    [DllImport(LibraryName)]
    public static extern byte XKeysymToKeycode(nint display, nuint keysym);

    // int XGrabKey(Display*, int keycode, unsigned int modifiers, Window grab_window, Bool owner_events,
    //              int pointer_mode, int keyboard_mode);
    [DllImport(LibraryName)]
    public static extern int XGrabKey(nint display, int keycode, uint modifiers, nuint grabWindow, int ownerEvents, int pointerMode, int keyboardMode);

    // int XUngrabKey(Display*, int keycode, unsigned int modifiers, Window grab_window);
    [DllImport(LibraryName)]
    public static extern int XUngrabKey(nint display, int keycode, uint modifiers, nuint grabWindow);

    // int XPending(Display*);
    [DllImport(LibraryName)]
    public static extern int XPending(nint display);

    // int XNextEvent(Display*, XEvent* event_return);
    [DllImport(LibraryName)]
    public static extern unsafe int XNextEvent(nint display, XEvent* eventReturn);

    // int XConnectionNumber(Display*);
    [DllImport(LibraryName)]
    public static extern int XConnectionNumber(nint display);

    // int XSync(Display*, Bool discard);
    [DllImport(LibraryName)]
    public static extern int XSync(nint display, int discard);

    // XErrorHandler XSetErrorHandler(XErrorHandler); typedef int (*XErrorHandler)(Display*, XErrorEvent*);
    [DllImport(LibraryName)]
    public static extern nint XSetErrorHandler(nint handler);

    // Bool XkbSetDetectableAutoRepeat(Display*, Bool detectable, Bool* supported);  (X11/XKBlib.h)
    [DllImport(LibraryName)]
    public static extern unsafe int XkbSetDetectableAutoRepeat(nint display, int detectable, int* supported);

    // int poll(struct pollfd *fds, nfds_t nfds, int timeout);  nfds_t is unsigned long.
    [DllImport(LibC, SetLastError = true)]
    public static extern unsafe int poll(PollFd* fds, nuint nfds, int timeout);
}
