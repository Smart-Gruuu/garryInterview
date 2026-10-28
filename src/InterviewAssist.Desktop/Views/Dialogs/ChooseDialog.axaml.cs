using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace InterviewAssist.Desktop.Views.Dialogs;

/// <summary>One button per option. <see cref="Result"/> is the chosen option, or null when cancelled.</summary>
public partial class ChooseDialog : UserControl, IDialogView
{
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ChooseDialog()
    {
        InitializeComponent();
    }

    public Task<string?> Result => _result.Task;

    public static ChooseDialog Create(string title, IReadOnlyList<string> options, string cancel)
    {
        var dialog = new ChooseDialog();
        dialog.TitleText.Text = title;
        dialog.CancelButton.Content = cancel;

        foreach (var option in options)
        {
            var button = new Button
            {
                Content = option,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => dialog._result.TrySetResult(option);
            dialog.OptionsPanel.Children.Add(button);
        }

        return dialog;
    }

    public void FocusInitialElement()
    {
        if (OptionsPanel.Children.Count > 0)
            OptionsPanel.Children[0].Focus();
        else
            CancelButton.Focus();
    }

    public void Cancel() => _result.TrySetResult(null);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => _result.TrySetResult(null);
}
