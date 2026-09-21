using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
namespace Monkasa.Services;

public sealed record DirectoryEnumerationResult(
    IReadOnlyList<string> Directories,
    bool IsComplete);

public sealed class FileSystemService : IDisposable
{
    private static readonly TimeSpan DirectoryRefreshPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DirectoryFailureCooldown = TimeSpan.FromSeconds(60);
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
    private readonly ConcurrentDictionary<string, Lazy<Task<DirectoryEnumerationResult>>> _directoryLoads;
    private readonly ConcurrentDictionary<string, byte> _inaccessibleDirectories;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _directoryRetryAfter;
    private readonly object _directoryWatcherGate = new();
    private readonly CancellationTokenSource _directoryRefreshPollingCts = new();
    private readonly Task _directoryRefreshPollingTask;
    private FileSystemWatcher? _directoryWatcher;
    private int _directoryRefreshRequested;
    private int _directoryWatcherRequestVersion;
    private bool _disposed;

    public FileSystemService(ILogger<FileSystemService> logger)
    {
        _logger = logger;
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        _directoryLoads = new ConcurrentDictionary<string, Lazy<Task<DirectoryEnumerationResult>>>(pathComparer);
        _inaccessibleDirectories = new ConcurrentDictionary<string, byte>(pathComparer);
        _directoryRetryAfter = new ConcurrentDictionary<string, DateTimeOffset>(pathComparer);
        _directoryRefreshPollingTask = Task.Run(() => DirectoryRefreshPollingLoopAsync(_directoryRefreshPollingCts.Token));
    }

    public event Func<Task>? DirectoryRefreshRequestedAsync;

    public async Task<DirectoryEnumerationResult> GetDirectoriesAsync(string path)
    {
        var normalizedPath = Path.GetFullPath(path);
        if (_inaccessibleDirectories.ContainsKey(normalizedPath))
        {
            return new DirectoryEnumerationResult([], IsComplete: true);
        }

        if (_directoryRetryAfter.TryGetValue(normalizedPath, out var retryAfter))
        {
            if (retryAfter > DateTimeOffset.UtcNow)
            {
                return new DirectoryEnumerationResult([], IsComplete: false);
            }

            _directoryRetryAfter.TryRemove(normalizedPath, out _);
        }

        var load = _directoryLoads.GetOrAdd(
            normalizedPath,
            static (directoryPath, service) => new Lazy<Task<DirectoryEnumerationResult>>(
                () => Task.Run(() => service.GetDirectoriesCore(directoryPath)),
                LazyThreadSafetyMode.ExecutionAndPublication),
            this);

        try
        {
            return await load.Value.ConfigureAwait(false);
        }
        finally
        {
            if (_directoryLoads.TryGetValue(normalizedPath, out var currentLoad) &&
                ReferenceEquals(currentLoad, load))
            {
                _directoryLoads.TryRemove(normalizedPath, out _);
            }
        }
    }

    private DirectoryEnumerationResult GetDirectoriesCore(string path)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            };

            var directories = Directory
                .EnumerateDirectories(path, "*", options)
                .Where(static directory =>
                {
                    var name = Path.GetFileName(directory);
                    return !name.StartsWith(".", StringComparison.Ordinal);
                })
                .OrderBy(static directory => directory, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new DirectoryEnumerationResult(directories, IsComplete: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            _inaccessibleDirectories.TryAdd(path, 0);
            _logger.LogDebug(ex, "Directory is inaccessible; suppressing retries for this session: {Path}", path);
            return new DirectoryEnumerationResult([], IsComplete: true);
        }
        catch (DirectoryNotFoundException)
        {
            return new DirectoryEnumerationResult([], IsComplete: true);
        }
        catch (IOException ex)
        {
            _directoryRetryAfter[path] = DateTimeOffset.UtcNow.Add(DirectoryFailureCooldown);
            _logger.LogDebug(ex, "Directory enumeration failed; suppressing retries until cooldown expires: {Path}", path);
            return new DirectoryEnumerationResult([], IsComplete: false);
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
        var requestVersion = Interlocked.Increment(ref _directoryWatcherRequestVersion);
        _ = Task.Run(() => SetWatchedDirectoryCore(directoryPath, requestVersion));
    }

    private void SetWatchedDirectoryCore(string? directoryPath, int requestVersion)
    {
        FileSystemWatcher? watcher = null;
        if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
        {
            try
            {
                watcher = new FileSystemWatcher(directoryPath)
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
            }
            catch (Exception ex)
            {
                watcher?.Dispose();
                watcher = null;
                _logger.LogWarning(ex, "Failed to initialize watcher for {Directory}", directoryPath);
            }
        }

        FileSystemWatcher? previousWatcher;
        lock (_directoryWatcherGate)
        {
            if (_disposed || requestVersion != _directoryWatcherRequestVersion)
            {
                watcher?.Dispose();
                return;
            }

            previousWatcher = _directoryWatcher;
            _directoryWatcher = watcher;
        }

        previousWatcher?.Dispose();
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
        Interlocked.Increment(ref _directoryWatcherRequestVersion);
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
