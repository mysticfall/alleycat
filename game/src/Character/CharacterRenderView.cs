namespace AlleyCat.Character;

/// <summary>
/// Immutable curated view of a character for session prompt rendering (AI-003 TR-30): it exposes exactly
/// <see cref="FullId" />, so live component state, such as voice configuration, stays out of authored prompts,
/// preserving render determinism and prompt hygiene.
/// </summary>
public sealed class CharacterRenderView
{
    private readonly ICharacter _character;

    /// <summary>
    /// Creates a curated render view wrapping the supplied character.
    /// </summary>
    /// <param name="character">Character whose canonical identity this view exposes.</param>
    public CharacterRenderView(ICharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        _character = character;
    }

    /// <summary>Gets the wrapped character's exact canonical authored identity.</summary>
    public string FullId => _character.FullId;
}
