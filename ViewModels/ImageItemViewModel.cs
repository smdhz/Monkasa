using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Monkasa.Models;

namespace Monkasa.ViewModels;

public sealed partial class ImageItemViewModel : ObservableObject, IDisposable
{
    public ImageItemViewModel(ImageFileInfo imageInfo)
    {
        ImageInfo = imageInfo;
        FileName = imageInfo.FileName;
        FullPath = imageInfo.FullPath;
    }

    public ImageFileInfo ImageInfo { get; }

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
