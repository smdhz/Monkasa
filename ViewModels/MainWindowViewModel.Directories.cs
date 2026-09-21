using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Monkasa.ViewModels;

public partial class MainWindowViewModel
{
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
            OnNodeExpandRequested);

        // Use lazy loading to avoid blocking the UI thread while building the tree.
        node.Children.Add(DirectoryTreeNodeViewModel.CreatePlaceholder(node));

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

    private void OnNodeExpandRequested(DirectoryTreeNodeViewModel node)
    {
        _ = EnsureNodeChildrenAsync(node);
    }

    private async Task EnsureNodeChildrenAsync(DirectoryTreeNodeViewModel node)
    {
        if (node.IsPlaceholder || node.ChildrenLoaded)
        {
            return;
        }

        string[] childPaths;
        try
        {
            childPaths = await Task.Run(() => _fileSystemService.GetDirectories(node.FullPath).ToArray());
        }
        catch
        {
            childPaths = [];
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (node.IsPlaceholder || node.ChildrenLoaded)
            {
                return;
            }

            node.Children.Clear();
            foreach (var childPath in childPaths)
            {
                node.Children.Add(CreateDirectoryNode(childPath, node));
            }

            node.ChildrenLoaded = true;
        }, DispatcherPriority.Background);
    }

    private async Task EnsureNodeChildrenWithOneLevelPreloadAsync(DirectoryTreeNodeViewModel node)
    {
        await EnsureNodeChildrenAsync(node);

        var children = node.Children.Where(child => !child.IsPlaceholder).ToArray();
        foreach (var child in children)
        {
            await EnsureNodeChildrenAsync(child);
        }
    }

    private async Task RefreshOtherRootsAsync(string currentDirectory)
    {
        var normalizedCurrentDirectory = NormalizePath(currentDirectory);
        var roots = DirectoryTreeRoots.Where(rootNode => !rootNode.IsPlaceholder).ToArray();

        foreach (var root in roots)
        {
            if (!IsSameOrChildPath(root.FullPath, normalizedCurrentDirectory))
            {
                await EnsureNodeChildrenAsync(root);
            }

            await Task.Delay(1);
        }
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
}
