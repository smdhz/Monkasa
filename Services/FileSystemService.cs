using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
namespace Monkasa.Services;

public sealed class FileSystemService : IDisposable
{
    private static readonly TimeSpan DirectoryRefreshPollInterval = TimeSpan.FromSeconds(5);
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
    private readonly object _directoryWatcherGate = new();
    private readonly CancellationTokenSource _directoryRefreshPollingCts = new();
    private readonly Task _directoryRefreshPollingTask;
    private FileSystemWatcher? _directoryWatcher;
    private int _directoryRefreshRequested;
    private bool _disposed;

    public FileSystemService(ILogger<FileSystemService> logger)
    {
        _logger = logger;
        _directoryRefreshPollingTask = Task.Run(() => DirectoryRefreshPollingLoopAsync(_directoryRefreshPollingCts.Token));
    }

    public event Func<Task>? DirectoryRefreshRequestedAsync;

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

    public void SetWatchedDirectory(string? directoryPath)
    {
        if (_disposed)
        {
            return;
        }

        DisposeDirectoryWatcher();

        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(directoryPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.CreationTime
                    | NotifyFilters.Size,
            };

            watcher.Changed += (_, _) => Interlocked.Exchange(ref _directoryRefreshRequested, 1);
            watcher.Created += (_, _) => Interlocked.Exchange(ref _directoryRefreshRequested, 1);
            watcher.Deleted += (_, _) => Interlocked.Exchange(ref _directoryRefreshRequested, 1);
            watcher.Renamed += (_, _) => Interlocked.Exchange(ref _directoryRefreshRequested, 1);
            watcher.Error += (_, e) =>
            {
                _logger.LogDebug(e.GetException(), "Directory watcher error for {Directory}", directoryPath);
                Interlocked.Exchange(ref _directoryRefreshRequested, 1);
            };

            watcher.EnableRaisingEvents = true;

            lock (_directoryWatcherGate)
            {
                _directoryWatcher = watcher;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize watcher for {Directory}", directoryPath);
        }
    }

    private async Task DirectoryRefreshPollingLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(DirectoryRefreshPollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (Interlocked.Exchange(ref _directoryRefreshRequested, 0) == 0)
                {
                    continue;
                }

                var callbacks = DirectoryRefreshRequestedAsync;
                if (callbacks is null)
                {
                    continue;
                }

                foreach (var callback in callbacks.GetInvocationList().Cast<Func<Task>>())
                {
                    try
                    {
                        await callback();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Directory refresh callback failed");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Directory refresh polling loop stopped unexpectedly");
        }
    }

    private void DisposeDirectoryWatcher()
    {
        FileSystemWatcher? watcher;

        lock (_directoryWatcherGate)
        {
            watcher = _directoryWatcher;
            _directoryWatcher = null;
        }

        if (watcher is null)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _directoryRefreshPollingCts.Cancel();

        try
        {
            _directoryRefreshPollingTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Directory refresh polling loop ended with exception");
        }

        _directoryRefreshPollingCts.Dispose();
        DisposeDirectoryWatcher();
        Interlocked.Exchange(ref _directoryRefreshRequested, 0);
        DirectoryRefreshRequestedAsync = null;
    }
}
