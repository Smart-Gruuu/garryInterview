namespace InterviewAssist.Desktop.Views.Dialogs;

/// <summary>A dialog shown over the main window by <see cref="Services.AvaloniaDialogService"/>.</summary>
public interface IDialogView
{
    /// <summary>Moves keyboard focus into the dialog once it is on screen.</summary>
    void FocusInitialElement();

    /// <summary>Closes the dialog as cancelled (Escape).</summary>
    void Cancel();
}
