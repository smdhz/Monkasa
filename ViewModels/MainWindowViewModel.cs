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
    private readonly AppLogService _appLogService;
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

    public MainWindowViewModel(
        FileSystemService fileSystemService,
        DbStorageService cacheStore,
        ThumbnailService thumbnailService,
        AppLogService appLogService,
        ILogger<MainWindowViewModel> logger)
    {
        _fileSystemService = fileSystemService;
        _cacheStore = cacheStore;
        _thumbnailService = thumbnailService;
        _appLogService = appLogService;
        _logger = logger;
        _directoryRefreshCallbackAsync = async () =>
        {
            var directoryToRefresh = CurrentDirectory;
            if (string.IsNullOrWhiteSpace(directoryToRefresh) ||
                !await Task.Run(() => Directory.Exists(directoryToRefresh)))
            {
                return;
            }

            Task refreshTask = Task.CompletedTask;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                refreshTask = RefreshDirectoryIncrementallyAsync(directoryToRefresh);
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
        try
        {
            await _cacheStore.EnsureSchemaAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to ensure local cache schema at startup");
        }

        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(homeDirectory) ||
            !await Task.Run(() => Directory.Exists(homeDirectory)))
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

        var selectedInitialNode = await TrySelectNodeByPathAsync(initialDirectory);
        if (!selectedInitialNode && DirectoryTreeRoots.FirstOrDefault() is { } firstRootNode)
        {
            SelectDirectoryNode(firstRootNode);
        }

        await LoadDirectoryAsync(initialDirectory, synchronizeTreeSelection: !selectedInitialNode);
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentDirectory))
        {
            return Task.CompletedTask;
        }

        return RefreshDirectoryIncrementallyAsync(CurrentDirectory);
    }

    [RelayCommand]
    private Task GoParentAsync()
    {
        if (SelectedDirectoryNode?.Parent is { } parentNode)
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

        if (_suppressTreeNavigation || value is null)
        {
            return;
        }

        _ = EnsureNodeChildrenAsync(value);

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
        if (!await Task.Run(() => Directory.Exists(path)))
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
            var imageFiles = await Task.Run(
                () => SortImageFiles(_fileSystemService.GetImages(fullPath)),
                cancellationToken);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentDirectory = fullPath;
                ReplaceImages(imageFiles);
                SelectedImage = null;
                CloseViewer();
                StatusText = $"{imageFiles.Count} images";
            });

            if (synchronizeTreeSelection)
            {
                await TrySelectNodeByPathAsync(fullPath);
            }

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


    [RelayCommand]
    private void OpenLogFile()
    {
        try
        {
            _appLogService.OpenLogFile();
            StatusText = $"Opened log: {_appLogService.LogFilePath}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to open log file {Path}", _appLogService.LogFilePath);
            StatusText = $"Unable to open log: {_appLogService.LogFilePath}";
        }
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
            await Task.Run(() => _fileSystemService.DeleteFile(target.FullPath));

            if (ReferenceEquals(SelectedImage, target))
            {
                SetViewerImage(null);
            }

            await RefreshDirectoryIncrementallyAsync(CurrentDirectory);
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

    private CancellationTokenSource ReplaceDirectoryTokenSource()
    {
        _directoryLoadCts?.Cancel();
        _directoryLoadCts?.Dispose();
        _directoryLoadCts = new CancellationTokenSource();
        return _directoryLoadCts;
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
