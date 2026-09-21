using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Monkasa.Services;

namespace Monkasa.ViewModels;

public partial class MainWindowViewModel
{
    private async Task RefreshDirectoryIncrementallyAsync(string path)
    {
        if (!await Task.Run(() => Directory.Exists(path)))
        {
            StatusText = $"Directory not found: {path}";
            return;
        }

        var fullPath = NormalizePath(path);
        if (!PathsEqual(fullPath, CurrentDirectory))
        {
            return;
        }

        var currentLoadCts = ReplaceDirectoryTokenSource();
        var cancellationToken = currentLoadCts.Token;
        IsBusy = true;

        try
        {
            var currentFiles = await Dispatcher.UIThread.InvokeAsync(
                () => Images.Select(static item => item.FileVersion).ToArray());
            var scannedFiles = await Task.Run(
                () => _fileSystemService.GetImages(fullPath),
                cancellationToken);
            var plan = ImageCatalogPlanner.CreatePlan(
                currentFiles,
                scannedFiles,
                CurrentSortMode,
                StringComparerFromPathComparison());

            cancellationToken.ThrowIfCancellationRequested();

            ImageItemViewModel[] thumbnailsToLoad = [];
            ImageItemViewModel? viewerItemToReload = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!PathsEqual(fullPath, CurrentDirectory))
                {
                    return;
                }

                (thumbnailsToLoad, viewerItemToReload) = ApplyIncrementalImageRefresh(plan);
                StatusText = plan.HasChanges
                    ? $"{Images.Count} images · +{plan.AddedPaths.Count} ~{plan.ModifiedPaths.Count} -{plan.RemovedPaths.Count}"
                    : $"{Images.Count} images";
            });

            await _cacheStore.RemoveMissingThumbnailsInDirectoryAsync(fullPath, cancellationToken);
            await LoadThumbnailsAsync(thumbnailsToLoad, cancellationToken);

            if (viewerItemToReload is not null && IsViewerOpen && ReferenceEquals(SelectedImage, viewerItemToReload))
            {
                await LoadViewerImageAsync(viewerItemToReload);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer navigation or refresh superseded this one.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to incrementally refresh directory {Directory}", fullPath);
            StatusText = $"Refresh failed: {fullPath}";
        }
        finally
        {
            if (ReferenceEquals(_directoryLoadCts, currentLoadCts))
            {
                IsBusy = false;
            }
        }
    }

    private (ImageItemViewModel[] ThumbnailsToLoad, ImageItemViewModel? ViewerItemToReload)
        ApplyIncrementalImageRefresh(ImageCatalogRefreshPlan plan)
    {
        var existingByPath = Images.ToDictionary(
            static item => item.FullPath,
            StringComparerFromPathComparison());
        var desiredItems = new List<ImageItemViewModel>(plan.OrderedFiles.Count);
        var thumbnailsToLoad = new List<ImageItemViewModel>();
        ImageItemViewModel? viewerItemToReload = null;

        foreach (var file in plan.OrderedFiles)
        {
            if (!existingByPath.TryGetValue(file.FullName, out var item))
            {
                item = new ImageItemViewModel(file);
                thumbnailsToLoad.Add(item);
            }
            else if (plan.ModifiedPaths.Contains(file.FullName))
            {
                item.UpdateImageInfo(file);
                thumbnailsToLoad.Add(item);
                if (ReferenceEquals(item, SelectedImage) && IsViewerOpen)
                {
                    viewerItemToReload = item;
                }
            }

            desiredItems.Add(item);
        }

        for (var index = Images.Count - 1; index >= 0; index--)
        {
            var item = Images[index];
            if (!plan.RemovedPaths.Contains(item.FullPath))
            {
                continue;
            }

            var wasSelected = ReferenceEquals(item, SelectedImage);
            Images.RemoveAt(index);
            item.Dispose();

            if (wasSelected)
            {
                SelectedImage = null;
                if (IsViewerOpen)
                {
                    CloseViewer();
                }
            }
        }

        for (var targetIndex = 0; targetIndex < desiredItems.Count; targetIndex++)
        {
            var desired = desiredItems[targetIndex];
            if (targetIndex < Images.Count && ReferenceEquals(Images[targetIndex], desired))
            {
                continue;
            }

            var currentIndex = Images.IndexOf(desired);
            if (currentIndex >= 0)
            {
                Images.Move(currentIndex, targetIndex);
            }
            else
            {
                Images.Insert(targetIndex, desired);
            }
        }

        _viewerIndex = SelectedImage is null ? -1 : Images.IndexOf(SelectedImage);
        return (thumbnailsToLoad.ToArray(), viewerItemToReload);
    }

    private async Task LoadThumbnailsAsync(CancellationToken cancellationToken)
    {
        await LoadThumbnailsAsync(Images.ToArray(), cancellationToken);
    }

    private async Task LoadThumbnailsAsync(
        IReadOnlyCollection<ImageItemViewModel> items,
        CancellationToken cancellationToken)
    {
        var snapshot = items.ToArray();
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
        };

        try
        {
            await Parallel.ForEachAsync(snapshot, options, async (item, token) =>
            {
                Bitmap? thumbnail = null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    thumbnail = await _thumbnailService.GetThumbnailAsync(
                        item.ImageInfo,
                        ThumbnailWidth,
                        ThumbnailHeight,
                        token);

                    if (thumbnail is null)
                    {
                        return;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested && Images.Contains(item))
                        {
                            item.SetThumbnail(thumbnail);
                        }
                        else
                        {
                            thumbnail.Dispose();
                        }
                    }, DispatcherPriority.Background, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    thumbnail?.Dispose();
                }
                catch
                {
                    thumbnail?.Dispose();
                    throw;
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when user navigates to another folder while thumbnails are loading.
        }
    }

    private void ReplaceImages(IReadOnlyList<FileInfo> imageFiles)
    {
        foreach (var image in Images)
        {
            image.Dispose();
        }

        Images.Clear();
        foreach (var image in imageFiles)
        {
            Images.Add(new ImageItemViewModel(image));
        }
    }

    private IReadOnlyList<FileInfo> SortImageFiles(IReadOnlyList<FileInfo> imageFiles)
        => ImageCatalogPlanner.Sort(imageFiles, CurrentSortMode).ToList();

    private void ApplySortToImageItems()
    {
        if (Images.Count <= 1)
        {
            return;
        }

        var selectedPath = SelectedImage?.FullPath;
        var orderedPaths = ImageCatalogPlanner
            .Sort(Images.Select(static item => item.ImageInfo), CurrentSortMode)
            .Select(static file => file.FullName)
            .ToArray();
        var itemsByPath = Images.ToDictionary(
            static item => item.FullPath,
            StringComparerFromPathComparison());

        for (var targetIndex = 0; targetIndex < orderedPaths.Length; targetIndex++)
        {
            var desired = itemsByPath[orderedPaths[targetIndex]];
            var currentIndex = Images.IndexOf(desired);
            if (currentIndex != targetIndex)
            {
                Images.Move(currentIndex, targetIndex);
            }
        }

        if (selectedPath is not null)
        {
            SelectedImage = Images.FirstOrDefault(x => PathsEqual(x.FullPath, selectedPath));
        }
    }
}
