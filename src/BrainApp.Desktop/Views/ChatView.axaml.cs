using Avalonia.Controls;
using Avalonia.Input;
using BrainApp.Desktop.ViewModels;

namespace BrainApp.Desktop.Views;

public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (DataContext is ChatViewModel vm && vm.SendMessageCommand.CanExecute(null))
            {
                vm.SendMessageCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }
        base.OnKeyDown(e);
    }
}