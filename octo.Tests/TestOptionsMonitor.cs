using Microsoft.Extensions.Options;

namespace Octo.Tests;

/// <summary>
/// Minimal IOptionsMonitor for tests. Options.Create only produces IOptions, and
/// the services under test deliberately take a monitor so admin-UI settings
/// changes reach them without a restart. Set() lets a test drive that reload.
/// </summary>
public sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = new();

    public TestOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; private set; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return null;
    }

    /// <summary>Stand in for settings.json being rewritten and reloaded.</summary>
    public void Set(T value)
    {
        CurrentValue = value;
        foreach (var l in _listeners) l(value, null);
    }
}

/// <summary>Factory so call sites keep type inference: TestOptions.Monitor(new X { ... }).</summary>
public static class TestOptions
{
    public static TestOptionsMonitor<T> Monitor<T>(T value) => new(value);
}
