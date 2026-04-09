using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using Monkasa.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Monkasa.Services;

public sealed class ThumbnailService
{
    private readonly SqliteThumbnailCacheStore _cacheStore;
    private readonly ILogger<ThumbnailService> _logger;

    public ThumbnailService(
        SqliteThumbnailCacheStore cacheStore,
        ILogger<ThumbnailService> logger)
    {
        _cacheStore = cacheStore;
        _logger = logger;
    }

    public async Task<Bitmap?> GetThumbnailAsync(
        ImageFileInfo imageInfo,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var safeWidth = Math.Max(32, width);
        var safeHeight = Math.Max(32, height);

        var cachedBytes = await _cacheStore.TryGetAsync(
            imageInfo.FullPath,
            imageInfo.LastWriteUtcTicks,
            imageInfo.FileLength,
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
                _logger.LogDebug(ex, "Ignoring broken cached thumbnail for {Path}", imageInfo.FullPath);
            }
        }

        var generatedBytes = await CreateResizedJpegAsync(
            imageInfo.FullPath,
            safeWidth,
            safeHeight,
            quality: 74,
            cancellationToken);

        if (generatedBytes is null)
        {
            return null;
        }

        await _cacheStore.SaveAsync(
            imageInfo.FullPath,
            imageInfo.LastWriteUtcTicks,
            imageInfo.FileLength,
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
            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
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

    private static Bitmap ToBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return new Bitmap(stream);
    }
}
