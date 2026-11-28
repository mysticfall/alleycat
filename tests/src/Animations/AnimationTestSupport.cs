using AlleyCat.Animations;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Tests.Animations;

/// <summary>Captures formatted log messages for assertions.</summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>Standalone animation entries for pure view and transition tests.</summary>
internal static class VariantTestEntries
{
    public static IReadOnlyList<AnimationEntry> Entries(params (AnimationCategory Category, string ID)[] entries)
        => [.. entries.Select(entry => new AnimationEntry(entry.ID, entry.Category, ["tag"], $"{entry.ID} fixture",
            "default", $"{entry.ID}.tres", $"res://{entry.ID}.tres"))];
}
