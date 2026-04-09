using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Monkasa.Models;

namespace Monkasa.Services;

public interface IThumbnailService
{
    Task<Bitmap?> GetThumbnailAsync(
        ImageFileInfo imageInfo,
        int width,
        int height,
        CancellationToken cancellationToken);

    Task<Bitmap?> GetPreviewAsync(
        string filePath,
        int maxSize,
        CancellationToken cancellationToken);
}
