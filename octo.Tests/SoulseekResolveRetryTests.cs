using Octo.Services.Soulseek;
using Octo.Services.Common;

namespace Octo.Tests;

/// <summary>
/// slskd marks a transfer Succeeded before moving the file out of its incomplete
/// directory, and on bind mounts that move is a copy that can take seconds. The
/// one-shot disk check used to miss the mid-move file, fail the attempt, and
/// re-download the same track from the next peer. These tests pin the bounded
/// re-poll that closes that window.
/// </summary>
public class SoulseekResolveRetryTests
{
    [Fact]
    public async Task ResolvesImmediately_WithoutWaiting()
    {
        var calls = 0;
        var result = await SoulseekDownloadService.RetryResolveAsync(
            () => { calls++; return "/music/song.flac"; },
            maxWait: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        Assert.Equal("/music/song.flac", result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ResolvesWhenFileAppearsMidWindow()
    {
        var calls = 0;
        var result = await SoulseekDownloadService.RetryResolveAsync(
            () => ++calls >= 3 ? "/music/song.flac" : null,
            maxWait: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        Assert.Equal("/music/song.flac", result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task GivesUpAfterMaxWait()
    {
        var result = await SoulseekDownloadService.RetryResolveAsync(
            () => null,
            maxWait: TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CancelledCaller_GetsOneFinalCheckInsteadOfTheWindow()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var calls = 0;
        var result = await SoulseekDownloadService.RetryResolveAsync(
            () => ++calls >= 2 ? "/music/song.flac" : null,
            maxWait: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromSeconds(30),
            cts.Token);

        // First check misses, the delay is cancelled, the final check lands.
        Assert.Equal("/music/song.flac", result);
        Assert.Equal(2, calls);
    }
}

/// <summary>
/// A Windows drive-letter path configured inside a Linux container is silently
/// created as a literal directory name; the detector behind the startup warning
/// must catch that shape and nothing else.
/// </summary>
public class WindowsDrivePathDetectionTests
{
    [Theory]
    [InlineData(@"E:\Media\Music")]
    [InlineData("E:/Media/Music")]
    [InlineData(@"c:\music")]
    public void DrivePaths_AreDetected(string path)
        => Assert.True(PathHelper.LooksLikeWindowsDrivePath(path));

    [Theory]
    [InlineData("/music")]
    [InlineData("./downloads")]
    [InlineData("music")]
    [InlineData("E:")]
    [InlineData("")]
    [InlineData(null)]
    public void NonDrivePaths_AreNot(string? path)
        => Assert.False(PathHelper.LooksLikeWindowsDrivePath(path));
}
