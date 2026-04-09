using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Monkasa.Views;

public partial class DirectoryInputDialog : Window
{
    private Func<Task<string?>>? _pickDirectoryAsync;

    public DirectoryInputDialog()
    {
        InitializeComponent();
        BrowseButton.IsEnabled = false;
        BrowseHintText.IsVisible = true;
        Opened += (_, _) => PathInput.Focus();
    }

    public DirectoryInputDialog(
        string? initialPath,
        Func<Task<string?>> pickDirectoryAsync,
        bool isBrowseSupported)
        : this()
    {
        _pickDirectoryAsync = pickDirectoryAsync;
        PathInput.Text = initialPath ?? string.Empty;
        BrowseButton.IsEnabled = isBrowseSupported;
        BrowseHintText.IsVisible = !isBrowseSupported;
    }

    private async void OnBrowseButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_pickDirectoryAsync is null)
        {
            return;
        }

        var picked = await _pickDirectoryAsync();
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        PathInput.Text = picked;
    }

    private void OnCancelButtonClick(object? sender, RoutedEventArgs e)
    {
        Close((string?)null);
    }

    private void OnOpenButtonClick(object? sender, RoutedEventArgs e)
    {
        Close(PathInput.Text?.Trim());
    }

    private void OnPathInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        Close(PathInput.Text?.Trim());
        e.Handled = true;
    }
}
