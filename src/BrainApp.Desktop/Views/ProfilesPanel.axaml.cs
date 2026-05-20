using Avalonia.Controls;
using Avalonia.Input;
using BrainApp.Core.Models;
using BrainApp.Desktop.ViewModels;

namespace BrainApp.Desktop.Views;

public partial class ProfilesPanel : UserControl
{
    public ProfilesPanel()
    {
        InitializeComponent();
    }

    private void OnProfileTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control c && c.DataContext is Profile profile &&
            DataContext is MainWindowViewModel vm)
        {
            vm.SelectProfile(profile);
        }
    }
}
