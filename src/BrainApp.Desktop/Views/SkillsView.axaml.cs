using Avalonia.Controls;
using Avalonia.Interactivity;
using BrainApp.Desktop.ViewModels;

namespace BrainApp.Desktop.Views;

public partial class SkillsView : UserControl
{
    public SkillsView()
    {
        InitializeComponent();
    }

    private void OnEnableCheckBoxChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not SkillItemViewModel item)
            return;
        if (DataContext is SkillsViewModel vm)
            vm.PersistSkillEnabled(item);
    }
}
