using System.Text;
using InterviewAssist.Core.Chat;
using InterviewAssist.Core.Documents;
using InterviewAssist.Core.Models;
using InterviewAssist.Presentation.Services;
using InterviewAssist.Presentation.Tests.Fakes;
using InterviewAssist.Presentation.ViewModels;

namespace InterviewAssist.Presentation.Tests;

public class SetupViewModelTests
{
    private readonly ManualUiDispatcher _dispatcher = new();
    private readonly FakeSettingsService _settingsService = new();
    private readonly DocumentTextExtractor _extractor = new();
    private readonly FakeFilePickerService _picker = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeNavigationService _navigation = new();
    private readonly SetupViewModel _vm;

    public SetupViewModelTests()
    {
        _vm = new SetupViewModel(_settingsService, _extractor, _picker, _dialogs, _navigation, _dispatcher);
    }

    private AppSettings Settings => _settingsService.Settings;

    private static PickedFile TextFile(string fileName, string content) =>
        FakeFilePickerService.File(fileName, Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task Importing_a_resume_extracts_its_text_and_reports_success()
    {
        _picker.Result = TextFile("resume.md", "  Jane Doe\r\nSenior engineer  \r\n\r\n\r\nSkills: C#  ");

        await _vm.ImportResumeCommand.ExecuteAsync(null);

        var (title, extensions) = Assert.Single(_picker.Requests);
        Assert.Equal("Import resume", title);
        Assert.Equal(_extractor.SupportedExtensions, extensions);
        Assert.Equal("Jane Doe\nSenior engineer\n\nSkills: C#", Settings.Resume);
        Assert.Equal($"Imported resume.md ({Settings.Resume.Length:N0} characters).", _vm.StatusMessage);
        Assert.False(_vm.StatusIsError);
        Assert.False(_vm.IsImporting);
        Assert.True(_vm.HasResume);
        Assert.Equal("✓ Resume", _vm.ResumeReadyText);
        Assert.True(_vm.ClearResumeCommand.CanExecute(null));
    }

    [Fact]
    public async Task Success_message_clears_itself_after_a_few_seconds()
    {
        _picker.Result = TextFile("job.txt", "Build things.");
        await _vm.ImportJobDescriptionCommand.ExecuteAsync(null);
        Assert.Equal("Build things.", Settings.JobDescription);
        Assert.True(_vm.HasStatus);

        _dispatcher.Advance(TimeSpan.FromSeconds(4.9));
        Assert.True(_vm.HasStatus);

        _dispatcher.Advance(TimeSpan.FromSeconds(0.2));
        Assert.False(_vm.HasStatus);
    }

    [Fact]
    public async Task Cancelled_picker_changes_nothing()
    {
        Settings.Resume = "Existing";

        await _vm.ImportResumeCommand.ExecuteAsync(null);

        Assert.Equal("Existing", Settings.Resume);
        Assert.False(_vm.HasStatus);
    }

    [Fact]
    public async Task Picker_failure_is_reported()
    {
        _picker.Exception = new InvalidOperationException("No portal available");

        await _vm.ImportResumeCommand.ExecuteAsync(null);

        Assert.True(_vm.StatusIsError);
        Assert.Equal("Couldn't open the file picker: No portal available", _vm.StatusMessage);
    }

    [Fact]
    public async Task Unsupported_file_type_is_reported()
    {
        Settings.Resume = "Existing";
        _picker.Result = TextFile("resume.rtf", @"{\rtf1 Jane}");

        await _vm.ImportResumeCommand.ExecuteAsync(null);

        Assert.Equal("Existing", Settings.Resume);
        Assert.True(_vm.StatusIsError);
        Assert.Equal("Can't read .rtf files. Use PDF, Word (.docx), .txt or .md, or paste the text.", _vm.StatusMessage);
        Assert.False(_vm.IsImporting);
        Assert.True(_vm.CanDismissStatus);
    }

    [Fact]
    public async Task Empty_or_unreadable_documents_show_the_extractor_message()
    {
        _picker.Result = TextFile("empty.txt", "   \n\n ");
        await _vm.ImportJobDescriptionCommand.ExecuteAsync(null);
        Assert.True(_vm.StatusIsError);
        Assert.Equal("This file doesn't contain any text.", _vm.StatusMessage);

        _picker.Result = FakeFilePickerService.File("broken.docx", [1, 2, 3, 4]);
        await _vm.ImportJobDescriptionCommand.ExecuteAsync(null);
        Assert.True(_vm.StatusIsError);
        Assert.StartsWith("This isn't a valid .docx file.", _vm.StatusMessage);
        Assert.Equal("", Settings.JobDescription);
    }

    [Fact]
    public async Task File_that_cannot_be_opened_is_reported()
    {
        _picker.Result = new PickedFile("gone.txt", () => Task.FromException<Stream>(new FileNotFoundException("The file was moved.")));

        await _vm.ImportResumeCommand.ExecuteAsync(null);

        Assert.True(_vm.StatusIsError);
        Assert.Equal("Couldn't import gone.txt: The file was moved.", _vm.StatusMessage);
        Assert.False(_vm.IsImporting);
    }

    [Fact]
    public async Task Error_message_stays_until_dismissed()
    {
        _picker.Exception = new InvalidOperationException("boom");
        await _vm.ImportResumeCommand.ExecuteAsync(null);

        _dispatcher.Advance(TimeSpan.FromSeconds(30));
        Assert.True(_vm.HasStatus);

        _vm.DismissStatusCommand.Execute(null);
        Assert.False(_vm.HasStatus);
    }

    [Fact]
    public async Task Clearing_asks_for_confirmation()
    {
        Settings.Resume = "My resume";
        Settings.ExtraNotes = "Notes";

        _dialogs.ConfirmResult = false;
        await _vm.ClearResumeCommand.ExecuteAsync(null);
        Assert.Equal("My resume", Settings.Resume);

        _dialogs.ConfirmResult = true;
        await _vm.ClearResumeCommand.ExecuteAsync(null);
        await _vm.ClearExtraNotesCommand.ExecuteAsync(null);

        Assert.Equal("", Settings.Resume);
        Assert.Equal("", Settings.ExtraNotes);
        Assert.Equal("Clear the resume?", _dialogs.Confirms[0].Title);
        Assert.False(_vm.ClearResumeCommand.CanExecute(null));
        Assert.False(_vm.ClearExtraNotesCommand.CanExecute(null));
        Assert.False(_vm.ClearJobDescriptionCommand.CanExecute(null));
    }

    [Fact]
    public void Readiness_and_length_texts_follow_the_settings()
    {
        Assert.Equal("✗ API keys", _vm.ApiKeysReadyText);
        Assert.Equal("✗ Job description", _vm.JobDescriptionReadyText);
        Assert.Equal("Empty", _vm.ResumeInfo);

        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Settings.DeepgramApiKey = "dg";
        Settings.OpenAiApiKey = "sk";
        Settings.JobDescription = new string('x', PromptBuilder.MaxSectionChars + 1);

        Assert.Contains(nameof(SetupViewModel.ApiKeysReadyText), changed);
        Assert.Contains(nameof(SetupViewModel.JobDescriptionInfo), changed);
        Assert.Equal("✓ API keys", _vm.ApiKeysReadyText);
        Assert.Equal("✓ Job description", _vm.JobDescriptionReadyText);
        Assert.EndsWith($"only the first {PromptBuilder.MaxSectionChars:N0} are sent", _vm.JobDescriptionInfo);
    }

    [Fact]
    public void Interview_type_maps_to_the_setting()
    {
        _vm.SelectedInterviewType = InterviewTypes.All.Single(t => t.Name == "System design");

        Assert.Equal("System design", Settings.InterviewType);

        Settings.InterviewType = "unknown";
        Assert.Equal(InterviewTypes.DefaultName, _vm.SelectedInterviewType.Name);
    }

    [Fact]
    public async Task Open_settings_navigates_to_the_settings_tab()
    {
        await _vm.OpenSettingsCommand.ExecuteAsync(null);

        Assert.Equal([AppPage.Settings], _navigation.Visited);
    }
}
