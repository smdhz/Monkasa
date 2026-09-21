using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Monkasa.Services;

[SupportedOSPlatform("windows10.0.16299")]
public sealed class WindowsSystemThumbnailProvider : ISystemThumbnailProvider
{
    private const uint CfPlaceholderStatePlaceholder = 0x00000001;
    private const uint CfPlaceholderStatePartial = 0x00000010;
    private const uint CfPlaceholderStatePartiallyOnDisk = 0x00000020;
    private const uint CfPlaceholderStateInvalid = 0xffffffff;
    private const uint FileAttributeRecallOnOpen = 0x00040000;
    private const uint FileAttributeRecallOnDataAccess = 0x00400000;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private readonly ILogger<WindowsSystemThumbnailProvider> _logger;

    public WindowsSystemThumbnailProvider(ILogger<WindowsSystemThumbnailProvider> logger)
    {
        _logger = logger;
    }

    public async ValueTask<SystemThumbnailResult> GetThumbnailAsync(
        string filePath,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        if (!IsUnhydratedPlaceholder(filePath))
        {
            return SystemThumbnailResult.NotApplicable;
        }

        try
        {
            var bytes = await Task.Run(
                () => GetShellThumbnail(filePath, width, height, cancellationToken),
                cancellationToken);
            return new SystemThumbnailResult(bytes, MustNotReadFile: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Windows Shell could not provide a thumbnail for {Path}", filePath);
            return SystemThumbnailResult.UnavailableForPlaceholder;
        }
    }

    private static bool IsUnhydratedPlaceholder(string filePath)
    {
        var findHandle = NativeMethods.FindFirstFile(filePath, out var findData);
        if (findHandle == NativeMethods.InvalidHandleValue)
        {
            return false;
        }

        NativeMethods.FindClose(findHandle);

        var attributes = findData.FileAttributes;
        var hasRecallAttribute = (attributes & (FileAttributeRecallOnDataAccess | FileAttributeRecallOnOpen)) != 0;

        try
        {
            var state = NativeMethods.CfGetPlaceholderStateFromAttributeTag(attributes, findData.Reserved0);
            if (state == CfPlaceholderStateInvalid)
            {
                return hasRecallAttribute;
            }

            var isPlaceholder = (state & CfPlaceholderStatePlaceholder) != 0;
            var isPartial = (state & (CfPlaceholderStatePartial | CfPlaceholderStatePartiallyOnDisk)) != 0;
            return hasRecallAttribute || (isPlaceholder && isPartial);
        }
        catch (DllNotFoundException)
        {
            return hasRecallAttribute;
        }
        catch (EntryPointNotFoundException)
        {
            return hasRecallAttribute;
        }
    }

    private static byte[]? GetShellThumbnail(
        string filePath,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var initializeResult = NativeMethods.CoInitializeEx(IntPtr.Zero, 0);
        var shouldUninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != RpcEChangedMode)
        {
            Marshal.ThrowExceptionForHR(initializeResult);
        }

        IShellItemImageFactory? imageFactory = null;
        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            var interfaceId = typeof(IShellItemImageFactory).GUID;
            var createResult = NativeMethods.SHCreateItemFromParsingName(
                filePath,
                IntPtr.Zero,
                ref interfaceId,
                out imageFactory);
            Marshal.ThrowExceptionForHR(createResult);

            var imageResult = imageFactory!.GetImage(
                new NativeSize(Math.Max(32, width), Math.Max(32, height)),
                ShellItemImageFactoryFlags.ThumbnailOnly | ShellItemImageFactoryFlags.BiggerSizeOk,
                out bitmapHandle);
            Marshal.ThrowExceptionForHR(imageResult);

            cancellationToken.ThrowIfCancellationRequested();
            return EncodeBitmapAsPng(bitmapHandle);
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }

            if (imageFactory is not null)
            {
                Marshal.FinalReleaseComObject(imageFactory);
            }

            if (shouldUninitialize)
            {
                NativeMethods.CoUninitialize();
            }
        }
    }

    private static byte[]? EncodeBitmapAsPng(IntPtr bitmapHandle)
    {
        if (NativeMethods.GetObject(bitmapHandle, Marshal.SizeOf<NativeBitmap>(), out var nativeBitmap) == 0
            || nativeBitmap.Width <= 0
            || nativeBitmap.Height == 0)
        {
            return null;
        }

        var width = nativeBitmap.Width;
        var height = Math.Abs(nativeBitmap.Height);
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        var bitmapInfo = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
                SizeImage = checked((uint)(width * height * 4)),
            },
        };

        var deviceContext = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            var copiedLines = NativeMethods.GetDIBits(
                deviceContext,
                bitmapHandle,
                0,
                (uint)height,
                bitmap.GetPixels(),
                ref bitmapInfo,
                0);
            if (copiedLines == 0)
            {
                return null;
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, deviceContext);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded?.ToArray();
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ShellItemImageFactoryFlags flags, out IntPtr bitmapHandle);
    }

    [Flags]
    private enum ShellItemImageFactoryFlags : uint
    {
        BiggerSizeOk = 0x00000001,
        ThumbnailOnly = 0x00000008,
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize
    {
        public NativeSize(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public readonly int Width;

        public readonly int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }

    private static class NativeMethods
    {
        internal static readonly IntPtr InvalidHandleValue = new(-1);

        [DllImport("ole32.dll")]
        internal static extern int CoInitializeEx(IntPtr reserved, uint concurrencyModel);

        [DllImport("ole32.dll")]
        internal static extern void CoUninitialize();

        [DllImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName", CharSet = CharSet.Unicode)]
        internal static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr bindContext,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? imageFactory);

        [DllImport("kernel32.dll", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindFirstFile(string fileName, out Win32FindData findData);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindClose(IntPtr findHandle);

        [DllImport("cldapi.dll")]
        internal static extern uint CfGetPlaceholderStateFromAttributeTag(uint fileAttributes, uint reparseTag);

        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
        internal static extern int GetObject(IntPtr handle, int bufferSize, out NativeBitmap bitmap);

        [DllImport("gdi32.dll")]
        internal static extern int GetDIBits(
            IntPtr deviceContext,
            IntPtr bitmap,
            uint startScan,
            uint scanLines,
            IntPtr bits,
            ref BitmapInfo bitmapInfo,
            uint usage);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr windowHandle);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);
    }
}
