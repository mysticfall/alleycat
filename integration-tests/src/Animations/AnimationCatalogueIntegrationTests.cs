using AlleyCat.Animations;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.IntegrationTests.Animations;

/// <summary>
/// ANIM-003 TR3–TR11 and TR24 against real resources, the ANIM-002 set loader and the reference skeletons.
/// </summary>
public sealed class AnimationCatalogueIntegrationTests
{
    private const string FemaleBase = "res://assets/characters/templates/reference_female/reference_female_base.tscn";
    private const string MaleBase = "res://assets/characters/templates/reference_male/reference_male_base.tscn";

    /// <summary>Reference descriptors resolve exact base-owned payloads and fixture entries are never discovered.</summary>
    [Fact]
    [Headless]
    public void BaseCatalogue_SelectsOnlyTheDeclaredVariants()
    {
        using var rig = new FemaleRig();
        AnimationCatalogue catalogue = Catalogue(AnimationFixtureVariants.Sad, AnimationFixtureVariants.Feminine);
        try
        {
            catalogue.Build(rig.Skeleton, ContentContext.Default, rig.Logger);
            Assert.Collection(catalogue.Entries,
                sad => Assert.Equal((AnimationCategory.Idle, "sad", "default", "res://assets/animations/idle/sad/sad.tres"),
                    (sad.Category, sad.ID, sad.OwnerID, sad.ResourcePath)),
                gait => Assert.Equal((AnimationCategory.Locomotion, "feminine", "default", "res://assets/animations/locomotion/feminine/set.json"),
                    (gait.Category, gait.ID, gait.OwnerID, gait.ResourcePath)));
            Assert.Null(catalogue.Idle.Find("slump"));
        }
        finally
        {
            catalogue.Free();
        }
    }

    /// <summary>Remove preserves unaffected order; replacement appends; category/id, not id alone, is the key.</summary>
    [Fact]
    [Headless]
    public void Layers_ApplyOperationsInOrderAndKeepCategoryKeysDistinct()
    {
        using var rig = new FemaleRig();
        AnimationCatalogue catalogue = Catalogue(AnimationFixtureVariants.Sad, AnimationFixtureVariants.Feminine, AnimationFixtureVariants.Slump);
        try
        {
            catalogue.AddChild(AnimationFixtureVariants.Layer("Derived",
                AnimationFixtureVariants.Operation(AnimationVariantOperation.Action.Remove, "idle", "slump"),
                AnimationFixtureVariants.Add(AnimationFixtureVariants.GaitWithIdleID()),
                AnimationFixtureVariants.Operation(AnimationVariantOperation.Action.Replace, "idle", "sad",
                    "res://tests/animation/catalogue/fixture/idle/sad_replacement.json")));
            catalogue.Build(rig.Skeleton, ContentContext.Default, rig.Logger);
            Assert.Equal(["locomotion/feminine", "locomotion/sad", "idle/sad"],
                catalogue.Entries.Select(entry => $"{entry.Category.ToName()}/{entry.ID}"));
            Assert.Equal("res://tests/animation/catalogue/fixture/idle/slump.tres", catalogue.Idle.GetClip().ResourcePath);
            Assert.Equal("feminine", catalogue.Locomotion.Effective.ID);
            var calibration = new SomaMotionData(ProjectSettings.GlobalizePath("res://assets/animations/motionbricks/poses.json"));
            Assert.NotSame(catalogue.Locomotion.CreateSampler(calibration), catalogue.Locomotion.CreateSampler(calibration, "sad"));
        }
        finally
        {
            catalogue.Free();
        }
    }

    /// <summary>All conflicts leave the installed state unpublished, including when a valid earlier operation ran.</summary>
    [Fact]
    [Headless]
    public void Layers_RejectConflictsWithoutPublishingPartialCatalogue()
    {
        using var rig = new FemaleRig();
        (AnimationVariantOperation[] Operations, string Expected)[] cases =
        [
            ([AnimationFixtureVariants.Add(AnimationFixtureVariants.Sad)], "cannot add existing idle/sad"),
            ([AnimationFixtureVariants.Operation(AnimationVariantOperation.Action.Remove, "idle", "sad"),
                AnimationFixtureVariants.Add(AnimationFixtureVariants.Sad)], "multiple operations on idle/sad"),
            ([AnimationFixtureVariants.Operation(AnimationVariantOperation.Action.Remove, "idle", "missing")], "no inherited idle/missing"),
            ([AnimationFixtureVariants.Operation(AnimationVariantOperation.Action.Replace, "idle", "missing", AnimationFixtureVariants.Slump)], "does not match idle/missing"),
            ([AnimationFixtureVariants.Operation(AnimationVariantOperation.Action.Replace, "idle", "slump", AnimationFixtureVariants.Slump)], "no inherited idle/slump")
        ];
        foreach ((AnimationVariantOperation[] operations, string expected) in cases)
        {
            AnimationCatalogue catalogue = Catalogue(AnimationFixtureVariants.Sad, AnimationFixtureVariants.Feminine);
            try
            {
                catalogue.AddChild(AnimationFixtureVariants.Layer("Derived", operations));
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => catalogue.Build(rig.Skeleton, ContentContext.Default, rig.Logger));
                Assert.Contains(expected, error.Message, StringComparison.Ordinal);
                Assert.Contains("Derived[", error.Message, StringComparison.Ordinal);
                Assert.False(catalogue.IsBuilt);
            }
            finally
            {
                catalogue.Free();
            }
        }
    }

    /// <summary>Preference beats category default, then fallback; an unknown preference never changes selection.</summary>
    [Fact]
    [Headless]
    public void Views_ResolveDefaultsInOrder()
    {
        using var rig = new FemaleRig();
        AnimationCatalogue catalogue = Catalogue(AnimationFixtureVariants.Sad, AnimationFixtureVariants.Feminine, AnimationFixtureVariants.Stroll);
        catalogue.DefaultLocomotion = "stroll";
        try
        {
            catalogue.Build(rig.Skeleton, ContentContext.Default, rig.Logger);
            LocomotionAnimationView view = catalogue.Locomotion;
            Assert.Equal("stroll", view.Effective.ID);
            view.Preference = "feminine";
            Assert.Equal("feminine", view.Effective.ID);
            view.Preference = "sprint";
            Assert.Equal("stroll", view.Effective.ID);
            Assert.Contains(rig.Logger.Messages, message => message.Contains("agent preference 'sprint'", StringComparison.Ordinal));
            Assert.Equal(["stroll"], view.FindByTag("leisurely").Select(entry => entry.ID));
        }
        finally
        {
            catalogue.Free();
        }
    }

    /// <summary>
    /// The female sad clip is compatible on the male skeleton: SOMA-mapped tracks bind and the breast tracks, absent
    /// on the male skeleton, are optional.
    /// </summary>
    [Fact]
    [Headless]
    public void FemaleSad_IsCompatibleOnMaleSkeleton()
    {
        Node male = TestUtils.LoadPackedScene(MaleBase).Instantiate();
        try
        {
            Skeleton3D skeleton = male.FindChild("GeneralSkeleton", true, false) as Skeleton3D
                ?? throw new InvalidOperationException("Male base has no GeneralSkeleton.");
            Animation clip = GD.Load<Animation>("res://assets/animations/idle/sad/sad.tres");
            Assert.True(skeleton.FindBone("breast_l") < 0);
            Assert.Contains(Enumerable.Range(0, clip.GetTrackCount()), i => clip.TrackGetPath(i).GetSubName(0) == "breast_l");

            var logger = new CapturingLogger();
            AnimationCatalogue catalogue = Catalogue(AnimationFixtureVariants.Sad);
            catalogue.Build(skeleton, ContentContext.Default, logger);

            Assert.Equal("sad", Assert.Single(catalogue.Idle.Entries).ID);
            Assert.DoesNotContain(logger.Messages, m => m.Contains("excluding idle/", StringComparison.Ordinal));
            catalogue.Free();
        }
        finally
        {
            male.Free();
        }
    }

    /// <summary>A clip with an unresolved SOMA-mapped track is excluded with one log line, and the view then fails clearly.</summary>
    [Fact]
    [Headless]
    public void IdleWithUnresolvedMappedTrack_IsExcluded()
    {
        using var skeleton = new Skeleton3D { Name = "GeneralSkeleton" };
        foreach (string bone in new[] { "Root", "Hips", "Spine", "Chest", "UpperChest" })
        {
            _ = skeleton.AddBone(bone);
        }

        var logger = new CapturingLogger();
        AnimationCatalogue catalogue = Catalogue(AnimationFixtureVariants.Sad);
        catalogue.Build(skeleton, ContentContext.Default, logger);

        Assert.Empty(catalogue.Idle.Entries);
        string exclusion = Assert.Single(logger.Messages, m => m.Contains("excluding idle/sad", StringComparison.Ordinal));
        Assert.Contains("%GeneralSkeleton:LeftFoot", exclusion, StringComparison.Ordinal);
        Assert.DoesNotContain("breast", exclusion, StringComparison.Ordinal);
        _ = Assert.Throws<InvalidOperationException>(() => catalogue.Idle.Effective);
        catalogue.Free();
    }

    /// <summary>The hardcoded SOMA body map equals the humanoid-profile bones of the canonical SOMA source rig.</summary>
    [Fact]
    [Headless]
    public void SomaBodyMap_MatchesSourceRig()
    {
        var humanoid = new SkeletonProfileHumanoid();
        HashSet<string> profile = [.. Enumerable.Range(0, humanoid.BoneSize).Select(i => humanoid.GetBoneName(i).ToString())];
        Skeleton3D source = TestUtils.LoadPackedScene("res://assets/animations/motionbricks/source-rest/female.tscn").Instantiate<Skeleton3D>();
        try
        {
            HashSet<string> mapped = [.. Enumerable.Range(0, source.GetBoneCount()).Select(source.GetBoneName).Where(profile.Contains)];
            Assert.Equal(mapped.Order(StringComparer.Ordinal), IdleClipCompatibility.SomaMappedBones.Order(StringComparer.Ordinal));
        }
        finally
        {
            source.Free();
        }
    }

    /// <summary>Templates declare variants and defaults: base locomotion default, NPC idle default, no player idle default.</summary>
    [Fact]
    [Headless]
    public void Templates_DeclareCatalogueDefaults()
    {
        (string Scene, string Idle)[] cases =
        [
            ("res://assets/characters/templates/reference_female/reference_female_npc.tscn", "sad"),
            ("res://assets/characters/templates/reference_male/reference_male_npc.tscn", "sad"),
            ("res://assets/characters/templates/reference_female/reference_female_player.tscn", "")
        ];
        foreach ((string scene, string idle) in cases)
        {
            Node root = TestUtils.LoadPackedScene(scene).Instantiate();
            try
            {
                var catalogue = AnimationCatalogue.RequireFor(root);
                Assert.Equal(idle, catalogue.DefaultIdle);
                Assert.Equal("feminine", catalogue.DefaultLocomotion);
                AnimationVariantLayer layer = Assert.Single(catalogue.GetChildren().OfType<AnimationVariantLayer>());
                Assert.Equal([AnimationFixtureVariants.Sad, AnimationFixtureVariants.Feminine], layer.Operations.Select(operation => operation.Descriptor));
            }
            finally
            {
                root.Free();
            }
        }
    }

    private static AnimationCatalogue Catalogue(params string[] descriptors)
    {
        var catalogue = new AnimationCatalogue();
        catalogue.AddChild(AnimationFixtureVariants.Layer("Base", [.. descriptors.Select(AnimationFixtureVariants.Add)]));
        return catalogue;
    }

    /// <summary>A female native skeleton mounted under a retarget modifier, as ANIM TR4 presentation requires.</summary>
    private sealed class FemaleRig : IDisposable
    {
        private readonly Skeleton3D _source = new();

        public FemaleRig()
        {
            Node female = TestUtils.LoadPackedScene(FemaleBase).Instantiate();
            try
            {
                var skeleton = (Skeleton3D)((Skeleton3D)female.FindChild("GeneralSkeleton", true, false)).Duplicate(0);
                var modifier = new RetargetModifier3D();
                _source.AddChild(modifier);
                modifier.AddChild(skeleton);
                Skeleton = skeleton;
            }
            finally
            {
                female.Free();
            }
        }

        public Skeleton3D Skeleton
        {
            get;
        }

        public CapturingLogger Logger { get; } = new();

        public void Dispose() => _source.Free();
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _messages.Add(formatter(state, exception));
    }
}
