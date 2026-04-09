namespace Monkasa.Models;

public sealed class ThumbnailCacheEntry
{
    public string FilePath { get; set; } = string.Empty;

    public long LastWriteUtcTicks { get; set; }

    public long FileLength { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public byte[] ImageBytes { get; set; } = [];

    public long UpdatedUtcTicks { get; set; }
}
