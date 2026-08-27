using Microsoft.Extensions.Logging;
using Octo.Services.Admin;

namespace Octo.Tests;

public sealed class AdminLogBufferTests
{
    [Fact]
    public void Subscribe_ReplaysOnlyTheBoundedTailInOrder()
    {
        using var buffer = new AdminLogBuffer(2);
        var logger = buffer.CreateLogger("Octo.Radio");
        logger.LogInformation("first");
        logger.LogWarning("second");
        logger.LogError("third");

        using var subscription = buffer.Subscribe();

        Assert.True(subscription.Reader.TryRead(out var second));
        Assert.True(subscription.Reader.TryRead(out var third));
        Assert.False(subscription.Reader.TryRead(out _));
        Assert.Equal("second", second.Message);
        Assert.Equal(LogLevel.Warning, second.Level);
        Assert.Equal("third", third.Message);
        Assert.True(second.Sequence < third.Sequence);
    }

    [Fact]
    public void Subscribe_AfterSequenceReplaysNewerEntriesAndReceivesLiveEntries()
    {
        using var buffer = new AdminLogBuffer(10);
        var logger = buffer.CreateLogger("Octo.Radio");
        logger.LogInformation("before");

        using var initial = buffer.Subscribe();
        Assert.True(initial.Reader.TryRead(out var before));

        using var resumed = buffer.Subscribe(before.Sequence);
        Assert.False(resumed.Reader.TryRead(out _));

        logger.LogWarning("after");

        Assert.True(resumed.Reader.TryRead(out var after));
        Assert.Equal("after", after.Message);
        Assert.Equal("Octo.Radio", after.Category);
    }

    [Fact]
    public void Logger_PreservesFormattedMessageAndExceptionForTroubleshooting()
    {
        using var buffer = new AdminLogBuffer(10);
        var logger = buffer.CreateLogger("Octo.Test");
        var exception = new InvalidOperationException("provider unavailable");

        logger.LogError(exception, "Warmup failed for {Station}", "Daily Mix");

        using var subscription = buffer.Subscribe();
        Assert.True(subscription.Reader.TryRead(out var entry));
        Assert.Equal("Warmup failed for Daily Mix", entry.Message);
        Assert.Contains("InvalidOperationException", entry.Exception);
        Assert.Contains("provider unavailable", entry.Exception);
    }

    [Fact]
    public void FrameworkNoise_DoesNotEvictOctoEntries()
    {
        using var buffer = new AdminLogBuffer(2);
        var radio = buffer.CreateLogger("Octo.Services.LastFm.Radio");
        var framework = buffer.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
        radio.LogInformation("radio warmup started");
        for (var index = 0; index < 10; index++)
            framework.LogInformation("health request {Index}", index);

        using var subscription = buffer.Subscribe();
        var entries = new List<AdminLogEntry>();
        while (subscription.Reader.TryRead(out var entry)) entries.Add(entry);

        Assert.Contains(entries, entry => entry.Message == "radio warmup started");
        Assert.Equal(2, entries.Count(entry => entry.Category.StartsWith("Microsoft.", StringComparison.Ordinal)));
    }
}
