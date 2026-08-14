using Microsoft.Extensions.Logging.Abstractions;
using Octo.Services.Admin;

namespace Octo.Tests;

/// <summary>
/// The picker exists to stop a user choosing a folder Octo cannot actually download
/// into, so the properties that matter are the ones that would quietly mislead:
/// reporting a path different from the one listed, claiming a folder is writable
/// because it merely exists, or failing a whole listing because one subdirectory
/// refused to be read.
/// </summary>
public class DirectoryBrowserTests
{
    private static DirectoryBrowser NewBrowser() =>
        new(NullLogger<DirectoryBrowser>.Instance);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "octo-browse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void TheReportedPathIsCanonicalSoItMatchesWhatWasListed()
    {
        var root = NewTempDir();
        try
        {
            var child = Path.Combine(root, "child");
            Directory.CreateDirectory(child);

            // Round-trips through the child and back up: the answer must describe
            // the directory actually listed, not the expression used to reach it.
            var result = NewBrowser().Browse(Path.Combine(child, ".."));

            Assert.True(result.Exists);
            Assert.Equal(Path.GetFullPath(root), result.Path);
            Assert.DoesNotContain("..", result.Path);
            Assert.Contains(result.Entries, e => e.Name == "child");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ListingReturnsSubdirectoriesAndNeverFileNames()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "albums"));
            File.WriteAllText(Path.Combine(root, "secret-track.flac"), "x");

            var result = NewBrowser().Browse(root);

            Assert.Contains(result.Entries, e => e.Name == "albums");
            Assert.DoesNotContain(result.Entries, e => e.Name.EndsWith(".flac"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AMissingPathIsReportedRatherThanThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "octo-not-here-" + Guid.NewGuid().ToString("N"));

        var result = NewBrowser().Browse(missing);

        Assert.False(result.Exists);
        Assert.Empty(result.Entries);
        Assert.False(result.Writable);
    }

    [Fact]
    public void WritabilityIsProvedByWritingNotByExistence()
    {
        var root = NewTempDir();
        try
        {
            var result = NewBrowser().Browse(root);
            Assert.True(result.Exists);
            Assert.True(result.Writable);

            // The negative case cannot be forced portably (Windows ACLs and Unix
            // modes diverge, and CI often runs as root, for whom mode 0500 is no
            // obstacle), so assert the probe leaves nothing behind instead. A probe
            // that littered the music library would be worse than no probe.
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AnEmptyPathStartsAtThePlatformRoot()
    {
        var result = NewBrowser().Browse(null);

        if (OperatingSystem.IsWindows())
        {
            // No single root to enumerate on Windows, so the drives are the entry point.
            Assert.NotEmpty(result.Entries);
            Assert.All(result.Entries, e => Assert.Contains(":", e.Path));
        }
        else
        {
            Assert.Equal("/", result.Path);
            Assert.True(result.Exists);
        }
    }

    [Fact]
    public void ParentIsOfferedForNavigationAndIsNullAtTheTop()
    {
        var root = NewTempDir();
        try
        {
            var child = Path.Combine(root, "nested");
            Directory.CreateDirectory(child);

            Assert.Equal(Path.GetFullPath(root), NewBrowser().Browse(child).Parent);

            var top = Path.GetPathRoot(Path.GetFullPath(root));
            Assert.Null(NewBrowser().Browse(top).Parent);
        }
        finally { Directory.Delete(root, true); }
    }
}
