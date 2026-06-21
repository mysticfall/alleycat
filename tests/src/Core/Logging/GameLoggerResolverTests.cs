using AlleyCat.Core.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.Tests.Core.Logging;

/// <summary>
/// Unit coverage for Godot-node logger resolution safety before the game singleton is available.
/// </summary>
public sealed class GameLoggerResolverTests
{
    /// <summary>
    /// Godot nodes must fail clearly when logger resolution is attempted before services exist.
    /// </summary>
    [Fact]
    public void ResolveRequired_WhenGameSingletonUnavailable_ThrowsClearError()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            GameLoggerResolver.ResolveRequired<GameLoggerResolverTests>);

        Assert.Contains("Unable to resolve required logger", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Optional logger resolution is explicit and safe for isolated test scenes without the game singleton.
    /// </summary>
    [Fact]
    public void TryResolve_WhenGameSingletonUnavailable_ReturnsFalse()
    {
        bool resolved = GameLoggerResolver.TryResolve(out ILogger<GameLoggerResolverTests>? logger);

        Assert.False(resolved);
        Assert.Null(logger);
    }

    /// <summary>
    /// Required factory resolution should still fail clearly for callers that need logging infrastructure.
    /// </summary>
    [Fact]
    public void ResolveFactoryRequired_WhenGameSingletonUnavailable_ThrowsClearError()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            GameLoggerResolver.ResolveFactoryRequired);

        Assert.Contains("Unable to resolve required logger factory", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Optional factory resolution lets diagnostic-only listeners no-op in isolated test scenes.
    /// </summary>
    [Fact]
    public void TryResolveFactory_WhenGameSingletonUnavailable_ReturnsFalse()
    {
        bool resolved = GameLoggerResolver.TryResolveFactory(out ILoggerFactory? loggerFactory);

        Assert.False(resolved);
        Assert.Null(loggerFactory);
    }
}
