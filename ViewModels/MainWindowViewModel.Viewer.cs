using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Monkasa.ViewModels;

public partial class MainWindowViewModel
{
    [RelayCommand]
    private Task OpenViewerAsync()
    {
        if (SelectedImage is null)
        {
            return Task.CompletedTask;
        }

        IsViewerOpen = true;
        _viewerIndex = Images.IndexOf(SelectedImage);
        return LoadViewerImageAsync(SelectedImage);
    }

    [RelayCommand]
    private Task NextImageAsync() => NavigateViewerAsync(step: +1);

    [RelayCommand]
    private Task PreviousImageAsync() => NavigateViewerAsync(step: -1);

    [RelayCommand]
    private Task DeleteCurrentImageAsync()
    {
        if (!IsViewerOpen || SelectedImage is null)
        {
            return Task.CompletedTask;
        }

        return DeleteImageAsync(SelectedImage);
    }

    [RelayCommand]
    private void CloseViewer()
    {
        IsViewerOpen = false;
        _viewerIndex = -1;

        _viewerLoadCts?.Cancel();
        _viewerLoadCts?.Dispose();
        _viewerLoadCts = null;

        SetViewerImage(null);
    }

    private Task NavigateViewerAsync(int step)
    {
        if (!IsViewerOpen || Images.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (_viewerIndex < 0 && SelectedImage is not null)
        {
            _viewerIndex = Images.IndexOf(SelectedImage);
        }

        if (_viewerIndex < 0)
        {
            _viewerIndex = 0;
        }

        var nextIndex = (_viewerIndex + step + Images.Count) % Images.Count;
        _viewerIndex = nextIndex;
        SelectedImage = Images[nextIndex];
        return Task.CompletedTask;
    }

    private async Task LoadViewerImageAsync(ImageItemViewModel? image)
    {
        var currentViewerCts = ReplaceViewerTokenSource();
        var cancellationToken = currentViewerCts.Token;

        if (image is null)
        {
            SetViewerImage(null);
            return;
        }

        try
        {
            StatusText = $"Loading: {image.FileName}";
            var preview = await _thumbnailService.GetPreviewAsync(image.FullPath, cancellationToken);

            if (preview is null || cancellationToken.IsCancellationRequested)
            {
                preview?.Dispose();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    preview.Dispose();
                    return;
                }

                SetViewerImage(preview);
                StatusText = image.FileName;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render preview for {Path}", image.FullPath);
            StatusText = $"Failed to render: {image.FileName}";
        }
    }

    private CancellationTokenSource ReplaceViewerTokenSource()
    {
        _viewerLoadCts?.Cancel();
        _viewerLoadCts?.Dispose();
        _viewerLoadCts = new CancellationTokenSource();
        return _viewerLoadCts;
    }

    private void SetViewerImage(Bitmap? bitmap)
    {
        var previous = ViewerImage;
        ViewerImage = bitmap;
        previous?.Dispose();
    }
}
