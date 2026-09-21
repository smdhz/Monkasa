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
        Action<DirectoryTreeNodeViewModel>? expandAction)
    {
        FullPath = fullPath;
        DisplayName = displayName;
        Parent = parent;
        _expandAction = expandAction;
    }

    public string FullPath { get; }

    public string DisplayName { get; }

    public DirectoryTreeNodeViewModel? Parent { get; }

    public ObservableCollection<DirectoryTreeNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private bool childrenLoaded;

    public string FolderIcon => IsExpanded ? "📂" : "📁";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(FolderIcon));

        if (value && !ChildrenLoaded)
        {
            _expandAction?.Invoke(this);
        }
    }

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
