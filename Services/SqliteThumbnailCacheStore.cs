using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Monkasa.Services;

public sealed class SqliteThumbnailCacheStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private readonly ILogger<SqliteThumbnailCacheStore> _logger;

    public SqliteThumbnailCacheStore(ILogger<SqliteThumbnailCacheStore> logger)
    {
        _logger = logger;

        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appDataDirectory))
        {
            appDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache");
        }

        var cacheDirectory = Path.Combine(appDataDirectory, "Monkasa");
        Directory.CreateDirectory(cacheDirectory);

        var databasePath = Path.Combine(cacheDirectory, "thumbnail-cache.db");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };

        _connectionString = builder.ToString();
        EnsureSchema();
    }

    public async Task<byte[]?> TryGetAsync(
        string filePath,
        long lastWriteUtcTicks,
        long fileLength,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT last_write_utc_ticks, file_length, image_bytes
                FROM thumbnail_cache
                WHERE file_path = $path AND width = $width AND height = $height;
                """;
            command.Parameters.AddWithValue("$path", filePath);
            command.Parameters.AddWithValue("$width", width);
            command.Parameters.AddWithValue("$height", height);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var cachedLastWriteUtcTicks = reader.GetInt64(0);
            var cachedFileLength = reader.GetInt64(1);

            if (cachedLastWriteUtcTicks != lastWriteUtcTicks || cachedFileLength != fileLength)
            {
                return null;
            }

            return (byte[])reader["image_bytes"];
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Unable to read thumbnail cache entry for {Path}", filePath);
            return null;
        }
        finally
        {
            _dbLock.Release();
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
        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO thumbnail_cache (
                    file_path,
                    last_write_utc_ticks,
                    file_length,
                    width,
                    height,
                    image_bytes,
                    updated_utc_ticks)
                VALUES (
                    $path,
                    $lastWriteUtcTicks,
                    $fileLength,
                    $width,
                    $height,
                    $imageBytes,
                    $updatedUtcTicks)
                ON CONFLICT(file_path, width, height) DO UPDATE SET
                    last_write_utc_ticks = excluded.last_write_utc_ticks,
                    file_length = excluded.file_length,
                    image_bytes = excluded.image_bytes,
                    updated_utc_ticks = excluded.updated_utc_ticks;
                """;
            command.Parameters.AddWithValue("$path", filePath);
            command.Parameters.AddWithValue("$lastWriteUtcTicks", lastWriteUtcTicks);
            command.Parameters.AddWithValue("$fileLength", fileLength);
            command.Parameters.AddWithValue("$width", width);
            command.Parameters.AddWithValue("$height", height);
            command.Parameters.AddWithValue("$imageBytes", imageBytes);
            command.Parameters.AddWithValue("$updatedUtcTicks", DateTime.UtcNow.Ticks);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Unable to write thumbnail cache entry for {Path}", filePath);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private void EnsureSchema()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA journal_mode = WAL;";
        pragmaCommand.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS thumbnail_cache (
                file_path TEXT NOT NULL,
                last_write_utc_ticks INTEGER NOT NULL,
                file_length INTEGER NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL,
                image_bytes BLOB NOT NULL,
                updated_utc_ticks INTEGER NOT NULL,
                PRIMARY KEY (file_path, width, height)
            );

            CREATE INDEX IF NOT EXISTS idx_thumbnail_cache_updated
            ON thumbnail_cache(updated_utc_ticks);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection CreateConnection() => new(_connectionString);
}
