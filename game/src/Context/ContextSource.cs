using AlleyCat.Character;
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
        IContextual subject,
        ISceneContext scene,
        ICharacter? observer);

    /// <summary>
    /// Validates a non-generic subject before delegating to a typed source implementation.
    /// </summary>
    /// <typeparam name="TContextual">Expected subject type.</typeparam>
    /// <param name="subject">Contextual subject supplied through the non-generic surface.</param>
    /// <returns>The subject as <typeparamref name="TContextual" />.</returns>
    protected static TContextual RequireCompatibleSubject<TContextual>(IContextual subject)
        where TContextual : IContextual
    {
        if (subject is TContextual typedSubject)
        {
            return typedSubject;
        }

        string expectedType = typeof(TContextual).FullName ?? typeof(TContextual).Name;
        string actualType = subject.GetType().FullName ?? subject.GetType().Name;
        throw new InvalidOperationException(
            $"Context source expected subject type {expectedType}, but received {actualType}.");
    }
}
