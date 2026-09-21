using System.Threading;
using System.Threading.Tasks;

namespace Monkasa.Services;

public readonly record struct SystemThumbnailResult(byte[]? ImageBytes, bool MustNotReadFile)
{
    public static SystemThumbnailResult NotApplicable => new(null, false);

    public static SystemThumbnailResult UnavailableForPlaceholder => new(null, true);
}

public interface ISystemThumbnailProvider
{
    ValueTask<SystemThumbnailResult> GetThumbnailAsync(
        string filePath,
        int width,
        int height,
        CancellationToken cancellationToken);
}
