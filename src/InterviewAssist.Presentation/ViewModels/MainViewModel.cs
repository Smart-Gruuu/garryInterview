using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Models;
using InterviewAssist.Core.Transcription;
using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Presentation.ViewModels;

public enum StatusKind
{
    Idle,
    Connecting,
    Listening,
    Reconnecting,
    Error,
}

/// <summary>
/// The Live tab: drives audio capture → Deepgram → transcript store, and question → OpenAI → streamed answer.
/// Singleton. Service events arrive on background threads and are marshalled to the UI thread; everything that
/// touches observable state, <see cref="Answers"/> or a <see cref="QaItem"/> runs on the UI thread.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxAnswers = 100;
    private const int LiveTextChars = 240;
    private const int RecentContextChars = 1500;

    private static readonly TimeSpan FinalizeTimeout = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan UiRefreshInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AnswerPushInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LongToastDuration = TimeSpan.FromSeconds(6);

    // A second Enter this soon after an answer started is a double press, not a new question.
    private static readonly TimeSpan DoubleAskGuard = TimeSpan.FromMilliseconds(400);

    private readonly IAudioCaptureService _audio;
    private readonly ITranscriptionClient _transcription;
    private readonly ITranscriptStore _store;
    private readonly IChatCompletionClient _chat;
    private readonly IPromptBuilder _promptBuilder;
    private readonly IPlatformWindowService _windowService;
    private readonly IUiDispatcher _dispatcher;
    private readonly INavigationService _navigation;
    private readonly IClipboardService _clipboard;
    private readonly IDialogService _dialogs;
    private readonly IUiTimer _uiTimer;
    private readonly IUiTimer _toastTimer;

    // Written by the capture thread, read by the UI timer.
    private readonly object _levelLock = new();
    private float _peakLevel;
    private volatile bool _forwardAudio;

    // Transcript refresh throttling (Changed arrives on any thread).
    private int _transcriptRefreshQueued;
    private long _lastTranscriptRefresh;

    // UI-thread-only state.
    private readonly QuestionDraft _draft = new();
    private Generation? _activeGeneration;
    private bool _askInFlight;
    private long _lastAnswerStartedAt;
    private long _listeningSince;
    private int _statusVersion;
    private bool _isPageVisible;
    private Task _stopTask = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListenButtonText))]
    private bool _isListening;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListenButtonText))]
    private bool _isBusyStarting;

    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private StatusKind _statusKind = StatusKind.Idle;
    [ObservableProperty] private double _audioLevel;
    [ObservableProperty] private string _elapsedText = "";
    /// <summary>The editable question box (two-way bound). New captions are appended as they arrive, even while editing.</summary>
    [ObservableProperty] private string _questionDraft = "";

    /// <summary>The words still being recognized (interim), shown under the box. Sent with the box on Enter.</summary>
    [ObservableProperty] private string _liveText = "";

    [ObservableProperty] private bool _hasPendingQuestion;
    [ObservableProperty] private string _typedQuestion = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentItem))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateCommand))]
    private QaItem? _currentItem;

    [ObservableProperty] private int _currentIndex = -1;

    [ObservableProperty] private string _positionText = "";
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private string _toastText = "";

    public MainViewModel(
        ISettingsService settingsService,
        IAudioCaptureService audio,
        ITranscriptionClient transcription,
        ITranscriptStore store,
        IChatCompletionClient chat,
        IPromptBuilder promptBuilder,
        IPlatformWindowService windowService,
        IUiDispatcher dispatcher,
        INavigationService navigation,
        IClipboardService clipboard,
        IDialogService dialogs)
    {
        Settings = settingsService.Settings;
        _audio = audio;
        _transcription = transcription;
        _store = store;
        _chat = chat;
        _promptBuilder = promptBuilder;
        _windowService = windowService;
        _dispatcher = dispatcher;
        _navigation = navigation;
        _clipboard = clipboard;
        _dialogs = dialogs;

        _uiTimer = _dispatcher.CreateTimer();
        _uiTimer.Interval = UiRefreshInterval;
        _uiTimer.IsRepeating = true;
        _uiTimer.Tick += OnUiTimerTick;

        _toastTimer = _dispatcher.CreateTimer();
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += OnToastTimerTick;

        _audio.ChunkAvailable += OnAudioChunk;
        _audio.Error += OnAudioError;
        _transcription.ResultReceived += OnTranscriptResult;
        _transcription.StateChanged += OnTranscriptionStateChanged;
        _transcription.Error += OnTranscriptionError;
        _store.Changed += OnStoreChanged;
        _windowService.CommandReceived += OnWindowCommand;
        _windowService.Warning += OnWindowWarning;
        Settings.PropertyChanged += OnSettingsPropertyChanged;

        RefreshTranscript();
    }

    /// <summary>Raised on the UI thread when the shown answer starts over (new question, regenerate, navigation).</summary>
    public event EventHandler? AnswerReset;

    /// <summary>
    /// Raised on the UI thread right before / right after new captions are appended to <see cref="QuestionDraft"/>,
    /// so the page can keep the user's caret and selection where they were while they edit.
    /// </summary>
    public event EventHandler? DraftAppending;

    /// <inheritdoc cref="DraftAppending"/>
    public event EventHandler? DraftAppended;

    public AppSettings Settings { get; }

    public ObservableCollection<QaItem> Answers { get; } = [];

    public bool HasCurrentItem => CurrentItem is not null;

    public bool SupportsPin => _windowService.SupportsAlwaysOnTop;

    public bool SupportsStealth => _windowService.SupportsStealth;

    public string ListenButtonText => IsBusyStarting ? "Starting…" : IsListening ? "■ Stop" : "▶ Start";

    /// <summary>Called by the page. Enter/Esc are routed to Ask/Cancel only while the Live tab is on screen.</summary>
    public void OnPageAppearing()
    {
        _isPageVisible = true;
        _windowService.InterceptEnterKey = true;
    }

    public void OnPageDisappearing()
    {
        _isPageVisible = false;
        _windowService.InterceptEnterKey = false;
    }

    partial void OnQuestionDraftChanged(string value)
    {
        _draft.Text = value;
        UpdatePendingFlags();
    }

    // ------------------------------------------------------------------ listening

    [RelayCommand]
    private async Task ToggleListeningAsync()
    {
        if (IsListening)
        {
            await StopListeningAsync();
        }
        else
        {
            await StartListeningAsync();
        }
    }

    private async Task StartListeningAsync()
    {
        // A stop triggered by an error may still be flushing the previous stream.
        await _stopTask;

        if (!Settings.HasDeepgramKey)
        {
            ReportError("Add your Deepgram API key in Settings");
            return;
        }

        IsBusyStarting = true;
        SetStatus(StatusKind.Connecting, "Connecting…");
        try
        {
            var options = new TranscriptionOptions(
                Settings.DeepgramApiKey.Trim(),
                Settings.DeepgramModel,
                Settings.TranscriptionLanguage,
                Settings.EndpointingMs,
                ParseKeyTerms(Settings.CustomVocabulary));

            try
            {
                await _transcription.StartAsync(options);
            }
            catch (Exception ex)
            {
                ReportError(StartErrorMessage(ex, "Could not connect to Deepgram"));
                await StopTranscriptionQuietlyAsync();
                return;
            }

            _forwardAudio = true;
            try
            {
                await _audio.StartAsync(Settings.AudioSource, Settings.AudioDeviceId);
            }
            catch (Exception ex)
            {
                _forwardAudio = false;
                ReportError(StartErrorMessage(ex, "Could not start audio capture"));
                await StopTranscriptionQuietlyAsync();
                return;
            }

            lock (_levelLock)
            {
                _peakLevel = 0;
            }

            _listeningSince = Stopwatch.GetTimestamp();
            IsListening = true;
            SetStatus(StatusKind.Listening, "Listening");
            ElapsedText = FormatElapsed(TimeSpan.Zero);
            _uiTimer.Start();
        }
        finally
        {
            IsBusyStarting = false;
        }
    }

    /// <summary>
    /// Stops capture, then the transcription stream. With no <paramref name="errorMessage"/> the status ends as
    /// "Ready" (unless something else changed it meanwhile); otherwise the error stays visible.
    /// </summary>
    private Task StopListeningAsync(string? errorMessage = null)
    {
        if (!IsListening)
        {
            return _stopTask;
        }

        IsListening = false;
        _forwardAudio = false;
        _uiTimer.Stop();
        AudioLevel = 0;
        ElapsedText = "";

        if (errorMessage is null)
        {
            SetStatus(StatusKind.Idle, "Stopping…");
        }
        else
        {
            ReportError(errorMessage);
        }

        _stopTask = StopServicesAsync(errorMessage is null ? _statusVersion : null);
        return _stopTask;
    }

    private async Task StopServicesAsync(int? readyIfStatusVersion)
    {
        try
        {
            await _audio.StopAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Audio stop failed: {ex}");
        }

        await StopTranscriptionQuietlyAsync();

        if (readyIfStatusVersion == _statusVersion)
        {
            SetStatus(StatusKind.Idle, "Ready");
        }
    }

    private async Task StopTranscriptionQuietlyAsync()
    {
        try
        {
            await _transcription.StopAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Transcription stop failed: {ex}");
        }
    }

    private static IReadOnlyList<string> ParseKeyTerms(string vocabulary) =>
        vocabulary
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string StartErrorMessage(Exception ex, string prefix) =>
        ex is InvalidOperationException ? ex.Message : $"{prefix}: {ex.Message}";

    // ------------------------------------------------------------------ asking

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task AskAsync()
    {
        // Ignore Enter while a previous press is still flushing the transcript, and double presses.
        if (_askInFlight || Stopwatch.GetElapsedTime(_lastAnswerStartedAt) < DoubleAskGuard)
        {
            return;
        }

        string question;
        _askInFlight = true;
        try
        {
            if (IsListening && Settings.FinalizeOnAsk)
            {
                try
                {
                    await _transcription.FinalizeAsync(FinalizeTimeout);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MainViewModel] Finalize failed: {ex}");
                }
            }

            // The (possibly edited) question box, plus speech that hasn't reached it yet.
            question = _draft.Take(_store.TakePending());
            QuestionDraft = "";
        }
        finally
        {
            _askInFlight = false;
        }

        RefreshTranscript();

        if (string.IsNullOrWhiteSpace(question))
        {
            ShowToast("Nothing new in the transcript yet");
            return;
        }

        await StartAnswerAsync(question, QuestionSource.Transcript);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SubmitTypedAsync()
    {
        var text = TypedQuestion.Trim();
        if (text.Length == 0)
        {
            await AskAsync();
            return;
        }

        TypedQuestion = "";
        await StartAnswerAsync(text, QuestionSource.Typed);
    }

    [RelayCommand(CanExecute = nameof(CanRegenerate), AllowConcurrentExecutions = true)]
    private Task RegenerateAsync()
    {
        var item = CurrentItem;
        return item is null || string.IsNullOrWhiteSpace(item.Question)
            ? Task.CompletedTask
            : StartAnswerAsync(item.Question, item.Source, item);
    }

    private bool CanRegenerate() => CurrentItem is { Question.Length: > 0 };

    [RelayCommand]
    private void Cancel() => CancelGeneration();

    /// <summary>
    /// Streams an answer into a new item, or into <paramref name="reuse"/> after clearing it. Runs on the UI thread:
    /// every continuation of the <c>await foreach</c> resumes on the UI synchronization context, so the item and the
    /// generation bookkeeping need no locking.
    /// </summary>
    private async Task StartAnswerAsync(string question, QuestionSource source, QaItem? reuse = null)
    {
        CancelGeneration();
        _lastAnswerStartedAt = Stopwatch.GetTimestamp();

        QaItem item;
        if (reuse is null)
        {
            item = new QaItem { Question = question, Source = source };
            Answers.Add(item);
            while (Answers.Count > MaxAnswers)
            {
                Answers.RemoveAt(0);
            }
        }
        else
        {
            item = reuse;
            item.Answer = "";
            item.ErrorMessage = "";
        }

        SetCurrent(Answers.IndexOf(item), forceReset: true);

        if (!Settings.HasOpenAiKey)
        {
            item.ErrorMessage = "Add your OpenAI API key in Settings";
            return;
        }

        var history = Answers.Take(Answers.IndexOf(item)).ToList();
        var recentContext = _store.GetRecentContext(RecentContextChars);

        var generation = new Generation(item);
        var token = generation.Token;
        _activeGeneration = generation;
        item.IsStreaming = true;
        IsGenerating = true;

        var lastPush = Stopwatch.GetTimestamp();
        try
        {
            var messages = _promptBuilder.Build(Settings, question, history, recentContext);
            var request = _promptBuilder.BuildRequest(Settings, messages);

            await foreach (var delta in _chat.StreamAsync(request, token))
            {
                if (generation.IsDetached)
                {
                    break;
                }

                generation.Text.Append(delta);
                if (Stopwatch.GetElapsedTime(lastPush) >= AnswerPushInterval)
                {
                    item.Answer = generation.Text.ToString();
                    lastPush = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopped by the user or superseded by a newer question: keep the partial answer.
        }
        catch (OperationCanceledException)
        {
            SetGenerationError(generation, "OpenAI took too long to respond. Try again.");
        }
        catch (ChatClientException ex)
        {
            SetGenerationError(generation, ex.Message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Answer generation failed: {ex}");
            SetGenerationError(generation, $"Something went wrong: {ex.Message}");
        }
        finally
        {
            CompleteGeneration(generation);
        }
    }

    private static void SetGenerationError(Generation generation, string message)
    {
        if (!generation.IsDetached)
        {
            generation.Item.ErrorMessage = message;
        }
    }

    /// <summary>Stops the running answer, if any, and leaves its partial text in place.</summary>
    private void CancelGeneration()
    {
        var generation = _activeGeneration;
        if (generation is null)
        {
            return;
        }

        _activeGeneration = null;
        generation.Cancel();
        // Flush now: the stream loop may only observe the cancellation after a newer answer reused this item.
        Detach(generation);
        IsGenerating = false;
    }

    private void CompleteGeneration(Generation generation)
    {
        Detach(generation);
        if (ReferenceEquals(_activeGeneration, generation))
        {
            _activeGeneration = null;
            IsGenerating = false;
        }

        generation.Dispose();
    }

    private static void Detach(Generation generation)
    {
        if (generation.IsDetached)
        {
            return;
        }

        generation.IsDetached = true;
        generation.Item.Answer = generation.Text.ToString();
        generation.Item.IsStreaming = false;
    }

    // ------------------------------------------------------------------ answer navigation & tools

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous() => SetCurrent(CurrentIndex - 1);

    private bool CanGoPrevious() => CurrentIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => SetCurrent(CurrentIndex + 1);

    private bool CanGoNext() => CurrentIndex >= 0 && CurrentIndex < Answers.Count - 1;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CopyAnswerAsync()
    {
        var text = CurrentItem?.Answer;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("Nothing to copy yet");
            return;
        }

        try
        {
            await _clipboard.SetTextAsync(text);
            ShowToast("Copied");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Clipboard failed: {ex}");
            ShowToast("Couldn't copy to the clipboard");
        }
    }

    [RelayCommand]
    private void ClearTranscript()
    {
        _store.Clear();
        _draft.Clear();
        QuestionDraft = "";
        RefreshTranscript();
    }

    [RelayCommand]
    private void ClearAll()
    {
        CancelGeneration();
        Answers.Clear();
        SetCurrent(-1);
        ClearTranscript();
    }

    [RelayCommand]
    private void TogglePin()
    {
        Settings.AlwaysOnTop = !Settings.AlwaysOnTop;
        ShowToast(Settings.AlwaysOnTop ? "Pinned on top" : "Unpinned");
    }

    [RelayCommand]
    private void ToggleStealth()
    {
        Settings.StealthMode = !Settings.StealthMode;
        ShowToast(Settings.StealthMode ? "Stealth on: hidden from screen sharing" : "Stealth off");
    }

    [RelayCommand]
    private void ToggleTranscript() => Settings.ShowTranscript = !Settings.ShowTranscript;

    /// <summary>Short name of the current interview type, for the chip next to Ask.</summary>
    public string InterviewTypeLabel => InterviewTypes.Find(Settings.InterviewType).ShortName;

    [RelayCommand]
    private async Task ChooseInterviewTypeAsync()
    {
        var names = InterviewTypes.All.Select(t => t.Name).ToArray();
        var choice = await _dialogs.ChooseAsync("Interview type", names);
        if (choice is null || !names.Contains(choice))
            return;

        Settings.InterviewType = choice;
        ShowToast($"Answering for: {choice}");
    }

    private void SetCurrent(int index, bool forceReset = false)
    {
        var item = Answers.Count == 0 ? null : Answers[Math.Clamp(index, 0, Answers.Count - 1)];
        var changed = !ReferenceEquals(item, CurrentItem);

        CurrentItem = item;
        CurrentIndex = item is null ? -1 : Answers.IndexOf(item);
        PositionText = item is null ? "" : $"{CurrentIndex + 1} / {Answers.Count}";

        // The count can change without the index changing (new item dropped the oldest one).
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();

        if (changed || forceReset)
        {
            AnswerReset?.Invoke(this, EventArgs.Empty);
        }
    }

    // ------------------------------------------------------------------ status & toast

    private void SetStatus(StatusKind kind, string text)
    {
        _statusVersion++;
        StatusKind = kind;
        StatusText = text;
    }

    /// <summary>Error status, plus a toast so a long message is readable when the status line truncates it.</summary>
    private void ReportError(string message)
    {
        SetStatus(StatusKind.Error, message);
        ShowToast(message, LongToastDuration);
    }

    private void ShowToast(string text, TimeSpan? duration = null)
    {
        ToastText = text;
        _toastTimer.Stop();
        _toastTimer.Interval = duration ?? ToastDuration;
        _toastTimer.Start();
    }

    private void OnToastTimerTick(object? sender, EventArgs e)
    {
        _toastTimer.Stop();
        ToastText = "";
    }

    // ------------------------------------------------------------------ periodic UI refresh (≤ 10×/s)

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        float peak;
        lock (_levelLock)
        {
            peak = _peakLevel;
            _peakLevel = 0;
        }

        // Fast attack, gentle release, so the meter doesn't flicker between chunks.
        var level = Math.Max(LevelToMeter(peak), AudioLevel * 0.6);
        AudioLevel = level < 0.02 ? 0 : Math.Round(level, 2);
        ElapsedText = FormatElapsed(Stopwatch.GetElapsedTime(_listeningSince));
    }

    /// <summary>Maps RMS 0..1 to a meter position on a dB scale (-55 dB → 0, -5 dB → 1).</summary>
    private static double LevelToMeter(float rms)
    {
        if (rms <= 0.0005f)
        {
            return 0;
        }

        var db = 20 * Math.Log10(rms);
        return Math.Clamp((db + 55) / 50, 0, 1);
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _transcriptRefreshQueued, 1) == 1)
        {
            return;
        }

        var sinceLast = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastTranscriptRefresh));
        if (sinceLast >= UiRefreshInterval)
        {
            _dispatcher.Post(RefreshTranscript);
        }
        else
        {
            _dispatcher.PostDelayed(UiRefreshInterval - sinceLast, RefreshTranscript);
        }
    }

    private void RefreshTranscript()
    {
        // Reset before reading so any change after this point queues another refresh.
        Volatile.Write(ref _transcriptRefreshQueued, 0);
        Interlocked.Exchange(ref _lastTranscriptRefresh, Stopwatch.GetTimestamp());

        // New final captions go straight into the editable box, even while the user is editing it.
        var pending = _store.GetPending();
        if (_draft.Sync(pending))
        {
            DraftAppending?.Invoke(this, EventArgs.Empty);
            QuestionDraft = _draft.Text;
            DraftAppended?.Invoke(this, EventArgs.Empty);
        }

        LiveText = TailOf(pending.Interim.Trim(), LiveTextChars);
        UpdatePendingFlags();
    }

    private void UpdatePendingFlags() =>
        HasPendingQuestion = !string.IsNullOrWhiteSpace(QuestionDraft) || LiveText.Length > 0;

    /// <summary>The last ~<paramref name="maxChars"/> characters, cut at a word, so the newest words stay visible.</summary>
    private static string TailOf(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var start = text.Length - maxChars;
        var space = text.IndexOf(' ', start);
        return "… " + (space >= 0 && space < text.Length - 1 ? text[(space + 1)..] : text[start..]);
    }

    // ------------------------------------------------------------------ service events (background threads)

    private void OnAudioChunk(object? sender, AudioChunkEventArgs e)
    {
        if (!_forwardAudio)
        {
            return;
        }

        if (e.Pcm16.Length > 0)
        {
            _transcription.SendAudio(e.Pcm16);
        }

        lock (_levelLock)
        {
            if (e.Level > _peakLevel)
            {
                _peakLevel = e.Level;
            }
        }
    }

    private void OnAudioError(object? sender, string message) => _dispatcher.Post(() =>
    {
        if (IsListening)
        {
            _ = StopListeningAsync(message);
        }
    });

    private void OnTranscriptResult(object? sender, TranscriptResult result) => _store.Apply(result);

    private void OnTranscriptionStateChanged(object? sender, TranscriptionState state) => _dispatcher.Post(() =>
    {
        // Start/stop set the status themselves; only react to changes while a session is running.
        if (!IsListening)
        {
            return;
        }

        switch (state)
        {
            case TranscriptionState.Connecting:
                SetStatus(StatusKind.Connecting, "Connecting…");
                break;
            case TranscriptionState.Connected:
                SetStatus(StatusKind.Listening, "Listening");
                break;
            case TranscriptionState.Reconnecting:
                SetStatus(StatusKind.Reconnecting, "Reconnecting…");
                break;
            case TranscriptionState.Error:
                // Reconnecting gave up; the Error event with the reason follows and replaces this text.
                _ = StopListeningAsync("Lost connection to Deepgram");
                break;
        }
    });

    private void OnTranscriptionError(object? sender, string message) => _dispatcher.Post(() =>
    {
        if (IsBusyStarting)
        {
            return; // StartAsync throws with the same message.
        }

        if (IsListening && _transcription.State is TranscriptionState.Connected or TranscriptionState.Reconnecting)
        {
            ShowToast(message, LongToastDuration); // transient; the stream is still alive
        }
        else
        {
            ReportError(message);
        }
    });

    // ------------------------------------------------------------------ window & settings events (UI thread)

    private async void OnWindowCommand(object? sender, WindowCommand command)
    {
        try
        {
            switch (command)
            {
                case WindowCommand.Ask:
                    if (!_isPageVisible)
                    {
                        await _navigation.GoToAsync(AppPage.Live);
                    }

                    AskCommand.Execute(null);
                    break;
                case WindowCommand.Cancel:
                    CancelGeneration();
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] Window command {command} failed: {ex}");
        }
    }

    private void OnWindowWarning(object? sender, string message) =>
        _dispatcher.Post(() => ShowToast(message, LongToastDuration));

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.Theme))
        {
            // The status dot color comes from theme resources; re-evaluate after the theme service applied them.
            _dispatcher.Post(() => OnPropertyChanged(nameof(StatusKind)));
        }
        else if (e.PropertyName is nameof(AppSettings.InterviewType) or null or "")
        {
            _dispatcher.Post(() => OnPropertyChanged(nameof(InterviewTypeLabel)));
        }
        else if (e.PropertyName == nameof(AppSettings.FontSize))
        {
            // Feedback for Ctrl + mouse wheel on this tab (the Settings slider shows its own value).
            _dispatcher.Post(() =>
            {
                if (_isPageVisible)
                {
                    ShowToast($"Text size {Settings.FontSize:0}");
                }
            });
        }
    }

    /// <summary>One streaming answer. Mutated only on the UI thread.</summary>
    private sealed class Generation(QaItem item) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public QaItem Item { get; } = item;

        public StringBuilder Text { get; } = new();

        public CancellationToken Token => _cts.Token;

        /// <summary>True once the item holds its final text for this run; later results must not touch it.</summary>
        public bool IsDetached { get; set; }

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (AggregateException ex)
            {
                // A registration callback in the HTTP stack threw; the token is cancelled regardless.
                Debug.WriteLine($"[MainViewModel] Cancellation callback failed: {ex}");
            }
        }

        public void Dispose() => _cts.Dispose();
    }
}
