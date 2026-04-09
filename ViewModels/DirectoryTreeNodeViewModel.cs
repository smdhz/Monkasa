using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Monkasa.ViewModels;

public sealed partial class DirectoryTreeNodeViewModel : ObservableObject
{
    private readonly Action<DirectoryTreeNodeViewModel>? _expandAction;

    public DirectoryTreeNodeViewModel(
        string fullPath,
        string displayName,
        DirectoryTreeNodeViewModel? parent,
        Action<DirectoryTreeNodeViewModel>? expandAction,
        bool isPlaceholder = false)
    {
        FullPath = fullPath;
        DisplayName = displayName;
        Parent = parent;
        IsPlaceholder = isPlaceholder;
        _expandAction = expandAction;
    }

    public string FullPath { get; }

    public string DisplayName { get; }

    public DirectoryTreeNodeViewModel? Parent { get; }

    public bool IsPlaceholder { get; }

    public bool IsVisible => !IsPlaceholder;

    public ObservableCollection<DirectoryTreeNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private bool childrenLoaded;

    public string FolderIcon => IsExpanded ? "📂" : "📁";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(FolderIcon));

        if (value && !IsPlaceholder && !ChildrenLoaded)
        {
            _expandAction?.Invoke(this);
        }
    }

    public static DirectoryTreeNodeViewModel CreatePlaceholder(DirectoryTreeNodeViewModel parent)
        => new(string.Empty, string.Empty, parent, expandAction: null, isPlaceholder: true);

    public static string GetDisplayName(string path)
    {
        var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(normalized);

        if (string.IsNullOrWhiteSpace(name))
        {
            return path;
        }

        return name;
    }
}
