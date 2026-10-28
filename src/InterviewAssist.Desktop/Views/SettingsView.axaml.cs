using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using InterviewAssist.Presentation.ViewModels;

namespace InterviewAssist.Desktop.Views;

/// <summary>The Settings tab. Every control applies its change immediately; SettingsService auto-saves.</summary>
public partial class SettingsView : UserControl
{
    private readonly SettingsViewModel? _viewModel;

    /// <summary>For the XAML designer and runtime loader only.</summary>
    public SettingsView()
    {
        InitializeComponent();
    }

    public SettingsView(SettingsViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // The tab host only keeps the selected tab in the visual tree, so this is "the Settings tab appeared".
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewModel?.OnAppearing();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.AudioDevices))
            return;

        // A new device list clears the combo's selection, and the SelectedItem binding re-reads the view model while
        // the combo is still swapping items. The combo drops that value but the binding keeps it, so it ignores the
        // next (equal) notification and the picker stays blank. Re-apply the selection once the swap is done.
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel is not null)
                DeviceCombo.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, _viewModel.SelectedAudioDevice);
        });
    }
}
