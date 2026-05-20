using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BrainApp.Desktop.ViewModels;

namespace BrainApp.Desktop.Views;

public partial class DocumentsView : UserControl
{
    public DocumentsView()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OnAddDocsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DocumentsViewModel vm && TopLevel.GetTopLevel(this) is TopLevel tl)
        {
            await vm.IngestFilesAsync(tl.StorageProvider);
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        // Visual feedback could be added here
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is DocumentsViewModel vm && e.Data.Contains(DataFormats.Files))
        {
            // Handle dropped files
            // Note: Avalonia's drag-drop for files requires platform-specific handling
        }
    }
}