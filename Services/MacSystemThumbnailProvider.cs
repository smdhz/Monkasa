using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Monkasa.Services;

public sealed class MacSystemThumbnailProvider : ISystemThumbnailProvider
{
    private const string NativeLibrary = "libMonkasa.Platform.Mac.dylib";
    private readonly ILogger<MacSystemThumbnailProvider> _logger;

    public MacSystemThumbnailProvider(ILogger<MacSystemThumbnailProvider> logger)
    {
        _logger = logger;
    }

    public async ValueTask<SystemThumbnailResult> GetThumbnailAsync(
        string filePath,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        bool isDataLess;
        try
        {
            isDataLess = NativeMethods.IsDataLess(filePath) == 1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Without the native detector we cannot safely distinguish an ordinary file
            // from a File Provider placeholder. Never risk materializing a cloud file.
            _logger.LogWarning(ex, "The macOS placeholder detector is unavailable");
            return SystemThumbnailResult.UnavailableForPlaceholder;
        }

        if (!isDataLess)
        {
            return SystemThumbnailResult.NotApplicable;
        }

        try
        {
            var bytes = await Task.Run(
                () => GenerateThumbnail(filePath, width, height, cancellationToken),
                cancellationToken);
            return new SystemThumbnailResult(bytes, MustNotReadFile: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "The macOS thumbnail bridge is unavailable");
            return SystemThumbnailResult.UnavailableForPlaceholder;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Quick Look could not provide a thumbnail for {Path}", filePath);
            return SystemThumbnailResult.UnavailableForPlaceholder;
        }
    }

    private static byte[]? GenerateThumbnail(
        string filePath,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = NativeMethods.GenerateThumbnail(
            filePath,
            width,
            height,
            scale: 1.0,
            out var bytesPointer,
            out var bytesLength);

        if (result != 1 || bytesPointer == IntPtr.Zero || bytesLength <= 0 || bytesLength > int.MaxValue)
        {
            if (bytesPointer != IntPtr.Zero)
            {
                NativeMethods.FreeBuffer(bytesPointer);
            }

            return null;
        }

        try
        {
            var bytes = new byte[(int)bytesLength];
            Marshal.Copy(bytesPointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            NativeMethods.FreeBuffer(bytesPointer);
        }
    }

    private static class NativeMethods
    {
        [DllImport(NativeLibrary, EntryPoint = "monkasa_is_dataless")]
        internal static extern int IsDataLess([MarshalAs(UnmanagedType.LPUTF8Str)] string filePath);

        [DllImport(NativeLibrary, EntryPoint = "monkasa_generate_thumbnail")]
        internal static extern int GenerateThumbnail(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath,
            int width,
            int height,
            double scale,
            out IntPtr bytes,
            out nint length);

        [DllImport(NativeLibrary, EntryPoint = "monkasa_free_buffer")]
        internal static extern void FreeBuffer(IntPtr bytes);
    }
}
