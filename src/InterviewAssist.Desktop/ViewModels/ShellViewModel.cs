using CommunityToolkit.Mvvm.ComponentModel;
using InterviewAssist.Presentation.Services;

namespace InterviewAssist.Desktop.ViewModels;

/// <summary>The main window's tab selection. Tab indexes follow <see cref="AppPage"/> (Live, Setup, Settings).</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPage))]
    private int _selectedTabIndex = (int)AppPage.Live;

    public AppPage SelectedPage => (AppPage)SelectedTabIndex;
}
