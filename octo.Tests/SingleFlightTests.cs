using Octo.Services.Common;

namespace Octo.Tests;

/// <summary>
/// Single-flight is what stops one typed query from running the discovery pipeline several
/// times over. The dangerous part is not the happy path but what a failure leaves behind:
/// a keyed dictionary of tasks that outlives its usefulness becomes a user-triggerable
/// leak, and a retained faulted task turns one bad moment into a permanently broken query.
/// </summary>
public class SingleFlightTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ConcurrentCallersForOneKeyShareASingleExecution()
    {
        var flight = new SingleFlight<string, int>();
        var runs = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult();
            await release.Task;
            return 42;
        }

        // Hold the first call open so the others cannot miss it by finishing too early.
        var first = flight.RunAsync("q", Factory, Generous);
        await started.Task;

        var joiners = Enumerable.Range(0, 8)
            .Select(_ => flight.RunAsync("q", Factory, Generous))
            .ToArray();

        release.SetResult();
        var results = await Task.WhenAll(joiners.Prepend(first));

        Assert.Equal(1, runs);
        Assert.All(results, r => Assert.Equal(42, r));
    }

    [Fact]
    public async Task DifferentKeysDoNotShareAnExecution()
    {
        var flight = new SingleFlight<string, string>();

        var results = await Task.WhenAll(
            flight.RunAsync("a", _ => Task.FromResult("a"), Generous),
            flight.RunAsync("b", _ => Task.FromResult("b"), Generous));

        Assert.Equal(new[] { "a", "b" }, results);
    }

    [Fact]
    public async Task AFailedExecutionReachesEveryJoinerAndIsNotRetained()
    {
        var flight = new SingleFlight<string, int>();
        var runs = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Failing(CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("upstream is down");
        }

        var first = flight.RunAsync("q", Failing, Generous);
        await started.Task;
        var joiner = flight.RunAsync("q", Failing, Generous);

        release.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => joiner);
        Assert.Equal(1, runs);

        // The entry must be gone, or one transient outage would poison this key for the
        // life of the process.
        Assert.Equal(0, flight.InFlightCount);
        Assert.Equal(7, await flight.RunAsync("q", _ => Task.FromResult(7), Generous));
    }

    [Fact]
    public async Task ASynchronouslyThrowingFactoryIsAlsoNotRetained()
    {
        var flight = new SingleFlight<string, int>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => flight.RunAsync("q", _ => throw new InvalidOperationException("boom"), Generous));

        Assert.Equal(0, flight.InFlightCount);
        Assert.Equal(1, await flight.RunAsync("q", _ => Task.FromResult(1), Generous));
    }

    [Fact]
    public async Task ACompletedKeyRunsAgainOnTheNextCall()
    {
        var flight = new SingleFlight<string, int>();
        var runs = 0;

        for (var i = 0; i < 3; i++)
        {
            await flight.RunAsync("q", _ => Task.FromResult(Interlocked.Increment(ref runs)), Generous);
        }

        // Nothing is cached, so results stay fresh and no eviction policy is needed.
        Assert.Equal(3, runs);
        Assert.Equal(0, flight.InFlightCount);
    }

    [Fact]
    public async Task TheFactoryTokenIsCancelledByTheDeadline()
    {
        var flight = new SingleFlight<string, bool>();

        // A hung dependency must not pin the key. The token the factory receives belongs to
        // the execution, never to a caller: joined callers share it, so one client
        // disconnecting cannot take the others down with it.
        var cancelled = await flight.RunAsync("q", async ct =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        }, TimeSpan.FromMilliseconds(150));

        Assert.True(cancelled);
        Assert.Equal(0, flight.InFlightCount);
    }
}
