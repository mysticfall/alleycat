namespace AlleyCat.Models;

/// <summary>
/// Session-wide registry of shared backends. Each key loads lazily and exactly once; a load failure is cached and
/// delivered to every requester without retry; loaded backends stay loaded until the owner shuts the registry down.
/// </summary>
public interface ISharedModelRegistry
{
    /// <summary>
    /// Resolves the shared backend for <paramref name="definition" />'s key, starting its single load off the calling
    /// thread on first request. Never blocks. Thread-safe.
    /// </summary>
    /// <typeparam name="TBackend">Opaque backend type.</typeparam>
    /// <param name="definition">Model-specific definition; only the first definition for a key is used to load.</param>
    /// <returns>The shared backend for the key.</returns>
    /// <exception cref="ObjectDisposedException">Thrown after shutdown started.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the key is already bound to another backend type.</exception>
    ISharedModel<TBackend> Resolve<TBackend>(ModelDefinition<TBackend> definition) where TBackend : class, IDisposable;
}
