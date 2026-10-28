using InterviewAssist.Core.Chat;
using InterviewAssist.Core.Models;
using InterviewAssist.Core.Transcription;
using InterviewAssist.Presentation.Services;
using InterviewAssist.Presentation.Tests.Fakes;
using InterviewAssist.Presentation.ViewModels;

namespace InterviewAssist.Presentation.Tests;

public class MainViewModelTests
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private readonly ManualUiDispatcher _dispatcher = new();
    private readonly FakeSettingsService _settingsService = new();
    private readonly FakeAudioCaptureService _audio = new();
    private readonly FakeTranscriptionClient _transcription = new();
    private readonly TranscriptStore _store = new();
    private readonly FakeChatClient _chat = new();
    private readonly FakeWindowService _window = new();
    private readonly FakeNavigationService _navigation = new();
    private readonly FakeClipboardService _clipboard = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly MainViewModel _vm;

    public MainViewModelTests()
    {
        Settings.DeepgramApiKey = "dg-key";
        Settings.OpenAiApiKey = "sk-test";
        _vm = new MainViewModel(
            _settingsService, _audio, _transcription, _store, _chat, new PromptBuilder(),
            _window, _dispatcher, _navigation, _clipboard, _dialogs);
    }

    private AppSettings Settings => _settingsService.Settings;

    private void Final(string text, double start, double end) =>
        _transcription.RaiseResult(new TranscriptResult(1, text, IsFinal: true, SpeechFinal: false, FromFinalize: false, start, end - start));

    private void Interim(string text, double start, double end) =>
        _transcription.RaiseResult(new TranscriptResult(1, text, IsFinal: false, SpeechFinal: false, FromFinalize: false, start, end - start));

    /// <summary>Lets the throttled transcript refresh run.</summary>
    private void Refresh() => _dispatcher.Advance(RefreshInterval);

    private Task StartListeningAsync() => _vm.ToggleListeningCommand.ExecuteAsync(null);

    private Task AskTypedAsync(string question)
    {
        _vm.TypedQuestion = question;
        return _vm.SubmitTypedCommand.ExecuteAsync(null);
    }

    private static string LastUserMessage(ChatRequest request) => request.Messages[^1].Content;

    // ---- listening ----

    [Fact]
    public async Task Start_connects_transcription_then_audio_and_shows_listening()
    {
        Settings.DeepgramApiKey = "  dg-key  ";
        Settings.CustomVocabulary = "Kubernetes, gRPC; kubernetes\nKafka";
        Settings.AudioSource = AudioSourceKind.Microphone;
        Settings.AudioDeviceId = "mic-1";

        await StartListeningAsync();

        var options = Assert.Single(_transcription.Starts);
        Assert.Equal("dg-key", options.ApiKey);
        Assert.Equal(Settings.DeepgramModel, options.Model);
        Assert.Equal(Settings.TranscriptionLanguage, options.Language);
        Assert.Equal(Settings.EndpointingMs, options.EndpointingMs);
        Assert.Equal(["Kubernetes", "gRPC", "Kafka"], options.KeyTerms);
        Assert.Equal([(AudioSourceKind.Microphone, (string?)"mic-1")], _audio.Starts);

        Assert.True(_vm.IsListening);
        Assert.False(_vm.IsBusyStarting);
        Assert.Equal(StatusKind.Listening, _vm.StatusKind);
        Assert.Equal("Listening", _vm.StatusText);
        Assert.Equal("■ Stop", _vm.ListenButtonText);
        Assert.Equal("00:00", _vm.ElapsedText);
    }

    [Fact]
    public async Task Audio_is_forwarded_and_the_level_meter_rises_fast_and_falls_gently()
    {
        await StartListeningAsync();

        _audio.RaiseChunk([1, 2, 3, 4], 0.5f);
        Assert.Equal([1, 2, 3, 4], Assert.Single(_transcription.SentAudio));

        _dispatcher.Advance(RefreshInterval); // -6 dB
        Assert.Equal(0.98, _vm.AudioLevel);

        _dispatcher.Advance(RefreshInterval); // silence: decays by 40 % per tick
        Assert.Equal(0.59, _vm.AudioLevel);
    }

    [Fact]
    public async Task Stop_stops_audio_and_transcription_and_returns_to_ready()
    {
        await StartListeningAsync();

        await _vm.ToggleListeningCommand.ExecuteAsync(null);

        Assert.False(_vm.IsListening);
        Assert.Equal(1, _audio.StopCount);
        Assert.Equal(1, _transcription.StopCount);
        Assert.Equal(StatusKind.Idle, _vm.StatusKind);
        Assert.Equal("Ready", _vm.StatusText);
        Assert.Equal("▶ Start", _vm.ListenButtonText);
        Assert.Equal(0, _vm.AudioLevel);
        Assert.Equal("", _vm.ElapsedText);

        _audio.RaiseChunk([1, 2], 0.5f);
        Assert.Empty(_transcription.SentAudio);
    }

    [Fact]
    public async Task Start_without_a_deepgram_key_reports_an_error_and_connects_nothing()
    {
        Settings.DeepgramApiKey = "  ";

        await StartListeningAsync();

        Assert.False(_vm.IsListening);
        Assert.Empty(_transcription.Starts);
        Assert.Empty(_audio.Starts);
        Assert.Equal(StatusKind.Error, _vm.StatusKind);
        Assert.Equal("Add your Deepgram API key in Settings", _vm.StatusText);
        Assert.Equal("Add your Deepgram API key in Settings", _vm.ToastText);
    }

    [Fact]
    public async Task Rejected_connection_shows_the_message_and_never_starts_audio()
    {
        _transcription.StartException = new InvalidOperationException("Deepgram rejected the API key.");

        await StartListeningAsync();

        Assert.False(_vm.IsListening);
        Assert.False(_vm.IsBusyStarting);
        Assert.Empty(_audio.Starts);
        Assert.Equal(1, _transcription.StopCount);
        Assert.Equal(StatusKind.Error, _vm.StatusKind);
        Assert.Equal("Deepgram rejected the API key.", _vm.StatusText);
    }

    [Fact]
    public async Task Unexpected_connection_failure_is_prefixed()
    {
        _transcription.StartException = new HttpRequestException("No such host");

        await StartListeningAsync();

        Assert.Equal("Could not connect to Deepgram: No such host", _vm.StatusText);
    }

    [Fact]
    public async Task Audio_start_failure_stops_transcription()
    {
        _audio.StartException = new InvalidOperationException("Microphone permission was denied.");

        await StartListeningAsync();

        Assert.False(_vm.IsListening);
        Assert.Single(_transcription.Starts);
        Assert.Equal(1, _transcription.StopCount);
        Assert.Equal(StatusKind.Error, _vm.StatusKind);
        Assert.Equal("Microphone permission was denied.", _vm.StatusText);

        _audio.RaiseChunk([1, 2], 0.5f);
        Assert.Empty(_transcription.SentAudio);
    }

    [Fact]
    public async Task Audio_error_while_listening_stops_and_keeps_the_error_visible()
    {
        await StartListeningAsync();

        _audio.RaiseError("The audio device was unplugged.");
        Assert.True(_vm.IsListening); // marshalled to the UI thread
        _dispatcher.Drain();

        Assert.False(_vm.IsListening);
        Assert.Equal(1, _audio.StopCount);
        Assert.Equal(1, _transcription.StopCount);
        Assert.Equal(StatusKind.Error, _vm.StatusKind);
        Assert.Equal("The audio device was unplugged.", _vm.StatusText);
    }

    [Fact]
    public async Task Connection_state_changes_update_the_status_while_listening()
    {
        await StartListeningAsync();

        _transcription.RaiseState(TranscriptionState.Reconnecting);
        _dispatcher.Drain();
        Assert.Equal(StatusKind.Reconnecting, _vm.StatusKind);
        Assert.Equal("Reconnecting…", _vm.StatusText);

        _transcription.RaiseState(TranscriptionState.Connected);
        _dispatcher.Drain();
        Assert.Equal(StatusKind.Listening, _vm.StatusKind);

        _transcription.RaiseState(TranscriptionState.Error);
        _dispatcher.Drain();
        Assert.False(_vm.IsListening);
        Assert.Equal(StatusKind.Error, _vm.StatusKind);
        Assert.Equal("Lost connection to Deepgram", _vm.StatusText);
    }

    [Fact]
    public async Task Transcription_error_on_a_live_stream_is_only_a_toast()
    {
        await StartListeningAsync();

        _transcription.RaiseError("Deepgram is slow right now.");
        _dispatcher.Drain();

        Assert.True(_vm.IsListening);
        Assert.Equal(StatusKind.Listening, _vm.StatusKind);
        Assert.Equal("Deepgram is slow right now.", _vm.ToastText);
    }

    // ---- captions → question box ----

    [Fact]
    public void Final_captions_are_appended_to_the_question_draft_on_the_ui_thread()
    {
        var events = new List<string>();
        _vm.DraftAppending += (_, _) => events.Add($"appending:{_vm.QuestionDraft}");
        _vm.DraftAppended += (_, _) => events.Add($"appended:{_vm.QuestionDraft}");

        Final("So, tell me", 0, 1);
        Assert.Equal("", _vm.QuestionDraft);

        Refresh();
        Final("about yourself.", 1, 2);
        Interim("And why", 2, 3);
        Refresh();

        Assert.Equal("So, tell me about yourself.", _vm.QuestionDraft);
        Assert.Equal("And why", _vm.LiveText);
        Assert.True(_vm.HasPendingQuestion);
        Assert.Equal(
            ["appending:", "appended:So, tell me", "appending:So, tell me", "appended:So, tell me about yourself."],
            events);
    }

    [Fact]
    public void Cleared_transcript_does_not_come_back()
    {
        Final("Small talk.", 0, 1);
        Refresh();

        _vm.ClearTranscriptCommand.Execute(null);
        Final("Real question?", 1, 2);
        Refresh();

        Assert.Equal("Real question?", _vm.QuestionDraft);
    }

    // ---- asking ----

    [Fact]
    public async Task Ask_sends_the_edited_draft_plus_interim_and_streams_the_answer_into_a_new_item()
    {
        var resets = 0;
        _vm.AnswerReset += (_, _) => resets++;
        _chat.Replies.Enqueue(FakeChatReply.Text("Because ", "I love ", "building things."));

        Final("Nice weather today.", 0, 1);
        Final("Why do you want this job?", 1, 3);
        Refresh();
        _vm.QuestionDraft = "Why do you want this job?"; // the user deleted the small talk
        Interim("And why now", 3, 4);
        Refresh();

        await _vm.AskCommand.ExecuteAsync(null);

        var item = Assert.Single(_vm.Answers);
        Assert.Equal("Why do you want this job? And why now", item.Question);
        Assert.Equal(QuestionSource.Transcript, item.Source);
        Assert.Equal("Because I love building things.", item.Answer);
        Assert.False(item.IsStreaming);
        Assert.False(item.HasError);
        Assert.Same(item, _vm.CurrentItem);
        Assert.Equal("1 / 1", _vm.PositionText);
        Assert.False(_vm.IsGenerating);
        Assert.Equal(1, resets);

        var request = Assert.Single(_chat.Requests);
        Assert.Equal("sk-test", request.ApiKey);
        Assert.EndsWith("Why do you want this job? And why now", LastUserMessage(request));

        // The box and the live line are emptied, and the final for the sent interim is not added again.
        Assert.Equal("", _vm.QuestionDraft);
        Assert.Equal("", _vm.LiveText);
        Final("And why now?", 3, 4.1);
        Refresh();
        Assert.Equal("", _vm.QuestionDraft);
    }

    [Fact]
    public async Task Ask_with_nothing_pending_only_shows_a_toast()
    {
        await _vm.AskCommand.ExecuteAsync(null);

        Assert.Empty(_vm.Answers);
        Assert.Empty(_chat.Requests);
        Assert.Equal("Nothing new in the transcript yet", _vm.ToastText);
    }

    [Fact]
    public async Task Ask_while_listening_finalizes_first_so_the_last_words_are_included()
    {
        await StartListeningAsync();
        Final("Tell me about", 0, 1);
        _transcription.OnFinalize = () =>
        {
            Final("your biggest weakness.", 1, 2);
            return Task.FromResult(true);
        };

        await _vm.AskCommand.ExecuteAsync(null);

        Assert.Equal(1, _transcription.FinalizeCount);
        Assert.Equal("Tell me about your biggest weakness.", Assert.Single(_vm.Answers).Question);
    }

    [Fact]
    public async Task Ask_does_not_finalize_when_turned_off()
    {
        Settings.FinalizeOnAsk = false;
        await StartListeningAsync();
        _vm.QuestionDraft = "Any question";

        await _vm.AskCommand.ExecuteAsync(null);

        Assert.Equal(0, _transcription.FinalizeCount);
        Assert.Single(_vm.Answers);
    }

    [Fact]
    public async Task Second_enter_right_after_an_answer_started_is_ignored()
    {
        _chat.Replies.Enqueue(FakeChatReply.Hang("Partial"));
        _vm.QuestionDraft = "First question";
        var first = _vm.AskCommand.ExecuteAsync(null);
        Assert.True(_vm.IsGenerating);

        _vm.QuestionDraft = "Second question";
        await _vm.AskCommand.ExecuteAsync(null);

        Assert.Single(_vm.Answers);
        Assert.Single(_chat.Requests);
        Assert.Equal("Second question", _vm.QuestionDraft);
        Assert.True(_vm.IsGenerating);

        _vm.CancelCommand.Execute(null);
        await first;
    }

    [Fact]
    public async Task Enter_while_the_previous_enter_is_still_finalizing_is_ignored()
    {
        await StartListeningAsync();
        var finalize = new TaskCompletionSource<bool>();
        _transcription.OnFinalize = () => finalize.Task;
        _vm.QuestionDraft = "Walk me through your resume";

        var first = _vm.AskCommand.ExecuteAsync(null);
        await _vm.AskCommand.ExecuteAsync(null);
        Assert.Equal(1, _transcription.FinalizeCount);

        finalize.SetResult(true);
        await first;

        Assert.Equal("Walk me through your resume", Assert.Single(_vm.Answers).Question);
        Assert.Single(_chat.Requests);
    }

    [Fact]
    public async Task Cancel_stops_the_answer_and_keeps_the_partial_text()
    {
        _chat.Replies.Enqueue(FakeChatReply.Hang("Hello ", "world"));
        var answering = AskTypedAsync("Say hello");
        var item = Assert.Single(_vm.Answers);
        Assert.True(_vm.IsGenerating);
        Assert.True(item.IsStreaming);

        _vm.CancelCommand.Execute(null);

        Assert.False(_vm.IsGenerating);
        Assert.False(item.IsStreaming);
        Assert.Equal("Hello world", item.Answer);

        await answering;
        Assert.True(_chat.Tokens[0].IsCancellationRequested);
        Assert.Equal("Hello world", item.Answer);
        Assert.False(item.HasError);
    }

    [Fact]
    public async Task A_new_question_supersedes_the_running_answer_which_keeps_its_partial_text()
    {
        _chat.Replies.Enqueue(FakeChatReply.Hang("Half an "));
        _chat.Replies.Enqueue(FakeChatReply.Text("Full answer."));
        var first = AskTypedAsync("First");

        await AskTypedAsync("Second");
        await first;

        Assert.Equal(2, _vm.Answers.Count);
        Assert.Equal("Half an ", _vm.Answers[0].Answer);
        Assert.False(_vm.Answers[0].HasError);
        Assert.Equal("Full answer.", _vm.Answers[1].Answer);
        Assert.Same(_vm.Answers[1], _vm.CurrentItem);
        Assert.False(_vm.IsGenerating);
    }

    [Fact]
    public async Task Typed_question_is_trimmed_answered_and_cleared()
    {
        await AskTypedAsync("  Tell me about Kafka  ");

        var item = Assert.Single(_vm.Answers);
        Assert.Equal("Tell me about Kafka", item.Question);
        Assert.Equal(QuestionSource.Typed, item.Source);
        Assert.Equal("An answer.", item.Answer);
        Assert.Equal("", _vm.TypedQuestion);
    }

    [Fact]
    public async Task Empty_typed_question_asks_from_the_transcript()
    {
        _vm.QuestionDraft = "From the box";

        await AskTypedAsync("   ");

        var item = Assert.Single(_vm.Answers);
        Assert.Equal("From the box", item.Question);
        Assert.Equal(QuestionSource.Transcript, item.Source);
    }

    [Fact]
    public async Task Missing_openai_key_adds_an_error_item_without_calling_the_api()
    {
        Settings.OpenAiApiKey = "";

        await AskTypedAsync("What is a monad?");

        var item = Assert.Single(_vm.Answers);
        Assert.Equal("Add your OpenAI API key in Settings", item.ErrorMessage);
        Assert.True(item.HasError);
        Assert.Empty(_chat.Requests);
        Assert.False(_vm.IsGenerating);
    }

    [Fact]
    public async Task Api_errors_are_shown_on_the_item_after_the_partial_text()
    {
        _chat.Replies.Enqueue(FakeChatReply.Fail(new ChatClientException("OpenAI rate limit reached."), "Par"));
        _chat.Replies.Enqueue(FakeChatReply.Fail(new OperationCanceledException()));
        _chat.Replies.Enqueue(FakeChatReply.Fail(new FormatException("bad chunk")));

        await AskTypedAsync("One");
        await AskTypedAsync("Two");
        await AskTypedAsync("Three");

        Assert.Equal("OpenAI rate limit reached.", _vm.Answers[0].ErrorMessage);
        Assert.Equal("Par", _vm.Answers[0].Answer);
        Assert.Equal("OpenAI took too long to respond. Try again.", _vm.Answers[1].ErrorMessage);
        Assert.Equal("Something went wrong: bad chunk", _vm.Answers[2].ErrorMessage);
        Assert.All(_vm.Answers, a => Assert.False(a.IsStreaming));
        Assert.False(_vm.IsGenerating);
    }

    [Fact]
    public async Task Regenerate_streams_a_fresh_answer_into_the_same_item()
    {
        Assert.False(_vm.RegenerateCommand.CanExecute(null));
        _chat.Replies.Enqueue(FakeChatReply.Fail(new ChatClientException("Server error."), "Broken"));
        _chat.Replies.Enqueue(FakeChatReply.Text("Second ", "try."));
        await AskTypedAsync("Explain CAP");
        Assert.True(_vm.RegenerateCommand.CanExecute(null));

        await _vm.RegenerateCommand.ExecuteAsync(null);

        var item = Assert.Single(_vm.Answers);
        Assert.Equal("Second try.", item.Answer);
        Assert.False(item.HasError);
        Assert.Equal(QuestionSource.Typed, item.Source);
        Assert.Equal(2, _chat.Requests.Count);
        Assert.EndsWith("Explain CAP", LastUserMessage(_chat.Requests[1]));
    }

    [Fact]
    public async Task Earlier_answers_are_sent_as_history_and_can_be_paged_through()
    {
        _chat.Replies.Enqueue(FakeChatReply.Text("First answer."));
        await AskTypedAsync("Q1");
        await AskTypedAsync("Q2");
        await AskTypedAsync("Q3");

        Assert.Contains(_chat.Requests[1].Messages, m => m.Role == "assistant" && m.Content == "First answer.");
        Assert.Equal(2, _vm.CurrentIndex);
        Assert.Equal("3 / 3", _vm.PositionText);
        Assert.False(_vm.NextCommand.CanExecute(null));

        var resets = 0;
        _vm.AnswerReset += (_, _) => resets++;
        _vm.PreviousCommand.Execute(null);
        _vm.PreviousCommand.Execute(null);

        Assert.Equal(0, _vm.CurrentIndex);
        Assert.Equal("1 / 3", _vm.PositionText);
        Assert.Equal("Q1", _vm.CurrentItem?.Question);
        Assert.False(_vm.PreviousCommand.CanExecute(null));
        Assert.True(_vm.NextCommand.CanExecute(null));
        Assert.Equal(2, resets);
    }

    [Fact]
    public async Task Clear_all_removes_answers_and_the_draft()
    {
        await AskTypedAsync("Q1");
        _vm.QuestionDraft = "half typed";

        _vm.ClearAllCommand.Execute(null);

        Assert.Empty(_vm.Answers);
        Assert.Null(_vm.CurrentItem);
        Assert.False(_vm.HasCurrentItem);
        Assert.Equal(-1, _vm.CurrentIndex);
        Assert.Equal("", _vm.PositionText);
        Assert.Equal("", _vm.QuestionDraft);
    }

    // ---- window commands ----

    [Fact]
    public void Live_page_visibility_turns_enter_interception_on_and_off()
    {
        _vm.OnPageAppearing();
        Assert.True(_window.InterceptEnterKey);

        _vm.OnPageDisappearing();
        Assert.False(_window.InterceptEnterKey);
    }

    [Fact]
    public void Enter_command_from_another_tab_switches_to_live_and_asks()
    {
        _vm.QuestionDraft = "What's your notice period?";

        _window.RaiseCommand(WindowCommand.Ask);

        Assert.Equal([AppPage.Live], _navigation.Visited);
        Assert.Equal("What's your notice period?", Assert.Single(_vm.Answers).Question);
    }

    [Fact]
    public void Enter_command_on_the_live_tab_asks_without_navigating()
    {
        _vm.OnPageAppearing();
        _vm.QuestionDraft = "Any questions for us?";

        _window.RaiseCommand(WindowCommand.Ask);

        Assert.Empty(_navigation.Visited);
        Assert.Single(_vm.Answers);
    }

    [Fact]
    public async Task Escape_command_cancels_the_running_answer()
    {
        _chat.Replies.Enqueue(FakeChatReply.Hang("Partial answer"));
        var answering = AskTypedAsync("Long question");

        _window.RaiseCommand(WindowCommand.Cancel);

        Assert.False(_vm.IsGenerating);
        Assert.Equal("Partial answer", _vm.Answers[0].Answer);
        await answering;
        Assert.False(_vm.Answers[0].HasError);
    }

    [Fact]
    public void Window_warnings_are_shown_as_a_toast()
    {
        _window.RaiseWarning("Ctrl+Shift+H is already used by another app.");
        _dispatcher.Drain();

        Assert.Equal("Ctrl+Shift+H is already used by another app.", _vm.ToastText);
    }

    // ---- interview type, tools, toasts ----

    [Fact]
    public async Task Interview_type_is_chosen_from_a_dialog()
    {
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _dialogs.ChooseResult = "Behavioral";

        await _vm.ChooseInterviewTypeCommand.ExecuteAsync(null);

        var (title, options) = Assert.Single(_dialogs.Chooses);
        Assert.Equal("Interview type", title);
        Assert.Equal(InterviewTypes.All.Select(t => t.Name), options);
        Assert.Equal("Behavioral", Settings.InterviewType);
        Assert.Equal("Answering for: Behavioral", _vm.ToastText);

        Assert.DoesNotContain(nameof(MainViewModel.InterviewTypeLabel), changed);
        _dispatcher.Drain();
        Assert.Contains(nameof(MainViewModel.InterviewTypeLabel), changed);
        Assert.Equal("Behavioral", _vm.InterviewTypeLabel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Not a real type")]
    public async Task Cancelled_or_unknown_interview_type_changes_nothing(string? choice)
    {
        Settings.InterviewType = "Technical";
        _dialogs.ChooseResult = choice;

        await _vm.ChooseInterviewTypeCommand.ExecuteAsync(null);

        Assert.Equal("Technical", Settings.InterviewType);
        Assert.Equal("", _vm.ToastText);
    }

    [Fact]
    public async Task Copy_puts_the_answer_on_the_clipboard()
    {
        await _vm.CopyAnswerCommand.ExecuteAsync(null);
        Assert.Equal("Nothing to copy yet", _vm.ToastText);
        Assert.Null(_clipboard.Text);

        await AskTypedAsync("Q");
        await _vm.CopyAnswerCommand.ExecuteAsync(null);
        Assert.Equal("An answer.", _clipboard.Text);
        Assert.Equal("Copied", _vm.ToastText);

        _clipboard.Exception = new InvalidOperationException("Clipboard is locked");
        await _vm.CopyAnswerCommand.ExecuteAsync(null);
        Assert.Equal("Couldn't copy to the clipboard", _vm.ToastText);
    }

    [Fact]
    public async Task Toast_disappears_after_its_duration()
    {
        await _vm.CopyAnswerCommand.ExecuteAsync(null);

        _dispatcher.Advance(TimeSpan.FromSeconds(2.9));
        Assert.Equal("Nothing to copy yet", _vm.ToastText);

        _dispatcher.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal("", _vm.ToastText);
    }

    [Fact]
    public void Pin_and_stealth_toggle_the_settings()
    {
        Settings.AlwaysOnTop = true;
        Settings.StealthMode = false;

        _vm.TogglePinCommand.Execute(null);
        Assert.False(Settings.AlwaysOnTop);
        Assert.Equal("Unpinned", _vm.ToastText);

        _vm.ToggleStealthCommand.Execute(null);
        Assert.True(Settings.StealthMode);
        Assert.Equal("Stealth on: hidden from screen sharing", _vm.ToastText);
    }

    [Fact]
    public void Capabilities_come_from_the_window_service()
    {
        _window.SupportsAlwaysOnTop = true;
        _window.SupportsStealth = false;

        Assert.True(_vm.SupportsPin);
        Assert.False(_vm.SupportsStealth);
    }

    [Fact]
    public void Font_size_changes_show_a_toast_only_on_the_live_tab()
    {
        Settings.FontSize = 15;
        _dispatcher.Drain();
        Assert.Equal("", _vm.ToastText);

        _vm.OnPageAppearing();
        Settings.FontSize = 16;
        _dispatcher.Drain();
        Assert.Equal("Text size 16", _vm.ToastText);
    }
}
