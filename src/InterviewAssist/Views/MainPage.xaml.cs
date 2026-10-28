using System.Diagnostics;
using InterviewAssist.Presentation.ViewModels;

namespace InterviewAssist.Views;

/// <summary>The Live tab. Scrolling and question-box keyboard behaviour live here; everything else is bound to <see cref="MainViewModel"/>.</summary>
public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private bool _answerScrollQueued;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;

        viewModel.AnswerReset += OnAnswerReset;
#if WINDOWS
        viewModel.DraftAppending += OnDraftAppending;
        viewModel.DraftAppended += OnDraftAppended;
        DraftEditor.HandlerChanged += OnDraftEditorHandlerChanged;
#endif
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.OnPageAppearing();
    }

    protected override void OnDisappearing()
    {
        _viewModel.OnPageDisappearing();
        base.OnDisappearing();
    }

    private void OnAnswerReset(object? sender, EventArgs e)
    {
        if (_answerScrollQueued)
        {
            return;
        }

        _answerScrollQueued = true;
        Dispatcher.Dispatch(ScrollAnswerToTop);
    }

    private async void ScrollAnswerToTop()
    {
        _answerScrollQueued = false;
        if (AnswerScroll.Handler is null || AnswerScroll.ScrollY <= 0)
        {
            return;
        }

        try
        {
            await AnswerScroll.ScrollToAsync(0, 0, false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainPage] Answer scroll failed: {ex}");
        }
    }

#if WINDOWS
    // How close to the bottom (in pixels) still counts as "following" the newest captions.
    private const double FollowTolerance = 4;

    private Microsoft.UI.Xaml.Controls.TextBox? _draftTextBox;
    private Microsoft.UI.Xaml.Controls.ScrollViewer? _draftScrollViewer;

    // Caret/selection saved right before captions are appended (null when the box isn't being edited).
    private (int Start, int Length)? _savedSelection;
    private bool _followDraftEnd = true;

    private void OnDraftEditorHandlerChanged(object? sender, EventArgs e)
    {
        if (_draftTextBox is not null)
        {
            _draftTextBox.PreviewKeyDown -= OnDraftPreviewKeyDown;
        }

        _draftTextBox = DraftEditor.Handler?.PlatformView as Microsoft.UI.Xaml.Controls.TextBox;
        _draftScrollViewer = null;
        if (_draftTextBox is not null)
        {
            _draftTextBox.PreviewKeyDown += OnDraftPreviewKeyDown;
        }
    }

    // Setting the text programmatically moves the caret to the end. Captions arrive while the user may be editing
    // in the middle, so remember where they were and put the caret/selection back afterwards.
    private void OnDraftAppending(object? sender, EventArgs e)
    {
        if (_draftTextBox is not { } textBox)
        {
            return;
        }

        var editing = textBox.FocusState != Microsoft.UI.Xaml.FocusState.Unfocused;
        _savedSelection = editing ? (textBox.SelectionStart, textBox.SelectionLength) : null;
        _followDraftEnd = DraftScrollViewer(textBox) is not { } viewer
            || viewer.VerticalOffset >= viewer.ScrollableHeight - FollowTolerance;
    }

    private void OnDraftAppended(object? sender, EventArgs e)
    {
        if (_draftTextBox is not { } textBox)
        {
            return;
        }

        if (_savedSelection is { } selection)
        {
            // New text only goes after the old end, so the saved positions still point at the same characters.
            var length = textBox.Text.Length;
            var start = Math.Min(selection.Start, length);
            textBox.Select(start, Math.Min(selection.Length, length - start));
            _savedSelection = null;
        }

        if (_followDraftEnd)
        {
            // After layout has measured the new text, keep the newest words in view.
            textBox.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (DraftScrollViewer(textBox) is { } viewer)
                {
                    viewer.ChangeView(null, viewer.ScrollableHeight, null, disableAnimation: true);
                }
            });
        }
    }

    private Microsoft.UI.Xaml.Controls.ScrollViewer? DraftScrollViewer(Microsoft.UI.Xaml.Controls.TextBox textBox) =>
        _draftScrollViewer ??= FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(textBox);

    private static T? FindDescendant<T>(Microsoft.UI.Xaml.DependencyObject root) where T : Microsoft.UI.Xaml.DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // In the question box, Enter sends (like everywhere else on this page) and Shift+Enter inserts a line break.
    private void OnDraftPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown)
        {
            return;
        }

        switch (e.Key)
        {
            case global::Windows.System.VirtualKey.Enter:
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Shift);
                if (shift.HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down))
                {
                    return;
                }

                e.Handled = true;
                if (_viewModel.AskCommand.CanExecute(null))
                {
                    _viewModel.AskCommand.Execute(null);
                }
                break;

            case global::Windows.System.VirtualKey.Escape when _viewModel.IsGenerating:
                e.Handled = true;
                _viewModel.CancelCommand.Execute(null);
                break;
        }
    }
#endif
}
