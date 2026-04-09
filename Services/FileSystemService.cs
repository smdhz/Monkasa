using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
namespace Monkasa.Services;

public sealed class FileSystemService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".gif",
        ".webp",
        ".tif",
        ".tiff",
    };

    private readonly ILogger<FileSystemService> _logger;

    public FileSystemService(ILogger<FileSystemService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<string> GetDirectories(string path)
    {
        try
        {
            return Directory
                .EnumerateDirectories(path)
                .Where(static directory =>
                {
                    var name = Path.GetFileName(directory);
                    return !name.StartsWith(".", StringComparison.Ordinal);
                })
                .OrderBy(static directory => directory, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
            or IOException
            or DirectoryNotFoundException)
        {
            _logger.LogWarning(ex, "Unable to enumerate directories under {Path}", path);
            return [];
        }
    }

    public IReadOnlyList<FileInfo> GetImages(string path)
    {
        try
        {
            return Directory
                .EnumerateFiles(path)
                .Where(static file => SupportedExtensions.Contains(Path.GetExtension(file)))
                .Select(static file => new FileInfo(file))
                .OrderBy(static file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
            or IOException
            or DirectoryNotFoundException)
        {
            _logger.LogWarning(ex, "Unable to enumerate images under {Path}", path);
            return [];
        }
    }

    public void DeleteFile(string path)
    {
        File.Delete(path);
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        Directory.Delete(path, recursive);
    }
}
