using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Installer;
using AlleyCat.IntegrationTests.Mind.AI;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Xunit;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Characters;

public sealed partial class CharacterRuntimeSubsystemInstallerValidationIntegrationTests
{
    /// <summary>Approved Unicode-ID plan: retain the entire install-to-written-cycle observation window.</summary>
    [Headless]
    [Fact]
    public Task Install_KoreanIdentity_ResolvesInheritsVoiceAndWritesScriptedCycle()
        => AssertInstalledIdentityCycleAsync("강주영");

    /// <summary>The identical ASCII control distinguishes an invalid fixture from identity rejection.</summary>
    [Headless]
    [Fact]
    public Task Install_AsciiIdentity_ControlCompletesTheSameScriptedCycle()
        => AssertInstalledIdentityCycleAsync("unicode_control");

    /// <summary>The combined CORE-009/AI-011 candidate keeps the original window and adds Korean JSON readability.</summary>
    [Headless]
    [Fact]
    public Task Install_KoreanIdentity_CompletedCycleHasReadableJson()
        => AssertInstalledIdentityCycleAsync("강주영", requireReadableJson: true);

    private static async Task AssertInstalledIdentityCycleAsync(string id, bool requireReadableJson = false)
    {
        using var fixture = RuntimeInstallFixture.CreateWithActualRootHub();
        // Exercise final template-to-target identity copying at the real runtime subsystem boundary.
        fixture.TemplateCharacter.Id = id;
        CharacterHub character = Assert.IsType<CharacterHub>(fixture.TargetRoot, exactMatch: false);
        SceneInstallationResult result = new CharacterRuntimeSubsystemInstaller().Install(fixture.CreateContext());
        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
        Assert.Equal(id, character.Id);
        Assert.Equal($"char:{id}", ((IIdentifiable)character).FullId);
        ICharacter installed = character;
        Assert.Equal(id, installed.RequireVoice().Id);
        Assert.Equal($"voice:{id}", installed.RequireVoice().FullId);
        var context = new SceneContext([character]);
        Assert.Same(character, context.Resolve($"char:{id}"));
        await MindSessionTranscriptIntegrationTests.AssertInstalledCharacterCycleAsync(character, requireReadableJson);
    }
}
