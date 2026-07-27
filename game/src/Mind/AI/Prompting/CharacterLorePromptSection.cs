using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.AI.Lore;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Runtime-backed lore for every scene character from the owning character's perspective.
/// </summary>
[GlobalClass]
public partial class CharacterLorePromptSection : PromptSection
{
    /// <inheritdoc />
    public override async Task<string> GetContentAsync(
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildContext);

        ICharacter owner = buildContext.Character;
        ICharacter[] sceneCharacters = [.. buildContext.Scene.Characters];
        ValidateCharacterIdentity(owner);
        foreach (ICharacter character in sceneCharacters)
        {
            ValidateCharacterIdentity(character);
        }

        if (!sceneCharacters.Any(character => ReferenceEquals(character, owner)))
        {
            throw new InvalidOperationException(
                $"CharacterLorePromptSection requires owning character '{owner.FullId}' to be present in the scene context.");
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
                    $"CharacterLorePromptSection requires valid character and observer FullIds; runtime FullId was '{character.FullId}'.",
                    exception);
            }

            string canonicalSubjectID = subject.SubjectID!;
            if (runtimeIDsBySubject.TryGetValue(canonicalSubjectID, out string? existingRuntimeID))
            {
                throw new InvalidOperationException(
                    $"CharacterLorePromptSection cannot map distinct runtime character FullIds '{existingRuntimeID}' and '{character.FullId}' to the same canonical lore subject '{canonicalSubjectID}'.");
            }

            runtimeIDsBySubject.Add(canonicalSubjectID, character.FullId);
            subjects.Add(subject);
        }

        LoreQuery query;
        try
        {
            query = new LoreQuery(owner.FullId, subjects);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "CharacterLorePromptSection requires a non-empty, valid observer ID.",
                exception);
        }

        ILoreQueryService queryService = buildContext.Services.GetRequiredService<ILoreQueryService>();
        ILorePromptFormatter formatter = buildContext.Services.GetRequiredService<ILorePromptFormatter>();
        IReadOnlyList<LoreEntry> entries = await queryService.QueryAsync(
            buildContext.Scene.Content,
            query,
            cancellationToken);

        return formatter.Format(entries);
    }

    private static void ValidateCharacterIdentity(ICharacter character)
    {
        string fullId = character.FullId;
        try
        {
            IdentityValidator.Validate(character, nameof(character));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"CharacterLorePromptSection requires matching canonical character Type, ID, and FullId values; runtime FullId was '{fullId}'.",
                exception);
        }
    }
}
