using System;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Monkasa.ViewModels;

public sealed partial class ImageItemViewModel : ObservableObject, IDisposable
{
    public ImageItemViewModel(FileInfo imageInfo)
    {
        ImageInfo = imageInfo;
        FileName = imageInfo.Name;
        FullPath = imageInfo.FullName;
    }

    public FileInfo ImageInfo { get; }

    public string FileName { get; }

    public string FullPath { get; }

    [ObservableProperty]
    private Bitmap? thumbnail;

    public void SetThumbnail(Bitmap bitmap)
    {
        var previous = Thumbnail;
        Thumbnail = bitmap;
        previous?.Dispose();
    }

    public void Dispose()
    {
        var previous = Thumbnail;
        Thumbnail = null;
        previous?.Dispose();
    }
}
