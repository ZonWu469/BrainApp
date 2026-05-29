using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BrainApp.Desktop.ViewModels;
using BrainApp.Desktop.Views;

namespace BrainApp.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closing += (_, _) => viewModel.PersistUiState();
    }

    private void OpenSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow();
        settingsWindow.Show();
    }
}