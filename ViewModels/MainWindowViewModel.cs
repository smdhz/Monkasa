using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Monkasa.Services;

namespace Monkasa.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const string LastOpenedDirectoryStateKey = "last_opened_directory";
    private const int ThumbnailWidth = 320;
    private const int ThumbnailHeight = 220;

    private readonly FileSystemService _fileSystemService;
    private readonly DbStorageService _cacheStore;
    private readonly ThumbnailService _thumbnailService;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private CancellationTokenSource? _directoryLoadCts;
    private CancellationTokenSource? _viewerLoadCts;
    private bool _initialized;
    private bool _suppressTreeNavigation;
    private int _viewerIndex = -1;
    private string _treeRootPath = string.Empty;

    public enum ImageSortMode
    {
        Name,
        Time
    }

    public MainWindowViewModel(
        FileSystemService fileSystemService,
        DbStorageService cacheStore,
        ThumbnailService thumbnailService,
        ILogger<MainWindowViewModel> logger)
    {
        _fileSystemService = fileSystemService;
        _cacheStore = cacheStore;
        _thumbnailService = thumbnailService;
        _logger = logger;
        StatusText = "Ready";
    }

    public Func<string, string, Task<bool>>? ConfirmDeleteAsync { get; set; }
    public Func<string, Task>? CopyTextAsync { get; set; }

    public ObservableCollection<DirectoryTreeNodeViewModel> DirectoryTreeRoots { get; } = [];

    public ObservableCollection<ImageItemViewModel> Images { get; } = [];

    [ObservableProperty]
    private string currentDirectory = string.Empty;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private DirectoryTreeNodeViewModel? selectedDirectoryNode;

    [ObservableProperty]
    private ImageItemViewModel? selectedImage;

    [ObservableProperty]
    private Bitmap? viewerImage;

    [ObservableProperty]
    private bool isViewerOpen;

    [ObservableProperty]
    private ImageSortMode currentSortMode = ImageSortMode.Name;

    public string SelectedImageName => SelectedImage?.FileName ?? "No image selected";
    public string HeaderRightText => SelectedImage?.FileName ?? StatusText;
    public string CurrentSortText => CurrentSortMode == ImageSortMode.Time ? "Time" : "Name";
    public string SortByNameMenuText => CurrentSortMode == ImageSortMode.Name ? "Sort by Name ✔" : "Sort by Name";
    public string SortByTimeMenuText => CurrentSortMode == ImageSortMode.Time ? "Sort by Time ✔" : "Sort by Time";

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(homeDirectory) || !Directory.Exists(homeDirectory))
        {
            homeDirectory = Directory.GetCurrentDirectory();
        }

        var initialDirectory = await GetInitialDirectoryAsync(homeDirectory);
        var rootDirectory = IsSameOrChildPath(homeDirectory, initialDirectory)
            ? homeDirectory
            : initialDirectory;

        BuildDirectoryTree(rootDirectory);

        if (DirectoryTreeRoots.FirstOrDefault() is { } rootNode)
        {
            EnsureNodeChildren(rootNode);
            PreloadOneMoreFolderLevel(rootNode);
            rootNode.IsExpanded = true;
            SelectDirectoryNode(rootNode);
        }

        await LoadDirectoryAsync(initialDirectory, synchronizeTreeSelection: true);
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentDirectory))
        {
            return Task.CompletedTask;
        }

        return LoadDirectoryAsync(CurrentDirectory);
    }

    [RelayCommand]
    private Task GoParentAsync()
    {
        if (SelectedDirectoryNode?.Parent is { IsPlaceholder: false } parentNode)
        {
            SelectDirectoryNode(parentNode);
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(CurrentDirectory))
        {
            return Task.CompletedTask;
        }

        var parent = Directory.GetParent(CurrentDirectory);
        if (parent is null)
        {
            return Task.CompletedTask;
        }

        return LoadDirectoryAsync(parent.FullName);
    }

    [RelayCommand]
    private void SetSortMode(string? mode)
    {
        var nextMode = string.Equals(mode, nameof(ImageSortMode.Time), StringComparison.OrdinalIgnoreCase)
            ? ImageSortMode.Time
            : ImageSortMode.Name;

        if (CurrentSortMode == nextMode)
        {
            return;
        }

        CurrentSortMode = nextMode;
        ApplySortToImageItems();
        StatusText = $"Sorted by {CurrentSortText}";
    }

    partial void OnSelectedDirectoryNodeChanged(DirectoryTreeNodeViewModel? value)
    {
        if (_suppressTreeNavigation || value is null || value.IsPlaceholder)
        {
            return;
        }

        EnsureNodeChildren(value);
        PreloadOneMoreFolderLevel(value);

        if (PathsEqual(value.FullPath, CurrentDirectory))
        {
            return;
        }

        _ = LoadDirectoryAsync(value.FullPath, synchronizeTreeSelection: false);
    }

    partial void OnSelectedImageChanged(ImageItemViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedImageName));
        OnPropertyChanged(nameof(HeaderRightText));

        if (!IsViewerOpen || value is null)
        {
            return;
        }

        _viewerIndex = Images.IndexOf(value);
        _ = LoadViewerImageAsync(value);
    }

    partial void OnCurrentDirectoryChanged(string value)
    {
        // Persist directory state from the navigation flow where failures can be awaited/retried.
    }

    partial void OnCurrentSortModeChanged(ImageSortMode value)
    {
        OnPropertyChanged(nameof(CurrentSortText));
        OnPropertyChanged(nameof(SortByNameMenuText));
        OnPropertyChanged(nameof(SortByTimeMenuText));
    }

    private async Task LoadDirectoryAsync(string path, bool synchronizeTreeSelection = true)
    {
        if (!Directory.Exists(path))
        {
            StatusText = $"Directory not found: {path}";
            return;
        }

        var fullPath = NormalizePath(path);
        var currentLoadCts = ReplaceDirectoryTokenSource();
        var cancellationToken = currentLoadCts.Token;

        IsBusy = true;
        StatusText = $"Loading {fullPath}";

        try
        {
            await _cacheStore.RemoveMissingThumbnailsInDirectoryAsync(fullPath, cancellationToken);
            var imageFiles = SortImageFiles(_fileSystemService.GetImages(fullPath));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentDirectory = fullPath;
                ReplaceImages(imageFiles);
                SelectedImage = null;
                CloseViewer();

                if (synchronizeTreeSelection)
                {
                    _ = TrySelectNodeByPath(fullPath);
                }

                StatusText = $"{imageFiles.Count} images";
            });

            await PersistCurrentDirectoryAsync(fullPath);
            await LoadThumbnailsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Navigation changed while loading.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load directory {Directory}", fullPath);
            StatusText = $"Failed to load: {fullPath}";
        }
        finally
        {
            if (ReferenceEquals(_directoryLoadCts, currentLoadCts))
            {
                IsBusy = false;
            }
        }
    }

    private async Task LoadThumbnailsAsync(CancellationToken cancellationToken)
    {
        var snapshot = Images.ToArray();

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
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

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
                        if (!token.IsCancellationRequested)
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
    private async Task DeleteImageAsync(ImageItemViewModel? image)
    {
        var target = image ?? SelectedImage;
        if (target is null)
        {
            return;
        }

        var confirmed = await ConfirmDeletionOrDefaultAsync(
            "Delete Image",
            $"Delete this image?\n\n{target.FullPath}");

        if (!confirmed)
        {
            return;
        }

        try
        {
            _fileSystemService.DeleteFile(target.FullPath);

            if (ReferenceEquals(SelectedImage, target))
            {
                SetViewerImage(null);
            }

            await LoadDirectoryAsync(CurrentDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete image {Path}", target.FullPath);
            StatusText = $"Delete failed: {target.FileName}";
        }
    }

    [RelayCommand]
    private async Task CopyImagePathAsync(ImageItemViewModel? image)
    {
        var target = image ?? SelectedImage;
        if (target is null || CopyTextAsync is null)
        {
            return;
        }

        await CopyTextAsync(target.FullPath);
        StatusText = $"Path copied: {target.FileName}";
    }

    [RelayCommand]
    private async Task DeleteDirectoryAsync(DirectoryTreeNodeViewModel? directoryNode)
    {
        var target = directoryNode ?? SelectedDirectoryNode;
        if (target is null || target.IsPlaceholder || target.Parent is null)
        {
            return;
        }

        var confirmed = await ConfirmDeletionOrDefaultAsync(
            "Delete Folder",
            $"Delete this folder and all contents?\n\n{target.FullPath}");

        if (!confirmed)
        {
            return;
        }

        var nextDirectory = target.Parent.FullPath;

        try
        {
            _fileSystemService.DeleteDirectory(target.FullPath, recursive: true);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RebuildDirectoryTreeAndTrySelect(nextDirectory);
            });

            await LoadDirectoryAsync(nextDirectory, synchronizeTreeSelection: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete folder {Path}", target.FullPath);
            StatusText = $"Delete failed: {target.DisplayName}";
        }
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

    private void BuildDirectoryTree(string rootPath)
    {
        _treeRootPath = NormalizePath(rootPath);
        DirectoryTreeRoots.Clear();
        var rootNode = CreateDirectoryNode(_treeRootPath, parent: null);
        DirectoryTreeRoots.Add(rootNode);
    }

    private DirectoryTreeNodeViewModel CreateDirectoryNode(string path, DirectoryTreeNodeViewModel? parent)
    {
        var node = new DirectoryTreeNodeViewModel(
            path,
            DirectoryTreeNodeViewModel.GetDisplayName(path),
            parent,
            EnsureNodeChildren);

        if (HasSubdirectories(path))
        {
            node.Children.Add(DirectoryTreeNodeViewModel.CreatePlaceholder(node));
        }

        return node;
    }

    private void EnsureNodeChildren(DirectoryTreeNodeViewModel node)
    {
        if (node.IsPlaceholder || node.ChildrenLoaded)
        {
            return;
        }

        var children = _fileSystemService.GetDirectories(node.FullPath);

        node.Children.Clear();
        foreach (var childPath in children)
        {
            node.Children.Add(CreateDirectoryNode(childPath, node));
        }

        node.ChildrenLoaded = true;
    }

    private bool TrySelectNodeByPath(string path)
    {
        var targetPath = NormalizePath(path);

        foreach (var rootNode in DirectoryTreeRoots)
        {
            var targetNode = ExpandToPath(rootNode, targetPath);
            if (targetNode is not null)
            {
                SelectDirectoryNode(targetNode);
                return true;
            }
        }

        return false;
    }

    private DirectoryTreeNodeViewModel? ExpandToPath(DirectoryTreeNodeViewModel startNode, string targetPath)
    {
        var currentPath = NormalizePath(startNode.FullPath);
        if (!IsSameOrChildPath(currentPath, targetPath))
        {
            return null;
        }

        if (!startNode.ChildrenLoaded)
        {
            EnsureNodeChildren(startNode);
        }

        if (PathsEqual(currentPath, targetPath))
        {
            return startNode;
        }

        startNode.IsExpanded = true;

        foreach (var child in startNode.Children)
        {
            if (child.IsPlaceholder)
            {
                continue;
            }

            var match = ExpandToPath(child, targetPath);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private void SelectDirectoryNode(DirectoryTreeNodeViewModel node)
    {
        _suppressTreeNavigation = true;
        SelectedDirectoryNode = node;
        _suppressTreeNavigation = false;
    }

    private void RebuildDirectoryTreeAndTrySelect(string preferredPath)
    {
        var rootPath = _treeRootPath;
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            rootPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                rootPath = Directory.GetCurrentDirectory();
            }
        }

        BuildDirectoryTree(rootPath);

        if (DirectoryTreeRoots.FirstOrDefault() is { } rootNode)
        {
            EnsureNodeChildren(rootNode);
            PreloadOneMoreFolderLevel(rootNode);
            rootNode.IsExpanded = true;
            SelectDirectoryNode(rootNode);
        }

        if (Directory.Exists(preferredPath))
        {
            if (TrySelectNodeByPath(preferredPath) && SelectedDirectoryNode is { } selectedNode)
            {
                EnsureNodeChildren(selectedNode);
                PreloadOneMoreFolderLevel(selectedNode);
            }
        }
    }

    private void PreloadOneMoreFolderLevel(DirectoryTreeNodeViewModel node)
    {
        foreach (var child in node.Children)
        {
            if (child.IsPlaceholder)
            {
                continue;
            }

            EnsureNodeChildren(child);
        }
    }

    private bool HasSubdirectories(string path)
    {
        try
        {
            using var enumerator = _fileSystemService.GetDirectories(path).GetEnumerator();
            return enumerator.MoveNext();
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ConfirmDeletionOrDefaultAsync(string title, string message)
    {
        if (ConfirmDeleteAsync is null)
        {
            return false;
        }

        return await ConfirmDeleteAsync(title, message);
    }

    private async Task<string> GetInitialDirectoryAsync(string defaultDirectory)
    {
        try
        {
            var savedDirectory = await _cacheStore.TryGetStateValueAsync(
                LastOpenedDirectoryStateKey,
                CancellationToken.None);

            if (!string.IsNullOrWhiteSpace(savedDirectory) && Directory.Exists(savedDirectory))
            {
                return NormalizePath(savedDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to load last opened directory");
        }

        return NormalizePath(defaultDirectory);
    }

    private async Task PersistCurrentDirectoryAsync(string directory)
    {
        try
        {
            await _cacheStore.SaveStateValueAsync(
                LastOpenedDirectoryStateKey,
                NormalizePath(directory),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to persist current directory {Directory}", directory);
        }
    }

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HeaderRightText));
    }

    private bool PathsEqual(string left, string right)
        => string.Equals(NormalizePath(left), NormalizePath(right), _pathComparison);

    private bool IsSameOrChildPath(string candidateAncestor, string targetPath)
    {
        if (PathsEqual(candidateAncestor, targetPath))
        {
            return true;
        }

        var withSeparator = EnsureTrailingSeparator(candidateAncestor);
        var targetWithSeparator = EnsureTrailingSeparator(targetPath);
        return targetWithSeparator.StartsWith(withSeparator, _pathComparison);
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        if (fullPath.Length == 1 && (fullPath[0] == Path.DirectorySeparatorChar || fullPath[0] == Path.AltDirectorySeparatorChar))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
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
    {
        return CurrentSortMode switch
        {
            ImageSortMode.Time => imageFiles
                .OrderByDescending(static x => x.LastWriteTimeUtc.Ticks)
                .ThenBy(static x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => imageFiles
                .OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(static x => x.LastWriteTimeUtc.Ticks)
                .ToList(),
        };
    }

    private void ApplySortToImageItems()
    {
        if (Images.Count <= 1)
        {
            return;
        }

        var selectedPath = SelectedImage?.FullPath;
        var ordered = CurrentSortMode switch
        {
            ImageSortMode.Time => Images
                .OrderByDescending(static x => x.ImageInfo.LastWriteTimeUtc.Ticks)
                .ThenBy(static x => x.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => Images
                .OrderBy(static x => x.FileName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(static x => x.ImageInfo.LastWriteTimeUtc.Ticks)
                .ToList(),
        };

        var changed = false;
        for (var index = 0; index < ordered.Count; index++)
        {
            if (!ReferenceEquals(Images[index], ordered[index]))
            {
                changed = true;
                break;
            }
        }

        if (!changed)
        {
            return;
        }

        Images.Clear();
        foreach (var item in ordered)
        {
            Images.Add(item);
        }

        if (selectedPath is not null)
        {
            SelectedImage = Images.FirstOrDefault(x => PathsEqual(x.FullPath, selectedPath));
        }
    }

    private CancellationTokenSource ReplaceDirectoryTokenSource()
    {
        _directoryLoadCts?.Cancel();
        _directoryLoadCts?.Dispose();
        _directoryLoadCts = new CancellationTokenSource();
        return _directoryLoadCts;
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

    public void Dispose()
    {
        _directoryLoadCts?.Cancel();
        _viewerLoadCts?.Cancel();

        _directoryLoadCts?.Dispose();
        _viewerLoadCts?.Dispose();

        foreach (var image in Images)
        {
            image.Dispose();
        }

        Images.Clear();
        DirectoryTreeRoots.Clear();
        SetViewerImage(null);
    }
}
