namespace Monkasa.Models;

public sealed record ImageFileInfo(
    string FullPath,
    string FileName,
    long FileLength,
    long LastWriteUtcTicks);
