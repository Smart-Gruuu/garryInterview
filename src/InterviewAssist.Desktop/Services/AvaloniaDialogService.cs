using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using InterviewAssist.Desktop.Views.Dialogs;
using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Desktop.Services;

/// <summary>
/// Modal dialogs drawn inside the main window (like MAUI's DisplayAlert on Windows) rather than as separate windows,
/// so they share the window's always-on-top, opacity and capture exclusion, and work while the window is hidden.
/// One dialog is shown at a time; later requests wait their turn.
/// </summary>
public sealed class AvaloniaDialogService : IDialogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Panel? _layer;
    private IDialogView? _current;

    /// <summary>
    /// Shows dialogs in <paramref name="layer"/>, an initially hidden panel covering <paramref name="window"/>'s
    /// content. Call before anything else adds a tunnelling key handler to the window, so Enter and Escape reach an
    /// open dialog first.
    /// </summary>
    public void Attach(Window window, Panel layer)
    {
        ArgumentNullException.ThrowIfNull(window);
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _layer.IsVisible = false;
        window.AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string accept, string cancel = "Cancel")
    {
        var dialog = MessageDialog.Create(title, message, accept, cancel);
        return await ShowAsync(dialog, dialog.Result, cancelled: false);
    }

    public Task AlertAsync(string title, string message, string ok = "OK")
    {
        var dialog = MessageDialog.Create(title, message, ok, cancel: null);
        return ShowAsync(dialog, dialog.Result, cancelled: false);
    }

    public Task<string?> ChooseAsync(string title, IReadOnlyList<string> options, string cancel = "Cancel")
    {
        ArgumentNullException.ThrowIfNull(options);
        var dialog = ChooseDialog.Create(title, options, cancel);
        return ShowAsync(dialog, dialog.Result, cancelled: null);
    }

    private async Task<T> ShowAsync<T>(Control dialog, Task<T> result, T cancelled)
    {
        if (_layer is not { } layer)
            return cancelled;

        await _gate.WaitAsync();
        var focusManager = TopLevel.GetTopLevel(layer)?.FocusManager;
        var previousFocus = focusManager?.GetFocusedElement();
        try
        {
            _current = (IDialogView)dialog;
            layer.Children.Add(dialog);
            layer.IsVisible = true;
            _current.FocusInitialElement();

            return await result;
        }
        finally
        {
            _current = null;
            layer.Children.Remove(dialog);
            layer.IsVisible = false;

            if (previousFocus is Visual { } visual && visual.IsAttachedToVisualTree())
                previousFocus.Focus();

            _gate.Release();
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_current is not { } dialog || e.Handled)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                dialog.Cancel();
                break;

            // Handled here so the window's Enter-to-ask shortcut can't see it; the focused dialog button acts instead.
            case Key.Enter:
                e.Handled = true;
                if (e.Source is Button { IsEffectivelyEnabled: true } button && IsInDialog(button))
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                break;
        }
    }

    private bool IsInDialog(Visual element) => _layer is { } layer && layer.IsVisualAncestorOf(element);
}
