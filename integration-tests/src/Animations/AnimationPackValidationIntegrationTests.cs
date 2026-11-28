using AlleyCat.Animations;
using AlleyCat.Core.Content;
using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.Animations;

/// <summary>
/// ANIM-003 pack-format Fixture Pack item 3 and Validator items 2–3 against real resources: every invalid test pack
/// fails through the production resource checks, and the validator command reports the default pack on both reference
/// templates.
/// </summary>
public sealed class AnimationPackValidationIntegrationTests
{
    private const string Fixtures = "res://tests/animation/catalogue/";
    private const string FemaleNPC = "res://assets/characters/templates/reference_female/reference_female_npc.tscn";
    private const string MaleNPC = "res://assets/characters/templates/reference_male/reference_male_npc.tscn";

    /// <summary>
    /// Each invalid fixture fails with one diagnostic on the expected field, including a clip that is a real
    /// non-Animation resource and a set the real ANIM-002 loader rejects.
    /// </summary>
    [Fact]
    [Headless]
    public void InvalidFixtures_FailThroughProductionResources()
    {
        var fixture = new ContentContext("fixture", Fixtures + "fixture/");
        (string Manifest, ContentContext Context, string? Field, string Message)[] cases =
        [
            ("fixture/invalid/malformed_json.json", fixture, null, "malformed JSON"),
            ("fixture/invalid/wrong_schema.json", fixture, "schema", "schema must be"),
            ("fixture/invalid/unknown_field.json", fixture, "mood", "unknown entry field"),
            ("fixture/invalid/missing_description.json", fixture, "description", "missing or blank"),
            ("fixture/invalid/empty_tags.json", fixture, "tags", "non-empty array"),
            ("fixture/invalid/bad_category.json", fixture, "category", "category must be"),
            ("fixture/invalid/duplicate_id.json", fixture, "id", "duplicate idle entry id"),
            ("fixture/invalid/absolute_path.json", fixture, "clip", "must be relative to the content root"),
            ("fixture/invalid/escaping_path.json", fixture, "clip", "must not escape the content root"),
            ("fixture/invalid/missing_file.json", fixture, "clip", "does not exist"),
            ("fixture/invalid/clip_not_animation.json", fixture, "clip", "loadable native Animation resource, found Curve"),
            ("extension/invalid/set_rejected.json", ContentContext.Default, "set", "the ANIM-002 reviewed-set loader rejects the set")
        ];

        foreach ((string manifest, ContentContext context, string? field, string message) in cases)
        {
            (AnimationPackManifest? loaded, IReadOnlyList<AnimationPackDiagnostic> diagnostics) =
                AnimationPackLoader.Validate(Fixtures + manifest, context, GodotAnimationPackResources.Instance);

            Assert.Null(loaded);
            AnimationPackDiagnostic diagnostic = Assert.Single(diagnostics);
            Assert.Equal(field, diagnostic.Field);
            Assert.Contains(message, diagnostic.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>The validator command passes the default pack and reports sad binding on both reference templates.</summary>
    [Fact]
    [Headless]
    public void ValidatorCommand_ReportsDefaultPackOnReferenceTemplates()
    {
        (AnimationPackValidationReport? report, string? usage) = AnimationPackValidationCommand.Execute(
            ["--pack=res://data/animations/default/pack.json", $"--template={FemaleNPC}", $"--template={MaleNPC}"]);

        Assert.Null(usage);
        Assert.NotNull(report);
        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Errors));
        Assert.Empty(report.IncompatibleEntries);
        foreach (string template in new[] { FemaleNPC, MaleNPC })
        {
            Assert.Contains($"Template '{template}': idle/sad (pack 'default') binds to skeleton 'GeneralSkeleton'.", report.Findings);
        }
    }
}
