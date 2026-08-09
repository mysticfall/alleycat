using AlleyCat.Body.Voice;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Characters;

/// <summary>
/// Integration coverage for identity-independent use of the reference-female player role.
/// </summary>
public sealed class AlternatePlayerIdentityIntegrationTests
{
    private const string FixtureScenePath =
        "res://assets/testing/alternate_player_identity/alternate_player_identity.tscn";

    /// <summary>
    /// A concrete non-Ally player retains its authored identity and assigns it to the local voice attribution.
    /// </summary>
    [Headless]
    [Fact]
    public void AlternateIdentity_UsingFemalePlayerRole_PreservesIdentityAndRoleDiscovery()
    {
        CharacterHub character = Assert.IsType<CharacterHub>(
            LoadPackedScene(FixtureScenePath).Instantiate(),
            exactMatch: false);

        try
        {
            EnsureCharacterRuntimeInstalled(character);

            Assert.Equal("riley", character.Id);
            Assert.NotEqual("ally", character.Id);
            Assert.Equal(character.Id, Assert.IsAssignableFrom<Voice>(character.Voice).Id);
            Assert.True(character.IsInGroup("Player"));
            Assert.True(character.IsInGroup("Actors"));

            ContextSource contextSource = Assert.Single(character.ContextSources);
            _ = Assert.IsType<CharacterCardContextSource>(contextSource, exactMatch: false);
            IReadOnlyDictionary<string, object?> characterCard = character.GetContext(
                new SceneContext([character]),
                observer: null);
            Assert.Equal("char:riley", Assert.Single(characterCard).Value);
        }
        finally
        {
            character.QueueFree();
        }
    }
}
