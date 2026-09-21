using System;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Monkasa.Services;

namespace Monkasa.ViewModels;

public sealed partial class ImageItemViewModel : ObservableObject, IDisposable
{
    public ImageItemViewModel(FileInfo imageInfo)
    {
        ImageInfo = imageInfo;
        FileName = imageInfo.Name;
        FullPath = imageInfo.FullName;
    }

    public FileInfo ImageInfo { get; private set; }

    public string FileName { get; }

    public string FullPath { get; }

    public ImageFileVersion FileVersion => new(
        FullPath,
        ImageInfo.LastWriteTimeUtc.Ticks,
        ImageInfo.Length);

    [ObservableProperty]
    private Bitmap? thumbnail;

    public void SetThumbnail(Bitmap bitmap)
    {
        ReplaceThumbnail(bitmap);
    }

    public void UpdateImageInfo(FileInfo imageInfo)
    {
        ArgumentNullException.ThrowIfNull(imageInfo);

        if (!string.Equals(FullPath, imageInfo.FullName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The refreshed file must have the same path.", nameof(imageInfo));
        }

        ImageInfo = imageInfo;
        ReplaceThumbnail(null);
    }

    private void ReplaceThumbnail(Bitmap? bitmap)
    {
        var previous = Thumbnail;
        Thumbnail = bitmap;
        previous?.Dispose();
    }

    public void Dispose()
    {
        ReplaceThumbnail(null);
    }
}
