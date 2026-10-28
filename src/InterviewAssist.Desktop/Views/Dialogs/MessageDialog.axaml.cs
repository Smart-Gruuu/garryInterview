using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InterviewAssist.Desktop.Views.Dialogs;

/// <summary>An alert (no cancel text) or a confirmation. <see cref="Result"/> is true when accepted.</summary>
public partial class MessageDialog : UserControl, IDialogView
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _focusAccept;

    public MessageDialog()
    {
        InitializeComponent();
    }

    public Task<bool> Result => _result.Task;

    /// <param name="cancel">Null for an alert with only <paramref name="accept"/>.</param>
    public static MessageDialog Create(string title, string message, string accept, string? cancel)
    {
        var dialog = new MessageDialog();
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.MessageText.IsVisible = !string.IsNullOrEmpty(message);
        dialog.AcceptButton.Content = accept;
        dialog.CancelButton.Content = cancel;
        dialog.CancelButton.IsVisible = cancel is not null;

        // Confirmations here guard destructive actions, so Enter right away takes the safe choice.
        dialog._focusAccept = cancel is null;
        return dialog;
    }

    public void FocusInitialElement() => (_focusAccept ? AcceptButton : CancelButton).Focus();

    public void Cancel() => _result.TrySetResult(false);

    private void OnAcceptClick(object? sender, RoutedEventArgs e) => _result.TrySetResult(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => _result.TrySetResult(false);
}
