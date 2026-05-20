using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace BrainApp.Desktop;

public partial class LoadingWindow : Window
{
    public LoadingWindow()
    {
        InitializeComponent();
    }

    public LoadingWindow(string title)
    {
        Title = title;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}