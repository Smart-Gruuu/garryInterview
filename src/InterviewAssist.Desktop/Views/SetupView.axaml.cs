using Avalonia.Controls;
using InterviewAssist.Presentation.ViewModels;

namespace InterviewAssist.Desktop.Views;

/// <summary>The Setup tab: the interview profile. Everything is bound to <see cref="SetupViewModel"/>.</summary>
public partial class SetupView : UserControl
{
    /// <summary>For the XAML designer and runtime loader only.</summary>
    public SetupView()
    {
        InitializeComponent();
    }

    public SetupView(SetupViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }
}
