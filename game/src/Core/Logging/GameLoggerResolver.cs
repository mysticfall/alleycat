using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Core.Logging;

/// <summary>
/// Resolves loggers from the scene-owned <see cref="Game" /> service provider without requiring constructor injection
/// for Godot nodes.
/// </summary>
internal static class GameLoggerResolver
{
    /// <summary>
    /// Resolves a required typed logger from the active game service provider.
    /// </summary>
    /// <typeparam name="T">Logger category type.</typeparam>
    /// <returns>The resolved logger.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the game service provider or logger is unavailable.</exception>
    public static ILogger<T> ResolveRequired<T>()
    {
        try
        {
            return Game.Instance.GetRequiredService<ILogger<T>>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            throw new InvalidOperationException(
                $"Unable to resolve required logger '{typeof(ILogger<T>).FullName}' from the active Game service provider.",
                ex);
        }
    }

    /// <summary>
    /// Attempts to resolve a typed logger from the active game service provider for paths where diagnostics are optional.
    /// </summary>
    /// <typeparam name="T">Logger category type.</typeparam>
    /// <param name="logger">Resolved logger, or <see langword="null" /> when no active provider/logger exists.</param>
    /// <returns><see langword="true" /> when a logger was resolved; otherwise <see langword="false" />.</returns>
    public static bool TryResolve<T>(out ILogger<T>? logger)
    {
        try
        {
            logger = Game.Instance.GetService<ILogger<T>>();
            return logger is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            logger = null;
            return false;
        }
    }
}
