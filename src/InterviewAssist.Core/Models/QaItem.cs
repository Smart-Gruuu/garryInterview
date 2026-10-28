using CommunityToolkit.Mvvm.ComponentModel;

namespace InterviewAssist.Core.Models;

public enum QuestionSource
{
    /// <summary>Taken from the live transcript (Enter / hotkey / Ask button).</summary>
    Transcript,

    /// <summary>Typed by the user.</summary>
    Typed,
}

/// <summary>One interviewer question and the generated answer. Answer grows while streaming.
/// Mutate only on the UI thread (it is bound directly to the view).</summary>
public partial class QaItem : ObservableObject
{
    [ObservableProperty] private string _question = "";
    [ObservableProperty] private string _answer = "";
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private QuestionSource _source;
    [ObservableProperty] private DateTimeOffset _createdAt = DateTimeOffset.Now;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
}
