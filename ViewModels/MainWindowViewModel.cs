using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private const string RootDirectoriesStateKey = "root_directories";
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
    private readonly Func<Task> _directoryRefreshCallbackAsync;
    private bool _initialized;
    private bool _suppressTreeNavigation;
    private int _viewerIndex = -1;
    private readonly List<string> _treeRootPaths = [];
    private string _homeDirectoryPath = string.Empty;

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
        _directoryRefreshCallbackAsync = async () =>
        {
            var directoryToRefresh = CurrentDirectory;
            if (string.IsNullOrWhiteSpace(directoryToRefresh) || !Directory.Exists(directoryToRefresh))
            {
                return;
            }

            Task refreshTask = Task.CompletedTask;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                refreshTask = LoadDirectoryAsync(directoryToRefresh, synchronizeTreeSelection: false);
            });
            await refreshTask;
        };
        _fileSystemService.DirectoryRefreshRequestedAsync += _directoryRefreshCallbackAsync;
        StatusText = "Ready";
    }

    public Func<string, string, Task<bool>>? ConfirmDeleteAsync { get; set; }
    public Func<string, Task>? CopyTextAsync { get; set; }
    public Func<Task<string?>>? PickDirectoryAsync { get; set; }
    public Func<string?, Task<string?>>? PromptDirectoryInputAsync { get; set; }

    public ObservableCollection<DirectoryTreeNodeViewModel> DirectoryTreeRoots { get; } = [];

    public ObservableCollection<ImageItemViewModel> Images { get; } = [];

    [ObservableProperty]
    private string currentDirectory = string.Empty;

    [ObservableProperty]
    private string directoryInputPath = string.Empty;

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
    public int SortModeIndex
    {
        get => CurrentSortMode == ImageSortMode.Time ? 1 : 0;
        set => SetSortMode(value == 1 ? nameof(ImageSortMode.Time) : nameof(ImageSortMode.Name));
    }
    public bool CanRemoveSelectedRootDirectory => CanRemoveRootDirectory(SelectedDirectoryNode);
    public bool CanAddSelectedDirectoryAsRoot => CanAddDirectoryAsRoot(SelectedDirectoryNode);
    public bool CanDeleteSelectedDirectory => CanDeleteDirectory(SelectedDirectoryNode);
    public bool IsDirectoryDialogSupported => !OperatingSystem.IsMacOS();

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

        _homeDirectoryPath = NormalizePath(homeDirectory);
        var initialDirectory = await GetInitialDirectoryAsync(homeDirectory);

        var initialRootDirectories = await GetInitialRootDirectoriesAsync(_homeDirectoryPath);
        BuildDirectoryTree(initialRootDirectories);
        if (!IsPathCoveredByRoots(initialDirectory))
        {
            AddRootDirectoryNode(initialDirectory);
            await PersistRootDirectoriesAsync();
        }

        foreach (var rootNode in DirectoryTreeRoots)
        {
            EnsureNodeChildren(rootNode);
        }
        ExpandPreferredRootNodeOnStartup();

        if (TrySelectNodeByPath(initialDirectory))
        {
            if (SelectedDirectoryNode is { } selectedNode)
            {
                EnsureNodeChildren(selectedNode);
                PreloadOneMoreFolderLevel(selectedNode);
            }
        }
        else if (DirectoryTreeRoots.FirstOrDefault() is { } firstRootNode)
        {
            SelectDirectoryNode(firstRootNode);
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
        OnPropertyChanged(nameof(CanRemoveSelectedRootDirectory));
        OnPropertyChanged(nameof(CanAddSelectedDirectoryAsRoot));
        OnPropertyChanged(nameof(CanDeleteSelectedDirectory));
        RemoveRootDirectoryCommand.NotifyCanExecuteChanged();
        AddDirectoryAsRootCommand.NotifyCanExecuteChanged();
        DeleteDirectoryCommand.NotifyCanExecuteChanged();

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
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!string.Equals(DirectoryInputPath, value, _pathComparison))
        {
            DirectoryInputPath = value;
        }

        _fileSystemService.SetWatchedDirectory(value);
    }

    partial void OnCurrentSortModeChanged(ImageSortMode value)
    {
        OnPropertyChanged(nameof(CurrentSortText));
        OnPropertyChanged(nameof(SortModeIndex));
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
    private async Task CopyDirectoryPathAsync(DirectoryTreeNodeViewModel? directoryNode)
    {
        var target = directoryNode ?? SelectedDirectoryNode;
        if (target is null || target.IsPlaceholder || CopyTextAsync is null)
        {
            return;
        }

        await CopyTextAsync(target.FullPath);
        StatusText = $"Path copied: {target.DisplayName}";
    }

    [RelayCommand]
    private async Task AddRootDirectoryAsync()
    {
        var normalizedPath = ResolveDirectoryInputPath(DirectoryInputPath);
        if (normalizedPath is null)
        {
            SelectedImage = null;
            StatusText = "Open failed: input a valid folder path";
            return;
        }

        if (!Directory.Exists(normalizedPath))
        {
            SelectedImage = null;
            StatusText = $"Directory not found: {normalizedPath}";
            return;
        }

        if (PathsEqual(normalizedPath, CurrentDirectory))
        {
            SelectedImage = null;
            StatusText = $"Already opened: {normalizedPath}";
            return;
        }

        var addAsRoot = !IsPathCoveredByRoots(normalizedPath);

        if (addAsRoot)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var rootNode = AddRootDirectoryNode(normalizedPath);
                EnsureNodeChildren(rootNode);
                PreloadOneMoreFolderLevel(rootNode);
                rootNode.IsExpanded = true;
                SelectDirectoryNode(rootNode);
            });

            await PersistRootDirectoriesAsync();
        }

        await LoadDirectoryAsync(normalizedPath, synchronizeTreeSelection: true);

        OnPropertyChanged(nameof(CanAddSelectedDirectoryAsRoot));
        AddDirectoryAsRootCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task PromptOpenDirectoryAsync()
    {
        if (PromptDirectoryInputAsync is null)
        {
            StatusText = "Path input dialog is unavailable";
            return;
        }

        var initialPath = string.IsNullOrWhiteSpace(CurrentDirectory)
            ? DirectoryInputPath
            : CurrentDirectory;

        var inputPath = await PromptDirectoryInputAsync(initialPath);
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return;
        }

        DirectoryInputPath = inputPath;
        await AddRootDirectoryAsync();
    }

    [RelayCommand(CanExecute = nameof(IsDirectoryDialogSupported))]
    private async Task OpenDirectoryDialogAsync()
    {
        if (PickDirectoryAsync is null)
        {
            StatusText = "Directory dialog is unavailable";
            return;
        }

        var pickedDirectory = await PickDirectoryAsync();
        if (string.IsNullOrWhiteSpace(pickedDirectory))
        {
            return;
        }

        DirectoryInputPath = pickedDirectory;
        await AddRootDirectoryAsync();
    }

    [RelayCommand(CanExecute = nameof(CanAddDirectoryAsRoot))]
    private async Task AddDirectoryAsRootAsync(DirectoryTreeNodeViewModel? directoryNode)
    {
        var target = directoryNode ?? SelectedDirectoryNode;
        if (!CanAddDirectoryAsRoot(target))
        {
            return;
        }

        var normalizedPath = NormalizePath(target!.FullPath);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var rootNode = AddRootDirectoryNode(normalizedPath);
            EnsureNodeChildren(rootNode);
            PreloadOneMoreFolderLevel(rootNode);
            rootNode.IsExpanded = true;
        });

        await PersistRootDirectoriesAsync();
        StatusText = $"Added favorite: {target.DisplayName}";

        OnPropertyChanged(nameof(CanAddSelectedDirectoryAsRoot));
        AddDirectoryAsRootCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveRootDirectory))]
    private async Task RemoveRootDirectoryAsync(DirectoryTreeNodeViewModel? directoryNode)
    {
        var target = directoryNode ?? SelectedDirectoryNode;
        if (!CanRemoveRootDirectory(target))
        {
            return;
        }

        var removedPath = NormalizePath(target!.FullPath);
        var nextDirectory = CurrentDirectory;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            DirectoryTreeRoots.Remove(target);
            _treeRootPaths.RemoveAll(rootPath => PathsEqual(rootPath, removedPath));

            if (IsSameOrChildPath(removedPath, nextDirectory) || !IsPathCoveredByRoots(nextDirectory))
            {
                nextDirectory = _homeDirectoryPath;
            }

            if (!IsPathCoveredByRoots(nextDirectory))
            {
                nextDirectory = DirectoryTreeRoots.FirstOrDefault()?.FullPath ?? _homeDirectoryPath;
            }

            if (DirectoryTreeRoots.Count > 0)
            {
                TrySelectNodeByPath(nextDirectory);
            }

            OnPropertyChanged(nameof(CanRemoveSelectedRootDirectory));
            OnPropertyChanged(nameof(CanAddSelectedDirectoryAsRoot));
            RemoveRootDirectoryCommand.NotifyCanExecuteChanged();
            AddDirectoryAsRootCommand.NotifyCanExecuteChanged();
        });

        await PersistRootDirectoriesAsync();

        if (Directory.Exists(nextDirectory))
        {
            await LoadDirectoryAsync(nextDirectory, synchronizeTreeSelection: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteDirectory))]
    private async Task DeleteDirectoryAsync(DirectoryTreeNodeViewModel? directoryNode)
    {
        var target = directoryNode ?? SelectedDirectoryNode;
        if (!CanDeleteDirectory(target))
        {
            return;
        }

        var targetPath = NormalizePath(target!.FullPath);
        var confirmed = await ConfirmDeletionOrDefaultAsync(
            "Delete Folder",
            $"Delete this folder and all subfolders?\n\n{targetPath}");

        if (!confirmed)
        {
            return;
        }

        var fallbackDirectory = CurrentDirectory;
        if (IsSameOrChildPath(targetPath, fallbackDirectory))
        {
            fallbackDirectory = target.Parent?.FullPath ?? _homeDirectoryPath;
        }

        try
        {
            _fileSystemService.DeleteDirectory(targetPath, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete directory {Path}", targetPath);
            StatusText = $"Delete failed: {target.DisplayName}";
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (target.Parent is null)
            {
                DirectoryTreeRoots.Remove(target);
                _treeRootPaths.RemoveAll(rootPath => PathsEqual(rootPath, targetPath));
            }
            else
            {
                target.Parent.Children.Remove(target);
                target.Parent.ChildrenLoaded = true;

                if (target.Parent.Children.Count == 0)
                {
                    target.Parent.Children.Add(DirectoryTreeNodeViewModel.CreatePlaceholder(target.Parent));
                    target.Parent.ChildrenLoaded = false;
                }
            }

            OnPropertyChanged(nameof(CanRemoveSelectedRootDirectory));
            OnPropertyChanged(nameof(CanAddSelectedDirectoryAsRoot));
            OnPropertyChanged(nameof(CanDeleteSelectedDirectory));
            RemoveRootDirectoryCommand.NotifyCanExecuteChanged();
            AddDirectoryAsRootCommand.NotifyCanExecuteChanged();
            DeleteDirectoryCommand.NotifyCanExecuteChanged();
        });

        await PersistRootDirectoriesAsync();

        if (!Directory.Exists(fallbackDirectory) || !IsPathCoveredByRoots(fallbackDirectory))
        {
            fallbackDirectory = DirectoryTreeRoots.FirstOrDefault()?.FullPath ?? _homeDirectoryPath;
        }

        if (Directory.Exists(fallbackDirectory))
        {
            await LoadDirectoryAsync(fallbackDirectory, synchronizeTreeSelection: true);
            StatusText = $"Deleted folder: {target.DisplayName}";
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

    private void BuildDirectoryTree(IEnumerable<string> rootPaths)
    {
        _treeRootPaths.Clear();
        DirectoryTreeRoots.Clear();

        foreach (var rootPath in rootPaths)
        {
            AddRootDirectoryNode(rootPath);
        }
    }

    private DirectoryTreeNodeViewModel AddRootDirectoryNode(string rootPath)
    {
        var normalizedRootPath = NormalizePath(rootPath);
        var existingRootNode = DirectoryTreeRoots.FirstOrDefault(rootNode => PathsEqual(rootNode.FullPath, normalizedRootPath));
        if (existingRootNode is not null)
        {
            return existingRootNode;
        }

        var rootNode = CreateDirectoryNode(normalizedRootPath, parent: null);
        var insertIndex = GetRootInsertIndex(normalizedRootPath);
        _treeRootPaths.Insert(insertIndex, normalizedRootPath);
        DirectoryTreeRoots.Insert(insertIndex, rootNode);
        return rootNode;
    }

    private int GetRootInsertIndex(string normalizedRootPath)
    {
        if (PathsEqual(normalizedRootPath, _homeDirectoryPath))
        {
            return DirectoryTreeRoots.Count;
        }

        for (var index = 0; index < DirectoryTreeRoots.Count; index++)
        {
            var existingRoot = DirectoryTreeRoots[index];
            if (!existingRoot.IsPlaceholder && PathsEqual(existingRoot.FullPath, _homeDirectoryPath))
            {
                return index;
            }
        }

        return DirectoryTreeRoots.Count;
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
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var targetPath = NormalizePath(path);
        var firstMatchingRoot = DirectoryTreeRoots.FirstOrDefault(rootNode =>
            !rootNode.IsPlaceholder && IsSameOrChildPath(rootNode.FullPath, targetPath));

        if (firstMatchingRoot is null)
        {
            return false;
        }

        var targetNode = ExpandToPath(firstMatchingRoot, targetPath);
        if (targetNode is null)
        {
            return false;
        }

        SelectDirectoryNode(targetNode);
        return true;
    }

    private DirectoryTreeNodeViewModel? ExpandToPath(DirectoryTreeNodeViewModel startNode, string targetPath)
    {
        if (startNode.IsPlaceholder)
        {
            return null;
        }

        var currentNode = startNode;
        while (true)
        {
            var currentPath = NormalizePath(currentNode.FullPath);
            if (!IsSameOrChildPath(currentPath, targetPath))
            {
                return null;
            }

            if (PathsEqual(currentPath, targetPath))
            {
                return currentNode;
            }

            currentNode.IsExpanded = true;
            EnsureNodeChildren(currentNode);

            DirectoryTreeNodeViewModel? nextNode = null;
            foreach (var child in currentNode.Children)
            {
                if (child.IsPlaceholder)
                {
                    continue;
                }

                if (IsSameOrChildPath(child.FullPath, targetPath))
                {
                    nextNode = child;
                    break;
                }
            }

            if (nextNode is null)
            {
                return null;
            }

            currentNode = nextNode;
        }
    }

    private void SelectDirectoryNode(DirectoryTreeNodeViewModel node)
    {
        _suppressTreeNavigation = true;
        SelectedDirectoryNode = node;
        _suppressTreeNavigation = false;
    }

    private bool IsPathCoveredByRoots(string path)
    {
        var normalizedPath = NormalizePath(path);
        foreach (var rootPath in _treeRootPaths)
        {
            if (IsSameOrChildPath(rootPath, normalizedPath))
            {
                return true;
            }
        }

        return false;
    }

    private string? ResolveDirectoryInputPath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var candidate = input.Trim();
        if (candidate.Length > 1 && candidate[0] == '"' && candidate[^1] == '"')
        {
            candidate = candidate[1..^1].Trim();
        }

        if (candidate == "~")
        {
            candidate = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (candidate.StartsWith("~/", StringComparison.Ordinal) ||
                 candidate.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidate = Path.Combine(home, candidate[2..]);
        }

        try
        {
            return NormalizePath(candidate);
        }
        catch
        {
            return null;
        }
    }

    private bool CanRemoveRootDirectory(DirectoryTreeNodeViewModel? node)
    {
        if (node is null || node.IsPlaceholder)
        {
            return false;
        }

        if (node.Parent is not null)
        {
            return false;
        }

        if (PathsEqual(node.FullPath, _homeDirectoryPath))
        {
            return false;
        }

        return true;
    }

    private bool CanDeleteDirectory(DirectoryTreeNodeViewModel? node)
    {
        if (node is null || node.IsPlaceholder)
        {
            return false;
        }

        if (PathsEqual(node.FullPath, _homeDirectoryPath))
        {
            return false;
        }

        return Directory.Exists(node.FullPath);
    }

    private bool CanAddDirectoryAsRoot(DirectoryTreeNodeViewModel? node)
    {
        if (node is null || node.IsPlaceholder)
        {
            return false;
        }

        if (!Directory.Exists(node.FullPath))
        {
            return false;
        }

        var normalizedPath = NormalizePath(node.FullPath);
        return !_treeRootPaths.Any(existingRoot => PathsEqual(existingRoot, normalizedPath));
    }

    private void ExpandPreferredRootNodeOnStartup()
    {
        var favoriteRoots = DirectoryTreeRoots.Where(rootNode =>
            !rootNode.IsPlaceholder &&
            !PathsEqual(rootNode.FullPath, _homeDirectoryPath));

        if (TryExpandRootNodeOnStartup(favoriteRoots))
        {
            return;
        }

        var homeRoot = DirectoryTreeRoots.FirstOrDefault(rootNode =>
            !rootNode.IsPlaceholder &&
            PathsEqual(rootNode.FullPath, _homeDirectoryPath));

        if (homeRoot is not null)
        {
            ExpandRootNodeOnStartup(homeRoot);
            return;
        }

        if (DirectoryTreeRoots.FirstOrDefault(rootNode => !rootNode.IsPlaceholder) is { } firstRoot)
        {
            ExpandRootNodeOnStartup(firstRoot);
        }
    }

    private bool TryExpandRootNodeOnStartup(IEnumerable<DirectoryTreeNodeViewModel> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (ExpandRootNodeOnStartup(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private bool ExpandRootNodeOnStartup(DirectoryTreeNodeViewModel node)
    {
        if (node.IsPlaceholder)
        {
            return false;
        }

        EnsureNodeChildren(node);
        PreloadOneMoreFolderLevel(node);
        node.IsExpanded = true;
        return true;
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

    private async Task<IReadOnlyList<string>> GetInitialRootDirectoriesAsync(string homeDirectory)
    {
        var normalizedHome = NormalizePath(homeDirectory);

        try
        {
            var serializedRoots = await _cacheStore.TryGetStateValueAsync(
                RootDirectoriesStateKey,
                CancellationToken.None);

            if (string.IsNullOrWhiteSpace(serializedRoots))
            {
                return [normalizedHome];
            }

            var candidates = JsonSerializer.Deserialize<List<string>>(serializedRoots);
            if (candidates is null || candidates.Count == 0)
            {
                return [normalizedHome];
            }

            var roots = new List<string>();
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
                {
                    continue;
                }

                var normalizedCandidate = NormalizePath(candidate);
                if (PathsEqual(normalizedCandidate, normalizedHome))
                {
                    continue;
                }

                if (roots.Any(existing => PathsEqual(existing, normalizedCandidate)))
                {
                    continue;
                }

                roots.Add(normalizedCandidate);
            }

            roots.Add(normalizedHome);
            return roots;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to load root directories from state");
            return [normalizedHome];
        }
    }

    private async Task PersistRootDirectoriesAsync()
    {
        try
        {
            var roots = _treeRootPaths
                .Where(path => !PathsEqual(path, _homeDirectoryPath))
                .Where(Directory.Exists)
                .Distinct(StringComparerFromPathComparison())
                .ToArray();

            await _cacheStore.SaveStateValueAsync(
                RootDirectoriesStateKey,
                JsonSerializer.Serialize(roots),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to persist root directories");
        }
    }

    private StringComparer StringComparerFromPathComparison()
        => _pathComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

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
        _fileSystemService.DirectoryRefreshRequestedAsync -= _directoryRefreshCallbackAsync;
        _fileSystemService.SetWatchedDirectory(null);

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
