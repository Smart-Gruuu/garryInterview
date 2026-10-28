using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using InterviewAssist.Core.Models;
using InterviewAssist.Presentation.ViewModels;

namespace InterviewAssist.Desktop.Views;

/// <summary>
/// The Live tab. Scrolling and question-box keyboard behaviour live here; everything else is bound to
/// <see cref="MainViewModel"/>. MainWindow calls OnPageAppearing/OnPageDisappearing as the tab is selected.
/// </summary>
public partial class LiveView : UserControl
{
    // How close to the bottom (in pixels) still counts as "following" the newest captions.
    private const double FollowTolerance = 4;

    private static readonly GridLength TranscriptRowHeight = new(0.55, GridUnitType.Star);

    private readonly MainViewModel? _viewModel;
    private ScrollViewer? _draftScrollViewer;

    // Caret/selection saved right before captions are appended (null when the box isn't being edited).
    private (int Caret, int Start, int End)? _savedSelection;
    private bool _followDraftEnd = true;
    private bool _answerScrollQueued;

    // Avalonia reports auto-repeat as more KeyDowns without a KeyUp; a held Enter must not ask again.
    private bool _enterDown;

    /// <summary>For the XAML designer and runtime loader only.</summary>
    public LiveView()
    {
        InitializeComponent();
    }

    public LiveView(MainViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.AnswerReset += OnAnswerReset;
        viewModel.DraftAppending += OnDraftAppending;
        viewModel.DraftAppended += OnDraftAppended;
        viewModel.Settings.PropertyChanged += OnSettingsPropertyChanged;
        UpdateTranscriptRow();

        DraftEditor.TemplateApplied += OnDraftEditorTemplateApplied;
        DraftEditor.AddHandler(KeyDownEvent, OnDraftKeyDown, RoutingStrategies.Tunnel);
        DraftEditor.LostFocus += (_, _) => _enterDown = false;
        TypedQuestionBox.AddHandler(KeyDownEvent, OnTypedQuestionKeyDown, RoutingStrategies.Tunnel);
        TypedQuestionBox.LostFocus += (_, _) => _enterDown = false;
        AddHandler(KeyUpEvent, OnKeyUpTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AppSettings.ShowTranscript) or null or ""))
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateTranscriptRow();
        }
        else
        {
            Dispatcher.UIThread.Post(UpdateTranscriptRow);
        }
    }

    // A hidden transcript gives its whole row to the answer card.
    private void UpdateTranscriptRow()
    {
        if (_viewModel is not null)
        {
            RootGrid.RowDefinitions[1].Height = _viewModel.Settings.ShowTranscript ? TranscriptRowHeight : new GridLength(0);
        }
    }

    private void OnAnswerReset(object? sender, EventArgs e)
    {
        if (_answerScrollQueued)
        {
            return;
        }

        _answerScrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _answerScrollQueued = false;
            if (AnswerScroll.Offset.Y > 0)
            {
                AnswerScroll.ScrollToHome();
            }
        });
    }

    private void OnDraftEditorTemplateApplied(object? sender, TemplateAppliedEventArgs e) =>
        _draftScrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");

    // Captions arrive while the user may be editing in the middle, so remember where the caret/selection was and put
    // it back after the text is replaced.
    private void OnDraftAppending(object? sender, EventArgs e)
    {
        var box = DraftEditor;
        _savedSelection = box.IsFocused ? (box.CaretIndex, box.SelectionStart, box.SelectionEnd) : null;
        _followDraftEnd = _draftScrollViewer is not { } viewer
            || viewer.Offset.Y >= viewer.ScrollBarMaximum.Y - FollowTolerance;
    }

    private void OnDraftAppended(object? sender, EventArgs e)
    {
        var box = DraftEditor;
        if (_savedSelection is { } saved)
        {
            // New text only goes after the old end, so the saved positions still point at the same characters.
            // The caret goes first: setting it collapses the selection.
            var length = box.Text?.Length ?? 0;
            box.CaretIndex = Math.Min(saved.Caret, length);
            box.SelectionStart = Math.Min(saved.Start, length);
            box.SelectionEnd = Math.Min(saved.End, length);
            _savedSelection = null;
        }

        if (_followDraftEnd && _draftScrollViewer is { } viewer)
        {
            // After layout has measured the new text, keep the newest words in view.
            Dispatcher.UIThread.Post(viewer.ScrollToEnd, DispatcherPriority.Background);
        }
    }

    // In the question box, Enter sends and Shift+Enter inserts a line break (the TextBox handles that itself).
    private void OnDraftKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when (e.KeyModifiers & KeyModifiers.Shift) == 0:
                e.Handled = true;
                if (!_enterDown)
                {
                    _enterDown = true;
                    if (_viewModel.AskCommand.CanExecute(null))
                    {
                        _viewModel.AskCommand.Execute(null);
                    }
                }

                break;

            case Key.Escape when _viewModel.IsGenerating:
                e.Handled = true;
                _viewModel.CancelCommand.Execute(null);
                break;
        }
    }

    private void OnTypedQuestionKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (!_enterDown)
        {
            _enterDown = true;
            if (_viewModel.SubmitTypedCommand.CanExecute(null))
            {
                _viewModel.SubmitTypedCommand.Execute(null);
            }
        }
    }

    private void OnKeyUpTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _enterDown = false;
        }
    }
}
