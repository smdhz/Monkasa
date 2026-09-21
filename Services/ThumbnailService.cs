using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Monkasa.Services;

public sealed class ThumbnailService
{
    private readonly DbStorageService _cacheStore;
    private readonly ISystemThumbnailProvider _systemThumbnailProvider;
    private readonly ILogger<ThumbnailService> _logger;

    public ThumbnailService(
        DbStorageService cacheStore,
        ISystemThumbnailProvider systemThumbnailProvider,
        ILogger<ThumbnailService> logger)
    {
        _cacheStore = cacheStore;
        _systemThumbnailProvider = systemThumbnailProvider;
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

        var systemResult = await _systemThumbnailProvider.GetThumbnailAsync(
            imageInfo.FullName,
            safeWidth,
            safeHeight,
            cancellationToken);

        var generatedBytes = systemResult.ImageBytes;
        if (generatedBytes is null && !systemResult.MustNotReadFile)
        {
            generatedBytes = await CreateResizedJpegAsync(
                imageInfo.FullName,
                safeWidth,
                safeHeight,
                quality: 74,
                cancellationToken);
        }

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
            var sourceBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            return await Task.Run(
                () => CreateJpeg(sourceBytes, targetWidth, targetHeight, quality, resizeToFit: true, cancellationToken),
                cancellationToken);
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
        var sourceBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var requiresAutoOrientation = await RequiresAutoOrientationAsync(sourceBytes, cancellationToken);
        if (!requiresAutoOrientation)
        {
            return sourceBytes;
        }

        return await CreateAutoOrientedJpegAsync(
            sourceBytes,
            quality: 95,
            cancellationToken);
    }

    private static async Task<bool> RequiresAutoOrientationAsync(
        byte[] sourceBytes,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var data = SKData.CreateCopy(sourceBytes);
            using var codec = SKCodec.Create(data);
            return codec is not null && codec.EncodedOrigin != SKEncodedOrigin.TopLeft;
        }, cancellationToken);
    }

    private static async Task<byte[]?> CreateAutoOrientedJpegAsync(
        byte[] sourceBytes,
        int quality,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(
                () => CreateJpeg(sourceBytes, targetWidth: 0, targetHeight: 0, quality, resizeToFit: false, cancellationToken),
                cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[]? CreateJpeg(
        byte[] sourceBytes,
        int targetWidth,
        int targetHeight,
        int quality,
        bool resizeToFit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Do not pass a FileStream to SKCodec. SkiaSharp services Stream reads through
        // an unmanaged callback; an IOException in that callback crosses the native
        // boundary and causes .NET to terminate the process before our catch can run.
        using var sourceData = SKData.CreateCopy(sourceBytes);
        using var codec = SKCodec.Create(sourceData);
        if (codec is null)
        {
            return null;
        }

        using var source = SKBitmap.Decode(codec);
        if (source is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var oriented = DrawOrientedBitmap(source, codec.EncodedOrigin, targetWidth, targetHeight, resizeToFit);
        using var image = SKImage.FromBitmap(oriented);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data?.ToArray();
    }

    private static SKBitmap DrawOrientedBitmap(
        SKBitmap source,
        SKEncodedOrigin origin,
        int targetWidth,
        int targetHeight,
        bool resizeToFit)
    {
        var swapsAxes = origin is SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;
        var orientedWidth = swapsAxes ? source.Height : source.Width;
        var orientedHeight = swapsAxes ? source.Width : source.Height;

        var scale = 1f;
        if (resizeToFit)
        {
            scale = Math.Min((float)targetWidth / orientedWidth, (float)targetHeight / orientedHeight);
            scale = Math.Min(1f, Math.Max(0.01f, scale));
        }

        var outputWidth = Math.Max(1, (int)Math.Round(orientedWidth * scale));
        var outputHeight = Math.Max(1, (int)Math.Round(orientedHeight * scale));
        var output = new SKBitmap(outputWidth, outputHeight, source.ColorType, source.AlphaType);

        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        ApplyOrientationTransform(canvas, origin, source.Width, source.Height);

        using var paint = new SKPaint
        {
            IsAntialias = true,
        };

        using var sourceImage = SKImage.FromBitmap(source);
        canvas.DrawImage(
            sourceImage,
            0,
            0,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
            paint);
        canvas.Flush();
        return output;
    }

    private static void ApplyOrientationTransform(SKCanvas canvas, SKEncodedOrigin origin, int width, int height)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(height, width);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, width);
                canvas.RotateDegrees(270);
                break;
        }
    }

    private static Bitmap ToBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return new Bitmap(stream);
    }
}
