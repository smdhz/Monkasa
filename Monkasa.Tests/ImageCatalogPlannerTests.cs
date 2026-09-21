using Monkasa.Services;
using Monkasa.ViewModels;
using Xunit;

namespace Monkasa.Tests;

public sealed class ImageCatalogPlannerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"monkasa-tests-{Guid.NewGuid():N}");

    public ImageCatalogPlannerTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void CreatePlan_DetectsAddedModifiedAndRemovedFiles()
    {
        var unchanged = CreateFile("same.jpg", "same", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var modified = CreateFile("changed.jpg", "new content", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var added = CreateFile("added.jpg", "added", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        var removedPath = Path.Combine(_directory, "removed.jpg");

        var current = new[]
        {
            VersionOf(unchanged),
            new ImageFileVersion(modified.FullName, modified.LastWriteTimeUtc.AddMinutes(-1).Ticks, 1),
            new ImageFileVersion(removedPath, 1, 1),
        };

        var plan = ImageCatalogPlanner.CreatePlan(
            current,
            new[] { unchanged, modified, added },
            ImageSortMode.Name,
            StringComparer.Ordinal);

        Assert.True(plan.HasChanges);
        Assert.Contains(added.FullName, plan.AddedPaths);
        Assert.Contains(modified.FullName, plan.ModifiedPaths);
        Assert.Contains(removedPath, plan.RemovedPaths);
        Assert.DoesNotContain(unchanged.FullName, plan.ModifiedPaths);
    }

    [Fact]
    public void CreatePlan_ReportsNoChangesForIdenticalSnapshot()
    {
        var image = CreateFile("same.jpg", "same", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var plan = ImageCatalogPlanner.CreatePlan(
            new[] { VersionOf(image) },
            new[] { image },
            ImageSortMode.Name,
            StringComparer.Ordinal);

        Assert.False(plan.HasChanges);
        Assert.Empty(plan.AddedPaths);
        Assert.Empty(plan.ModifiedPaths);
        Assert.Empty(plan.RemovedPaths);
    }

    [Fact]
    public void Sort_UsesNameOrNewestFirstAccordingToMode()
    {
        var alpha = CreateFile("alpha.jpg", "a", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        var beta = CreateFile("beta.jpg", "b", new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc));

        var byName = ImageCatalogPlanner.Sort(new[] { beta, alpha }, ImageSortMode.Name).ToArray();
        var byTime = ImageCatalogPlanner.Sort(new[] { alpha, beta }, ImageSortMode.Time).ToArray();

        Assert.Equal(new[] { "alpha.jpg", "beta.jpg" }, byName.Select(x => x.Name));
        Assert.Equal(new[] { "beta.jpg", "alpha.jpg" }, byTime.Select(x => x.Name));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private FileInfo CreateFile(string name, string contents, DateTime lastWriteTimeUtc)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
        return new FileInfo(path);
    }

    private static ImageFileVersion VersionOf(FileInfo file)
        => new(file.FullName, file.LastWriteTimeUtc.Ticks, file.Length);
}
