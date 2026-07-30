using AlleyCat.Core;
using AlleyCat.Scene;
using Godot;

namespace AlleyCat.Context;

/// <summary>
/// Godot-authored resource base for context sources.
/// </summary>
[GlobalClass]
public abstract partial class ContextSource : Resource, IContextSource
{
    /// <inheritdoc />
    public abstract IReadOnlyDictionary<string, object?> GetContext(
        IIdentifiable subject,
        ISceneContext scene,
        IIdentifiable? observer);

    /// <summary>
    /// Validates a non-generic subject before delegating to a typed source implementation.
    /// </summary>
    /// <typeparam name="TSubject">Expected subject type.</typeparam>
    /// <param name="subject">Identifiable subject supplied through the non-generic surface.</param>
    /// <returns>The subject as <typeparamref name="TSubject" />.</returns>
    protected static TSubject RequireCompatibleSubject<TSubject>(IIdentifiable subject)
        where TSubject : IIdentifiable
    {
        if (subject is TSubject typedSubject)
        {
            return typedSubject;
        }

        string expectedType = typeof(TSubject).FullName ?? typeof(TSubject).Name;
        string actualType = subject.GetType().FullName ?? subject.GetType().Name;
        throw new InvalidOperationException(
            $"Context source expected subject type {expectedType}, but received {actualType}.");
    }
}
