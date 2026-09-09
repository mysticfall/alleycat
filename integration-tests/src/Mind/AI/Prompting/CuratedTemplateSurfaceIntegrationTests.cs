using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Scene;
using AlleyCat.Templating;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Mind.AI.Prompting;

/// <summary>
/// Godot-runtime coverage proving the curated member-access policy seals real scene characters end-to-end: a
/// production render context over an instantiated authored character renders exactly the canonical
/// <c>FullId</c> through the real engine, while every non-curated member — including the Godot <c>Node</c>
/// surface — renders nil (TMPL-001 TR-14).
/// </summary>
[Headless]
public sealed class CuratedTemplateSurfaceIntegrationTests
{
    private const string AuthoredNpcPath = "res://assets/characters/reference/ally_npc.tscn";

    /// <summary>
    /// Raw scene characters placed in a production render context expose exactly their curated identity when
    /// rendered through the real engine: <c>FullId</c> resolves for the owner and the player, while the
    /// non-curated <c>Id</c>, <c>Components</c>, and the Godot <c>Node</c> surface render empty.
    /// </summary>
    [Fact]
    public async Task RealEngineRender_WithSceneCharacter_ExposesExactlyTheCuratedFullId()
    {
        CharacterHub owner = Assert.IsType<CharacterHub>(
            ResourceLoader.Load<PackedScene>(AuthoredNpcPath).Instantiate(),
            exactMatch: false);
        FixturePlayerCharacter player = new();

        try
        {
            SceneContext scene = new([owner, player]);
            IReadOnlyDictionary<string, object?> renderContext = AgenticMind.CreateRenderContext(owner, scene);

            ITemplate template = new FluidTemplateCompiler().Compile(
                "Owner:{{ character.FullId }}|Id:{{ character.Id }}|Components:{{ character.Components }}" +
                "|NodeName:{{ character.Name }}|Player:{{ player.FullId }}|PlayerComponents:{{ player.Components }}");

            string output = await template.RenderAsync(renderContext);

            Assert.Equal(
                "Owner:char:ally|Id:|Components:|NodeName:|Player:char:fixture_player|PlayerComponents:",
                output);
        }
        finally
        {
            owner.Free();
            player.Free();
        }
    }
}
