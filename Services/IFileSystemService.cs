using System.Collections.Generic;
using Monkasa.Models;

namespace Monkasa.Services;

public interface IFileSystemService
{
    IReadOnlyList<string> GetDirectories(string path);

    IReadOnlyList<ImageFileInfo> GetImages(string path);

    void DeleteFile(string path);

    void DeleteDirectory(string path, bool recursive);
}
