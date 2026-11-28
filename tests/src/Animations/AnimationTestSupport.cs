using AlleyCat.Animations;
using AlleyCat.Core.Content;
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

/// <summary>In-memory resource checks: listed files exist; clip/set verdicts are configurable per path.</summary>
internal sealed class FakePackResources : IAnimationPackResources
{
    public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> ClipErrors { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> SetErrors { get; } = new(StringComparer.Ordinal);

    public string ReadText(string resourcePath)
        => Texts.TryGetValue(resourcePath, out string? text) ? text : throw new FileNotFoundException(resourcePath);

    public bool Exists(string resourcePath) => Files.Contains(resourcePath);

    public string? CheckClip(string resourcePath) => ClipErrors.GetValueOrDefault(resourcePath);

    public string? CheckReviewedSet(string resourcePath) => SetErrors.GetValueOrDefault(resourcePath);
}

/// <summary>Builders for manifests used by catalogue tests.</summary>
internal static class PackBuilder
{
    public static AnimationPackManifest Pack(string id, params (AnimationCategory Category, string ID)[] entries)
        => new(id, $"res://packs/{id}/pack.json", ContentContext.Default,
            [.. entries.Select(e => new AnimationEntry(e.ID, e.Category, ["tag"], $"{e.ID} from {id}", id, $"{id}/{e.ID}", $"res://{id}/{e.ID}"))]);
}
