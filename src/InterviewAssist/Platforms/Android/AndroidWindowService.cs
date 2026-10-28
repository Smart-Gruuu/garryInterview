using System.ComponentModel;
using System.Diagnostics;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;
using InterviewAssist.Presentation.Services;
using AndroidActivity = Android.App.Activity;
using MauiWindow = Microsoft.Maui.Controls.Window;
using WindowManagerFlags = Android.Views.WindowManagerFlags;

namespace InterviewAssist.Native;

/// <summary>
/// Android: only stealth is supported, via FLAG_SECURE (blocks screenshots, screen recording and casting).
/// Always-on-top, opacity, global hotkeys and hiding the window don't exist for a normal Android activity.
/// </summary>
public sealed class AndroidWindowService : IPlatformWindowService
{
    private readonly AppSettings _settings;
    private MauiWindow? _window;
    private bool _settingsHooked;

    public AndroidWindowService(ISettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        _settings = settingsService.Settings;
    }

    public bool SupportsAlwaysOnTop => false;
    public bool SupportsOpacity => false;
    public bool SupportsStealth => true;
    public bool SupportsGlobalHotkeys => false;

    public bool InterceptEnterKey { get; set; }

    // Nothing on Android raises these; empty accessors keep the contract without an "unused event" warning.
    public event EventHandler<WindowCommand>? CommandReceived
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? Warning
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Called by App, on the UI thread, once the MAUI window has its native handler. Applies the current settings
    /// and keeps applying them whenever they change.
    /// </summary>
    public void Attach(MauiWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!ReferenceEquals(_window, window))
        {
            if (_window is not null)
            {
                _window.Activated -= OnWindowActivated;
            }

            _window = window;
            // FLAG_SECURE lives on the activity's window; Activated (OnResume) also fires for a recreated activity.
            window.Activated += OnWindowActivated;
        }

        if (!_settingsHooked)
        {
            _settingsHooked = true;
            _settings.PropertyChanged += OnSettingsPropertyChanged;
        }

        ApplyStealth();
    }

    public void ToggleVisibility()
    {
        // An activity can't hide itself; the user switches apps instead.
    }

    private void OnWindowActivated(object? sender, EventArgs e) => ApplyStealth();

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.StealthMode) or null or "")
        {
            ApplyStealth();
        }
    }

    private void ApplyStealth()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var activity = _window?.Handler?.PlatformView as AndroidActivity
                    ?? Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
                var nativeWindow = activity?.Window;
                if (nativeWindow is null)
                {
                    return;
                }

                if (_settings.StealthMode)
                {
                    nativeWindow.AddFlags(WindowManagerFlags.Secure);
                }
                else
                {
                    nativeWindow.ClearFlags(WindowManagerFlags.Secure);
                }
            }
            catch (Exception ex)
            {
                // The activity may be finishing or destroyed.
                Debug.WriteLine($"[WindowService] Applying FLAG_SECURE failed: {ex}");
            }
        });
    }
}
