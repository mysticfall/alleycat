using AlleyCat.Core;
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
    /// <param name="subject">Identifiable subject being described.</param>
    /// <param name="scene">Current scene membership snapshot.</param>
    /// <param name="observer">Optional identifiable observer.</param>
    /// <returns>Context entries contributed by this source, keyed by stable field name.</returns>
    IReadOnlyDictionary<string, object?> GetContext(IIdentifiable subject, ISceneContext scene, IIdentifiable? observer);
}

/// <summary>
/// Provides typed contextual information for an identifiable subject.
/// </summary>
/// <typeparam name="TSubject">Subject type handled by this source.</typeparam>
public interface IContextSource<in TSubject> : IContextSource
    where TSubject : IIdentifiable
{
    /// <summary>
    /// Gets contextual information for the supplied subject within the current scene and optional observer.
    /// </summary>
    /// <param name="subject">Identifiable subject being described.</param>
    /// <param name="scene">Current scene membership snapshot.</param>
    /// <param name="observer">Optional identifiable observer.</param>
    /// <returns>Context entries contributed by this source, keyed by stable field name.</returns>
    IReadOnlyDictionary<string, object?> GetContext(TSubject subject, ISceneContext scene, IIdentifiable? observer);

    /// <inheritdoc />
    IReadOnlyDictionary<string, object?> IContextSource.GetContext(
        IIdentifiable subject,
        ISceneContext scene,
        IIdentifiable? observer)
    {
        if (subject is not TSubject typedSubject)
        {
            string expectedType = typeof(TSubject).FullName ?? typeof(TSubject).Name;
            string actualType = subject.GetType().FullName ?? subject.GetType().Name;
            throw new InvalidOperationException(
                $"Context source expected subject type {expectedType}, but received {actualType}.");
        }

        return GetContext(typedSubject, scene, observer);
    }
}
