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
        if (target is null || CopyTextAsync is null)
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

        if (!await Task.Run(() => Directory.Exists(normalizedPath)))
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
            DirectoryTreeNodeViewModel? rootNode = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                rootNode = AddRootDirectoryNode(normalizedPath);
                rootNode.IsExpanded = true;
                SelectDirectoryNode(rootNode);
            });

            await EnsureNodeChildrenAsync(rootNode!);

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

        DirectoryTreeNodeViewModel? rootNode = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            rootNode = AddRootDirectoryNode(normalizedPath);
            rootNode.IsExpanded = true;
        });

        await EnsureNodeChildrenAsync(rootNode!);

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

            OnPropertyChanged(nameof(CanRemoveSelectedRootDirectory));
            OnPropertyChanged(nameof(CanAddSelectedDirectoryAsRoot));
            RemoveRootDirectoryCommand.NotifyCanExecuteChanged();
            AddDirectoryAsRootCommand.NotifyCanExecuteChanged();
        });

        if (DirectoryTreeRoots.Count > 0)
        {
            await TrySelectNodeByPathAsync(nextDirectory);
        }

        await PersistRootDirectoriesAsync();

        if (await Task.Run(() => Directory.Exists(nextDirectory)))
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
            await Task.Run(() => _fileSystemService.DeleteDirectory(targetPath, recursive: true));
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
            }

            OnPropertyChanged(nameof(CanRemoveSelectedRootDirectory));
            OnPropertyChanged(nameof(CanAddSelectedDirectoryAsRoot));
            OnPropertyChanged(nameof(CanDeleteSelectedDirectory));
            RemoveRootDirectoryCommand.NotifyCanExecuteChanged();
            AddDirectoryAsRootCommand.NotifyCanExecuteChanged();
            DeleteDirectoryCommand.NotifyCanExecuteChanged();
        });

        await PersistRootDirectoriesAsync();

        if (!await Task.Run(() => Directory.Exists(fallbackDirectory)) || !IsPathCoveredByRoots(fallbackDirectory))
        {
            fallbackDirectory = DirectoryTreeRoots.FirstOrDefault()?.FullPath ?? _homeDirectoryPath;
        }

        if (await Task.Run(() => Directory.Exists(fallbackDirectory)))
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
            if (PathsEqual(existingRoot.FullPath, _homeDirectoryPath))
            {
                return index;
            }
        }

        return DirectoryTreeRoots.Count;
    }

    private DirectoryTreeNodeViewModel CreateDirectoryNode(string path, DirectoryTreeNodeViewModel? parent)
    {
        return new DirectoryTreeNodeViewModel(
            path,
            DirectoryTreeNodeViewModel.GetDisplayName(path),
            parent,
            OnNodeExpandRequested);
    }

    private void OnNodeExpandRequested(DirectoryTreeNodeViewModel node)
    {
        _ = EnsureNodeChildrenAsync(node);
    }

    private async Task EnsureNodeChildrenAsync(DirectoryTreeNodeViewModel node)
    {
        if (node.ChildrenLoaded)
        {
            return;
        }

        var result = await _fileSystemService.GetDirectoriesAsync(node.FullPath);
        if (!result.IsComplete)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (node.ChildrenLoaded)
            {
                return;
            }

            node.Children.Clear();
            foreach (var childPath in result.Directories)
            {
                node.Children.Add(CreateDirectoryNode(childPath, node));
            }

            node.ChildrenLoaded = true;
        }, DispatcherPriority.Background);
    }

    private async Task<bool> TrySelectNodeByPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var targetPath = NormalizePath(path);
        var firstMatchingRoot = DirectoryTreeRoots.FirstOrDefault(rootNode =>
            IsSameOrChildPath(rootNode.FullPath, targetPath));

        if (firstMatchingRoot is null)
        {
            return false;
        }

        var targetNode = await ExpandToPathAsync(firstMatchingRoot, targetPath);
        if (targetNode is null)
        {
            return false;
        }

        await EnsureNodeChildrenAsync(targetNode);
        SelectDirectoryNode(targetNode);
        return true;
    }

    private async Task<DirectoryTreeNodeViewModel?> ExpandToPathAsync(
        DirectoryTreeNodeViewModel startNode,
        string targetPath)
    {
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

            await EnsureNodeChildrenAsync(currentNode);
            currentNode.IsExpanded = true;

            DirectoryTreeNodeViewModel? nextNode = null;
            foreach (var child in currentNode.Children)
            {
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
        if (node is null)
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
        if (node is null)
        {
            return false;
        }

        if (PathsEqual(node.FullPath, _homeDirectoryPath))
        {
            return false;
        }

        return true;
    }

    private bool CanAddDirectoryAsRoot(DirectoryTreeNodeViewModel? node)
    {
        if (node is null)
        {
            return false;
        }

        var normalizedPath = NormalizePath(node.FullPath);
        return !_treeRootPaths.Any(existingRoot => PathsEqual(existingRoot, normalizedPath));
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

            if (!string.IsNullOrWhiteSpace(savedDirectory) &&
                await Task.Run(() => Directory.Exists(savedDirectory)))
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

            var roots = await Task.Run(() =>
            {
                var availableRoots = new List<string>();
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

                    if (availableRoots.Any(existing => PathsEqual(existing, normalizedCandidate)))
                    {
                        continue;
                    }

                    availableRoots.Add(normalizedCandidate);
                }

                return availableRoots;
            });

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
            var rootCandidates = _treeRootPaths
                .Where(path => !PathsEqual(path, _homeDirectoryPath))
                .ToArray();
            var roots = await Task.Run(() => rootCandidates
                .Where(Directory.Exists)
                .Distinct(StringComparerFromPathComparison())
                .ToArray());

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
