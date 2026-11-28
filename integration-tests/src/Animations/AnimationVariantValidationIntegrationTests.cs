using AlleyCat.Animations;
using AlleyCat.Core.Content;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlleyCat.IntegrationTests.Animations;

/// <summary>Exercise shared ANIM-003 validation against real descriptors, clips, sets and template chains.</summary>
public sealed class AnimationVariantValidationIntegrationTests
{
    private const string Invalid = "res://tests/animation/catalogue/fixture/invalid/";
    private const string Female = "res://assets/characters/templates/reference_female/reference_female_npc.tscn";
    private const string Male = "res://assets/characters/templates/reference_male/reference_male_npc.tscn";
    private const string IdleFixtures = "res://tests/animation/catalogue/fixture/idle/";

    /// <summary>A terminal newline in an otherwise valid id is not part of a lowercase identifier.</summary>
    [Fact]
    [Headless]
    public void Descriptor_RejectsTrailingNewlineInID() => AssertInvalidVariant(IdleFixtures + "trailing_newline_id.json", "id");

    /// <summary>A terminal newline in an otherwise valid tag is not part of a lowercase word.</summary>
    [Fact]
    [Headless]
    public void Descriptor_RejectsTrailingNewlineInTag() => AssertInvalidVariant(IdleFixtures + "trailing_newline_tag.json", "tags");

    /// <summary>Remove then add in two nodes of one authored template cannot implement an implicit replacement.</summary>
    [Fact]
    [Headless]
    public void Template_RejectsSameSceneSplitReplacementWithoutPublishing()
    {
        const string path = Invalid + "split_replacement.tscn";
        PackedScene template = ResourceLoader.Load<PackedScene>(path);
        Node root = template.Instantiate();
        try
        {
            var catalogue = AnimationCatalogue.RequireFor(root);
            var skeleton = (Skeleton3D)root.FindChild("GeneralSkeleton", true, false);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                catalogue.Build(skeleton, ContentContext.Default, NullLogger.Instance, template));
            Assert.False(catalogue.IsBuilt);
            Assert.Contains(path, error.Message, StringComparison.Ordinal);
            Assert.Contains("multiple layers", error.Message, StringComparison.Ordinal);
            Assert.Contains("AnimationCatalogue/Removal", error.Message, StringComparison.Ordinal);
            Assert.Contains("AnimationCatalogue/Addition", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Free();
        }

        (AnimationVariantValidationReport? report, string? usage) = AnimationVariantValidationCommand.Execute([$"--template={path}"]);
        Assert.Null(usage);
        Assert.NotNull(report);
        Assert.False(report.IsValid);
        Assert.Empty(report.Findings);
        Assert.Contains("multiple layers", Assert.Single(report.Errors), StringComparison.Ordinal);
    }

    private static void AssertInvalidVariant(string path, string field)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            AnimationVariantLoader.Load(path, ContentContext.Default, GodotAnimationVariantResources.Instance));
        Assert.Contains($"field '{field}'", error.Message, StringComparison.Ordinal);
        (AnimationVariantValidationReport? report, string? usage) = AnimationVariantValidationCommand.Execute([$"--variant={path}"]);
        Assert.Null(usage);
        Assert.NotNull(report);
        Assert.False(report.IsValid);
        Assert.Empty(report.Findings);
        Assert.Contains($"field '{field}'", Assert.Single(report.Errors), StringComparison.Ordinal);
    }

    /// <summary>Each independently corrupted declaration identifies the descriptor and affected field.</summary>
    [Fact]
    [Headless]
    public void InvalidDescriptors_ReportTheActualFailure()
    {
        (string File, string Field, string Message)[] cases =
        [
            ("malformed_json", "descriptor/payload", "field"),
            ("wrong_schema", "schema", "must be anim003_variant_v1"),
            ("bad_category", "category", "must be idle or locomotion"),
            ("bad_id", "id", "lowercase identifier"),
            ("bad_tag", "tags", "lowercase words"),
            ("empty_tags", "tags", "non-empty array"),
            ("unknown_field", "mood", "unknown"),
            ("wrong_category_field", "set", "wrong-category"),
            ("duplicate_field", "id", "duplicate field"),
            ("missing_description", "description", "must not be blank"),
            ("blank_description", "description", "must not be blank"),
            ("bad_description_type", "description", "must not be blank"),
            ("absolute_path", "clip", "owner-root-relative"),
            ("resource_url", "clip", "owner-root-relative"),
            ("escaping_path", "clip", "parent traversal"),
            ("missing_clip", "clip", "owner-root-relative"),
            ("missing_file", "clip", "does not exist"),
            ("not_adjacent", "clip", "must be adjacent"),
            ("wrong_owner", "clip", "outside the base owner"),
            ("clip_not_animation", "clip", "loadable native Animation resource, found Curve"),
            ("rejected-set/variant", "set", "ANIM-002 reviewed-set loader rejects the set")
        ];
        foreach ((string name, string field, string message) in cases)
        {
            string path = Invalid + name + ".json";
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                AnimationVariantLoader.Load(path, ContentContext.Default, GodotAnimationVariantResources.Instance));
            Assert.Contains(path, error.Message, StringComparison.Ordinal);
            Assert.Contains($"field '{field}'", error.Message, StringComparison.Ordinal);
            Assert.Contains(message, error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>An inactive content owner is ineligible even before its descriptor is read.</summary>
    [Fact]
    [Headless]
    public void InactiveOwner_IsNotBaseContent()
    {
        const string path = "res://content/fixture/variant.json";
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            AnimationVariantLoader.Load(path, ContentContext.Default, GodotAnimationVariantResources.Instance));
        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        Assert.Contains("not owned by active content 'default'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Validate composed templates and report metadata, invalid defaults and compatibility separately.</summary>
    [Fact]
    [Headless]
    public void Validator_ReportsCompositionAndCompatibility()
    {
        const string incompatible = "res://tests/animation/catalogue/fixture/invalid/incompatible_template.tscn";
        (AnimationVariantValidationReport? report, string? usage) = AnimationVariantValidationCommand.Execute(
            [$"--variant={AnimationFixtureVariants.Slump}", $"--template={Female}", $"--template={Male}", $"--template={incompatible}"]);
        Assert.Null(usage);
        Assert.NotNull(report);
        Assert.True(report.IsValid, string.Join(System.Environment.NewLine, report.Errors));
        Assert.Equal(2, report.IncompatibleEntries.Count);
        Assert.Contains("idle/sad", Assert.Single(report.InvalidDefaults), StringComparison.Ordinal);
        foreach (string template in new[] { Female, Male })
        {
            Assert.Contains(report.Findings, line => line.StartsWith($"Template '{template}': idle/sad (owner 'default') binds", StringComparison.Ordinal));
            Assert.Contains(report.Findings, line => line.StartsWith($"Template '{template}': locomotion/feminine (owner 'default') binds", StringComparison.Ordinal));
            Assert.DoesNotContain(report.Findings, line => line.StartsWith($"Template '{template}': idle/slump", StringComparison.Ordinal));
        }
    }
}
