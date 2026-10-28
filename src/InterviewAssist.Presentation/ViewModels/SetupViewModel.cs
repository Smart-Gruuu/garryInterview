using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewAssist.Core.Abstractions;
using InterviewAssist.Core.Chat;
using InterviewAssist.Core.Models;
using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Presentation.ViewModels;

/// <summary>The Setup tab: the interview profile (role, job description, resume, notes) the answers are grounded in.</summary>
public partial class SetupViewModel : ObservableObject
{
    private static readonly TimeSpan SuccessMessageDuration = TimeSpan.FromSeconds(5);

    private readonly IDocumentTextExtractor _extractor;
    private readonly IFilePickerService _filePicker;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUiTimer _statusClearTimer;
    private string? _autoClearMessage;

    public SetupViewModel(
        ISettingsService settingsService,
        IDocumentTextExtractor extractor,
        IFilePickerService filePicker,
        IDialogService dialogs,
        INavigationService navigation,
        IUiDispatcher dispatcher)
    {
        _extractor = extractor;
        _filePicker = filePicker;
        _dialogs = dialogs;
        _navigation = navigation;
        _dispatcher = dispatcher;

        _statusClearTimer = dispatcher.CreateTimer();
        _statusClearTimer.Interval = SuccessMessageDuration;
        _statusClearTimer.IsRepeating = false;
        _statusClearTimer.Tick += OnStatusClearTimerTick;

        Settings = settingsService.Settings;
        Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    public AppSettings Settings { get; }

    public IReadOnlyList<InterviewTypeOption> InterviewTypeOptions => InterviewTypes.All;

    public InterviewTypeOption SelectedInterviewType
    {
        get => InterviewTypes.Find(Settings.InterviewType);
        set
        {
            if (value is not null && value.Name != Settings.InterviewType)
                Settings.InterviewType = value.Name;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus), nameof(CanDismissStatus))]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _statusIsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDismissStatus))]
    [NotifyCanExecuteChangedFor(nameof(ImportResumeCommand), nameof(ImportJobDescriptionCommand))]
    private bool _isImporting;

    public bool HasStatus => StatusMessage.Length > 0;

    public bool CanDismissStatus => HasStatus && !IsImporting;

    public bool CanImport => !IsImporting;

    public bool HasResume => !string.IsNullOrWhiteSpace(Settings.Resume);

    public bool HasJobDescription => !string.IsNullOrWhiteSpace(Settings.JobDescription);

    public bool HasExtraNotes => !string.IsNullOrWhiteSpace(Settings.ExtraNotes);

    public bool HasApiKeys => Settings.HasDeepgramKey && Settings.HasOpenAiKey;

    public string ResumeReadyText => (HasResume ? "✓" : "✗") + " Resume";

    public string JobDescriptionReadyText => (HasJobDescription ? "✓" : "✗") + " Job description";

    public string ApiKeysReadyText => (HasApiKeys ? "✓" : "✗") + " API keys";

    public string ResumeInfo => DescribeLength(Settings.Resume);

    public string JobDescriptionInfo => DescribeLength(Settings.JobDescription);

    public string ExtraNotesInfo => DescribeLength(Settings.ExtraNotes);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private Task ImportResumeAsync() => ImportAsync(isResume: true);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private Task ImportJobDescriptionAsync() => ImportAsync(isResume: false);

    [RelayCommand(CanExecute = nameof(HasResume))]
    private async Task ClearResumeAsync()
    {
        if (await ConfirmClearAsync("resume"))
            Settings.Resume = "";
    }

    [RelayCommand(CanExecute = nameof(HasJobDescription))]
    private async Task ClearJobDescriptionAsync()
    {
        if (await ConfirmClearAsync("job description"))
            Settings.JobDescription = "";
    }

    [RelayCommand(CanExecute = nameof(HasExtraNotes))]
    private async Task ClearExtraNotesAsync()
    {
        if (await ConfirmClearAsync("notes"))
            Settings.ExtraNotes = "";
    }

    [RelayCommand]
    private void DismissStatus() => SetStatus("", isError: false);

    [RelayCommand]
    private Task OpenSettingsAsync() => _navigation.GoToAsync(AppPage.Settings);

    private async Task ImportAsync(bool isResume)
    {
        var what = isResume ? "resume" : "job description";

        PickedFile? file;
        try
        {
            file = await _filePicker.PickDocumentAsync($"Import {what}", _extractor.SupportedExtensions);
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't open the file picker: {ex.Message}", isError: true);
            return;
        }

        if (file is null)
            return; // cancelled

        IsImporting = true;
        SetStatus($"Reading {file.FileName}…", isError: false);
        try
        {
            string text;
            await using (var stream = await file.OpenReadAsync())
                text = await _extractor.ExtractAsync(stream, file.FileName);

            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus($"{file.FileName} doesn't contain any text. Paste the {what} instead.", isError: true);
                return;
            }

            if (isResume)
                Settings.Resume = text;
            else
                Settings.JobDescription = text;

            SetStatus($"Imported {file.FileName} ({text.Length:N0} characters).", isError: false, autoClear: true);
        }
        catch (NotSupportedException)
        {
            SetStatus(
                $"Can't read {Path.GetExtension(file.FileName)} files. Use PDF, Word (.docx), .txt or .md, or paste the text.",
                isError: true);
        }
        catch (InvalidDataException ex)
        {
            SetStatus(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't import {file.FileName}: {ex.Message}", isError: true);
        }
        finally
        {
            IsImporting = false;
        }
    }

    private Task<bool> ConfirmClearAsync(string what) =>
        _dialogs.ConfirmAsync($"Clear the {what}?", $"This removes the {what} text. It can't be undone.", "Clear");

    private void SetStatus(string message, bool isError, bool autoClear = false)
    {
        _statusClearTimer.Stop();
        _autoClearMessage = null;

        StatusIsError = isError;
        StatusMessage = message;

        if (autoClear)
        {
            _autoClearMessage = message;
            _statusClearTimer.Start();
        }
    }

    private void OnStatusClearTimerTick(object? sender, EventArgs e)
    {
        _statusClearTimer.Stop();
        if (StatusMessage == _autoClearMessage)
            StatusMessage = "";
        _autoClearMessage = null;
    }

    private static string DescribeLength(string text)
    {
        if (text.Length == 0)
            return "Empty";

        var description = $"{text.Length:N0} characters";
        return text.Length > PromptBuilder.MaxSectionChars
            ? $"{description} · only the first {PromptBuilder.MaxSectionChars:N0} are sent"
            : description;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_dispatcher.IsUiThread)
        {
            _dispatcher.Post(() => OnSettingsPropertyChanged(sender, e));
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(AppSettings.InterviewType):
                OnPropertyChanged(nameof(SelectedInterviewType));
                break;
            case nameof(AppSettings.Resume):
                OnPropertyChanged(nameof(HasResume));
                OnPropertyChanged(nameof(ResumeReadyText));
                OnPropertyChanged(nameof(ResumeInfo));
                ClearResumeCommand.NotifyCanExecuteChanged();
                break;
            case nameof(AppSettings.JobDescription):
                OnPropertyChanged(nameof(HasJobDescription));
                OnPropertyChanged(nameof(JobDescriptionReadyText));
                OnPropertyChanged(nameof(JobDescriptionInfo));
                ClearJobDescriptionCommand.NotifyCanExecuteChanged();
                break;
            case nameof(AppSettings.ExtraNotes):
                OnPropertyChanged(nameof(HasExtraNotes));
                OnPropertyChanged(nameof(ExtraNotesInfo));
                ClearExtraNotesCommand.NotifyCanExecuteChanged();
                break;
            case nameof(AppSettings.HasDeepgramKey) or nameof(AppSettings.HasOpenAiKey):
                OnPropertyChanged(nameof(HasApiKeys));
                OnPropertyChanged(nameof(ApiKeysReadyText));
                break;
            case null or "":
                OnPropertyChanged(string.Empty);
                ClearResumeCommand.NotifyCanExecuteChanged();
                ClearJobDescriptionCommand.NotifyCanExecuteChanged();
                ClearExtraNotesCommand.NotifyCanExecuteChanged();
                break;
        }
    }
}
