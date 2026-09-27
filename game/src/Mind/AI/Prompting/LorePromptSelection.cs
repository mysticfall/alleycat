using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.AI.Lore;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Shared construction of the automatic lore selection behind the shared NPC stack: the essential-world query and
/// the scene-character query. The catalogue section reuses these exact queries to exclude precisely the entries
/// automatic injection selected — by entry ID, never through duplicated rules or whole-category removal
/// (AI-004 requirement 44).
/// </summary>
internal static class LorePromptSelection
{
    /// <summary>
    /// Creates the essential world query for the owning character. Lore identity is validated only when used
    /// (AI-004 requirement 17).
    /// </summary>
    public static LoreQuery CreateEssentialQuery(PromptSectionBuildContext buildContext, string sectionName)
    {
        ArgumentNullException.ThrowIfNull(buildContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        try
        {
            return LoreQuery.Essential(buildContext.Character.FullId);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"{sectionName} requires a non-empty, valid observer ID.",
                exception);
        }
    }

    /// <summary>
    /// Creates the scene-character query from the owning character's perspective: the owner first and every other
    /// scene character by ordinal exact <c>FullId</c> (AI-004 requirement 32). Invalid or duplicate scene character
    /// <c>FullId</c> values fail rather than silently merging lore (AI-004 requirement 33).
    /// </summary>
    public static LoreQuery CreateSceneCharacterQuery(PromptSectionBuildContext buildContext, string sectionName)
    {
        ArgumentNullException.ThrowIfNull(buildContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        ICharacter owner = buildContext.Character;
        ICharacter[] sceneCharacters = [.. buildContext.Scene.Characters];
        ValidateCharacterIdentity(owner, sectionName);
        foreach (ICharacter character in sceneCharacters)
        {
            ValidateCharacterIdentity(character, sectionName);
        }

        if (!sceneCharacters.Any(character => ReferenceEquals(character, owner)))
        {
            throw new InvalidOperationException(
                $"{sectionName} requires owning character '{owner.FullId}' to be present in the scene context.");
        }

        ICharacter[] orderedCharacters =
        [
            owner,
            .. sceneCharacters
                .Where(character => !ReferenceEquals(character, owner))
                .OrderBy(character => character.FullId, StringComparer.Ordinal),
        ];

        List<LoreSubjectRequest> subjects = new(orderedCharacters.Length);
        Dictionary<string, string> runtimeIDsBySubject = new(StringComparer.Ordinal);
        foreach (ICharacter character in orderedCharacters)
        {
            LoreSubjectRequest subject;
            try
            {
                subject = LoreSubjectRequest.Character(character.FullId);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(
                    $"{sectionName} requires valid character and observer FullIds; runtime FullId was '{character.FullId}'.",
                    exception);
            }

            string canonicalSubjectID = subject.SubjectID!;
            if (runtimeIDsBySubject.TryGetValue(canonicalSubjectID, out string? existingRuntimeID))
            {
                throw new InvalidOperationException(
                    $"{sectionName} cannot map distinct runtime character FullIds '{existingRuntimeID}' and "
                    + $"'{character.FullId}' to the same canonical lore subject '{canonicalSubjectID}'.");
            }

            runtimeIDsBySubject.Add(canonicalSubjectID, character.FullId);
            subjects.Add(subject);
        }

        try
        {
            return new LoreQuery(owner.FullId, subjects);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"{sectionName} requires a non-empty, valid observer ID.",
                exception);
        }
    }

    /// <summary>
    /// Runs one contextual lore query and formats its entries through the shared lore body formatter.
    /// </summary>
    public static async Task<string> QueryAndFormatAsync(
        PromptSectionBuildContext buildContext,
        LoreQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buildContext);
        ArgumentNullException.ThrowIfNull(query);

        ILoreQueryService queryService = buildContext.Services.GetRequiredService<ILoreQueryService>();
        ILorePromptFormatter formatter = buildContext.Services.GetRequiredService<ILorePromptFormatter>();
        IReadOnlyList<LoreEntry> entries = await queryService.QueryAsync(
            buildContext.Scene.Content,
            query,
            cancellationToken);

        return formatter.Format(entries);
    }

    private static void ValidateCharacterIdentity(ICharacter character, string sectionName)
    {
        string fullId = character.FullId;
        try
        {
            IdentityValidator.Validate(character, nameof(character));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"{sectionName} requires matching canonical character Type, ID, and FullId values; runtime FullId "
                + $"was '{fullId}'.",
                exception);
        }
    }
}
