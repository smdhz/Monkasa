using System.Threading;
using System.Threading.Tasks;

namespace Monkasa.Services;

public sealed class NullSystemThumbnailProvider : ISystemThumbnailProvider
{
    public ValueTask<SystemThumbnailResult> GetThumbnailAsync(
        string filePath,
        int width,
        int height,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(SystemThumbnailResult.NotApplicable);
}
