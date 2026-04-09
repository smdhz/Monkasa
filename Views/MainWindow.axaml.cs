using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Monkasa.ViewModels;

namespace Monkasa.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        AttachDeleteConfirmHandler(viewModel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainWindowViewModel viewModel)
        {
            AttachDeleteConfirmHandler(viewModel);
        }
    }

    private void OnImageListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (e.Source is StyledElement { DataContext: ImageItemViewModel item } && !ReferenceEquals(viewModel.SelectedImage, item))
        {
            viewModel.SelectedImage = item;
        }

        viewModel.OpenViewerCommand.Execute(null);
    }

    private void OnImageListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (e.Source is StyledElement { DataContext: ImageItemViewModel item })
        {
            viewModel.SelectedImage = item;
        }
    }

    private void OnDirectoryTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (e.Source is StyledElement { DataContext: DirectoryTreeNodeViewModel node } && !node.IsPlaceholder)
        {
            viewModel.SelectedDirectoryNode = node;
        }
    }

    private void OnSortButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is Button { ContextMenu: { } contextMenu } button)
        {
            contextMenu.Open(button);
            e.Handled = true;
        }
    }

    private void AttachDeleteConfirmHandler(MainWindowViewModel viewModel)
    {
        viewModel.ConfirmDeleteAsync = ShowDeleteConfirmDialogAsync;
        viewModel.CopyTextAsync = CopyTextToClipboardAsync;
    }

    private async Task<bool> ShowDeleteConfirmDialogAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 480,
            Height = 210,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var deleteButton = new Button
        {
            Content = "Delete",
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        cancelButton.Click += (_, _) => dialog.Close(false);
        deleteButton.Click += (_, _) => dialog.Close(true);

        dialog.Content = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    [Grid.RowProperty] = 1,
                    Children =
                    {
                        cancelButton,
                        deleteButton,
                    },
                },
            },
        };

        return await dialog.ShowDialog<bool>(this);
    }

    private async Task CopyTextToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        await clipboard.SetTextAsync(text);
    }
}
