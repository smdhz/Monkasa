using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Processing;

namespace Monkasa.Services;

public sealed class ThumbnailService
{
    private readonly DbStorageService _cacheStore;
    private readonly ILogger<ThumbnailService> _logger;

    public ThumbnailService(
        DbStorageService cacheStore,
        ILogger<ThumbnailService> logger)
    {
        _cacheStore = cacheStore;
        _logger = logger;
    }

    public async Task<Bitmap?> GetThumbnailAsync(
        FileInfo imageInfo,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var safeWidth = Math.Max(32, width);
        var safeHeight = Math.Max(32, height);

        var cachedBytes = await _cacheStore.TryGetAsync(
            imageInfo.FullName,
            imageInfo.LastWriteTimeUtc.Ticks,
            imageInfo.Length,
            safeWidth,
            safeHeight,
            cancellationToken);

        if (cachedBytes is not null)
        {
            try
            {
                return ToBitmap(cachedBytes);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignoring broken cached thumbnail for {Path}", imageInfo.FullName);
            }
        }

        var generatedBytes = await CreateResizedJpegAsync(
            imageInfo.FullName,
            safeWidth,
            safeHeight,
            quality: 74,
            cancellationToken);

        if (generatedBytes is null)
        {
            return null;
        }

        await _cacheStore.SaveAsync(
            imageInfo.FullName,
            imageInfo.LastWriteTimeUtc.Ticks,
            imageInfo.Length,
            safeWidth,
            safeHeight,
            generatedBytes,
            cancellationToken);

        return ToBitmap(generatedBytes);
    }

    public async Task<Bitmap?> GetPreviewAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var bytes = await ReadPreviewBytesAsync(filePath, cancellationToken);
            if (bytes is null)
            {
                return null;
            }

            return ToBitmap(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<byte[]?> CreateResizedJpegAsync(
        string filePath,
        int targetWidth,
        int targetHeight,
        int quality,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            await using var fileStream = File.OpenRead(filePath);
            using var image = await Image.LoadAsync(fileStream, cancellationToken);

            image.Mutate(context =>
            {
                context.AutoOrient();
                context.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(targetWidth, targetHeight),
                });
            });

            await using var outputStream = new MemoryStream();
            await image.SaveAsJpegAsync(outputStream, new JpegEncoder
            {
                Quality = quality,
            }, cancellationToken);

            return outputStream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadPreviewBytesAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var requiresAutoOrientation = await RequiresAutoOrientationAsync(filePath, cancellationToken);
        if (!requiresAutoOrientation)
        {
            return await File.ReadAllBytesAsync(filePath, cancellationToken);
        }

        return await CreateAutoOrientedJpegAsync(
            filePath,
            quality: 95,
            cancellationToken);
    }

    private static async Task<bool> RequiresAutoOrientationAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(filePath);
        var imageInfo = await Image.IdentifyAsync(fileStream, cancellationToken);
        if (imageInfo?.Metadata.ExifProfile is not ExifProfile exifProfile)
        {
            return false;
        }

        if (!exifProfile.TryGetValue(ExifTag.Orientation, out IExifValue<ushort>? orientationTag))
        {
            return false;
        }

        return orientationTag.Value != 1;
    }

    private static async Task<byte[]?> CreateAutoOrientedJpegAsync(
        string filePath,
        int quality,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var fileStream = File.OpenRead(filePath);
            using var image = await Image.LoadAsync(fileStream, cancellationToken);

            image.Mutate(context => context.AutoOrient());

            await using var outputStream = new MemoryStream();
            await image.SaveAsJpegAsync(outputStream, new JpegEncoder
            {
                Quality = quality,
            }, cancellationToken);

            return outputStream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Bitmap ToBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return new Bitmap(stream);
    }
}
