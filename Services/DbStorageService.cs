using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Monkasa.Models;
using Microsoft.Extensions.Logging;

namespace Monkasa.Services;

public sealed class DbStorageService
{
    private static readonly TimeSpan CleanupInitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupBatchDelay = TimeSpan.FromMilliseconds(500);
    private const int CleanupBatchSize = 25;

    private readonly IDbContextFactory<MonkasaDbContext> _dbContextFactory;
    private readonly ILogger<DbStorageService> _logger;

    public DbStorageService(
        IDbContextFactory<MonkasaDbContext> dbContextFactory,
        ILogger<DbStorageService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task RunBackgroundCleanupAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(CleanupInitialDelay, stoppingToken);

            _logger.LogInformation("Starting background thumbnail cache cleanup");
            var deletedCount = await RemoveMissingThumbnailsInBatchesAsync(
                CleanupBatchSize,
                CleanupBatchDelay,
                stoppingToken);

            _logger.LogInformation(
                "Background thumbnail cache cleanup finished; deleted {Count} stale entries",
                deletedCount);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Background thumbnail cache cleanup was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background thumbnail cache cleanup failed");
        }
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task<byte[]?> TryGetAsync(
        string filePath,
        long lastWriteUtcTicks,
        long fileLength,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var entry = await db.ThumbnailCache
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.FilePath == filePath && x.Width == width && x.Height == height,
                    cancellationToken);

            if (entry is null)
            {
                return null;
            }

            if (entry.LastWriteUtcTicks != lastWriteUtcTicks || entry.FileLength != fileLength)
            {
                return null;
            }

            return entry.ImageBytes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to read thumbnail cache entry for {Path}", filePath);
            return null;
        }
    }

    public async Task SaveAsync(
        string filePath,
        long lastWriteUtcTicks,
        long fileLength,
        int width,
        int height,
        byte[] imageBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var entry = await db.ThumbnailCache.FirstOrDefaultAsync(
                x => x.FilePath == filePath && x.Width == width && x.Height == height,
                cancellationToken);

            if (entry is null)
            {
                entry = new ThumbnailCacheEntry
                {
                    FilePath = filePath,
                    Width = width,
                    Height = height,
                };
                db.ThumbnailCache.Add(entry);
            }

            entry.LastWriteUtcTicks = lastWriteUtcTicks;
            entry.FileLength = fileLength;
            entry.ImageBytes = imageBytes;
            entry.UpdatedUtcTicks = DateTime.UtcNow.Ticks;

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to write thumbnail cache entry for {Path}", filePath);
        }
    }

    public async Task<string?> TryGetStateValueAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var state = await db.AppState
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.StateKey == key, cancellationToken);

            return state?.StateValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to read app state for key {Key}", key);
            return null;
        }
    }

    public async Task SaveStateValueAsync(string key, string value, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var state = await db.AppState.FirstOrDefaultAsync(x => x.StateKey == key, cancellationToken);
            if (state is null)
            {
                state = new AppStateEntry
                {
                    StateKey = key,
                };
                db.AppState.Add(state);
            }

            state.StateValue = value;
            state.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to write app state for key {Key}", key);
        }
    }

    public async Task RemoveMissingThumbnailsInDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var normalizedDirectory = Path.GetFullPath(directoryPath);
            var directoryPrefix = normalizedDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? normalizedDirectory
                : normalizedDirectory + Path.DirectorySeparatorChar;

            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var candidatePaths = await db.ThumbnailCache
                .AsNoTracking()
                .Where(x => x.FilePath.StartsWith(directoryPrefix))
                .Select(x => x.FilePath)
                .Distinct()
                .ToListAsync(cancellationToken);

            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            var missingFilePaths = candidatePaths
                .Where(path =>
                    string.Equals(Path.GetDirectoryName(path), normalizedDirectory, pathComparison)
                    && !File.Exists(path))
                .ToArray();

            if (missingFilePaths.Length == 0)
            {
                return;
            }

            var deletedCount = await db.ThumbnailCache
                .Where(x => missingFilePaths.Contains(x.FilePath))
                .ExecuteDeleteAsync(cancellationToken);

            _logger.LogInformation(
                "Deleted {Count} thumbnail cache entries for missing files in {Directory}",
                deletedCount,
                normalizedDirectory);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to cleanup missing thumbnails for {Directory}", directoryPath);
        }
    }

    private async Task<int> RemoveMissingThumbnailsInBatchesAsync(
        int batchSize,
        TimeSpan delayBetweenBatches,
        CancellationToken cancellationToken)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        await using var readDb = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var candidatePaths = await readDb.ThumbnailCache
            .AsNoTracking()
            .Select(x => x.FilePath)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        var deletedCount = 0;
        foreach (var batch in candidatePaths.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check paths serially. In particular, do not fan out filesystem calls to
            // removable volumes or cloud-storage providers during background cleanup.
            var missingFilePaths = batch
                .Where(path => !File.Exists(path))
                .ToArray();

            if (missingFilePaths.Length > 0)
            {
                await using var writeDb = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
                deletedCount += await writeDb.ThumbnailCache
                    .Where(x => missingFilePaths.Contains(x.FilePath))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            if (delayBetweenBatches > TimeSpan.Zero)
            {
                await Task.Delay(delayBetweenBatches, cancellationToken);
            }
        }

        return deletedCount;
    }

    public static string GetDatabasePath()
    {
        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appDataDirectory))
        {
            appDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache");
        }

        var cacheDirectory = Path.Combine(appDataDirectory, "Monkasa");
        Directory.CreateDirectory(cacheDirectory);
        return Path.Combine(cacheDirectory, "thumbnail-cache.db");
    }
}
