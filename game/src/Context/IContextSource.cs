using AlleyCat.Scene;

namespace AlleyCat.Context;

/// <summary>
/// Provides contextual information for a contextual subject.
/// </summary>
public interface IContextSource
{
    /// <summary>
    /// Gets contextual information for the supplied subject within the current scene and optional observer.
    /// </summary>
    /// <param name="subject">Contextual subject being described.</param>
    /// <param name="scene">Current scene membership snapshot.</param>
    /// <param name="observer">Optional contextual observer.</param>
    /// <returns>Context entries contributed by this source, keyed by stable field name.</returns>
    IReadOnlyDictionary<string, object?> GetContext(IContextual subject, ISceneContext scene, IContextual? observer);
}

/// <summary>
/// Provides typed contextual information for a contextual subject.
/// </summary>
/// <typeparam name="TContextual">Subject type handled by this source.</typeparam>
public interface IContextSource<in TContextual> : IContextSource
    where TContextual : IContextual
{
    /// <summary>
    /// Gets contextual information for the supplied subject within the current scene and optional observer.
    /// </summary>
    /// <param name="subject">Contextual subject being described.</param>
    /// <param name="scene">Current scene membership snapshot.</param>
    /// <param name="observer">Optional contextual observer.</param>
    /// <returns>Context entries contributed by this source, keyed by stable field name.</returns>
    IReadOnlyDictionary<string, object?> GetContext(TContextual subject, ISceneContext scene, IContextual? observer);

    /// <inheritdoc />
    IReadOnlyDictionary<string, object?> IContextSource.GetContext(
        IContextual subject,
        ISceneContext scene,
        IContextual? observer)
    {
        if (subject is not TContextual typedSubject)
        {
            string expectedType = typeof(TContextual).FullName ?? typeof(TContextual).Name;
            string actualType = subject.GetType().FullName ?? subject.GetType().Name;
            throw new InvalidOperationException(
                $"Context source expected subject type {expectedType}, but received {actualType}.");
        }

        return GetContext(typedSubject, scene, observer);
    }
}
