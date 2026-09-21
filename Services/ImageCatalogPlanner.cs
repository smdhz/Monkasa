using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Monkasa.Services;

public readonly record struct ImageFileVersion(
    string FullPath,
    long LastWriteUtcTicks,
    long FileLength);

public sealed record ImageCatalogRefreshPlan(
    IReadOnlyList<FileInfo> OrderedFiles,
    IReadOnlySet<string> AddedPaths,
    IReadOnlySet<string> ModifiedPaths,
    IReadOnlySet<string> RemovedPaths)
{
    public bool HasChanges => AddedPaths.Count > 0 || ModifiedPaths.Count > 0 || RemovedPaths.Count > 0;
}

public static class ImageCatalogPlanner
{
    public static ImageCatalogRefreshPlan CreatePlan(
        IEnumerable<ImageFileVersion> currentFiles,
        IEnumerable<FileInfo> scannedFiles,
        ImageSortMode sortMode,
        StringComparer pathComparer)
    {
        ArgumentNullException.ThrowIfNull(currentFiles);
        ArgumentNullException.ThrowIfNull(scannedFiles);
        ArgumentNullException.ThrowIfNull(pathComparer);

        var currentByPath = currentFiles.ToDictionary(x => x.FullPath, pathComparer);
        var scannedByPath = scannedFiles.ToDictionary(x => x.FullName, pathComparer);

        var addedPaths = new HashSet<string>(pathComparer);
        var modifiedPaths = new HashSet<string>(pathComparer);
        var removedPaths = new HashSet<string>(currentByPath.Keys, pathComparer);

        foreach (var (path, file) in scannedByPath)
        {
            removedPaths.Remove(path);

            if (!currentByPath.TryGetValue(path, out var current))
            {
                addedPaths.Add(path);
                continue;
            }

            if (current.LastWriteUtcTicks != file.LastWriteTimeUtc.Ticks || current.FileLength != file.Length)
            {
                modifiedPaths.Add(path);
            }
        }

        var orderedFiles = Sort(scannedByPath.Values, sortMode).ToArray();
        return new ImageCatalogRefreshPlan(orderedFiles, addedPaths, modifiedPaths, removedPaths);
    }

    public static IEnumerable<FileInfo> Sort(IEnumerable<FileInfo> files, ImageSortMode sortMode)
        => sortMode switch
        {
            ImageSortMode.Time => files
                .OrderByDescending(static x => x.LastWriteTimeUtc.Ticks)
                .ThenBy(static x => x.Name, StringComparer.OrdinalIgnoreCase),
            _ => files
                .OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(static x => x.LastWriteTimeUtc.Ticks),
        };
}
