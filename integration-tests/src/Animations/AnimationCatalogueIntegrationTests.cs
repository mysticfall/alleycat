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
    private const string DefaultPack = "res://data/animations/default/pack.json";
    private const string ExtensionPack = "res://tests/animation/catalogue/extension/pack.json";
    private const string FixtureRoot = "res://tests/animation/catalogue/fixture/";
    private const string FemaleBase = "res://assets/characters/templates/reference_female/reference_female_base.tscn";
    private const string MaleBase = "res://assets/characters/templates/reference_male/reference_male_base.tscn";

    /// <summary>The default pack validates under the production-derived default context and lists sad and feminine.</summary>
    [Fact]
    [Headless]
    public void DefaultPack_ValidatesUnderDerivedDefaultContext()
    {
        ContentContext context = AnimationPackLoader.DeriveContext(DefaultPack);
        AnimationPackManifest manifest = AnimationPackLoader.Load(DefaultPack, context, GodotAnimationPackResources.Instance);

        Assert.Same(ContentContext.Default, context);
        Assert.Equal("default", manifest.ID);
        Assert.Collection(manifest.Entries,
            sad => Assert.Equal((AnimationCategory.Idle, "sad", "res://data/animations/default/idle/sad.tres"), (sad.Category, sad.ID, sad.ResourcePath)),
            feminine => Assert.Equal((AnimationCategory.Locomotion, "feminine", "res://data/motionbricks/reviewed-feminine/set.json"),
                (feminine.Category, feminine.ID, feminine.ResourcePath)));
    }

    /// <summary>The idle-only fixture resolves and loads its clip through an injected content-pack context.</summary>
    [Fact]
    [Headless]
    public void FixturePack_LoadsThroughInjectedContentPackContext()
    {
        using var rig = new FemaleRig();
        AnimationCatalogue catalogue = Catalogue(rig.Logger, (FixtureRoot + "pack.json", 0, new ContentContext("fixture", FixtureRoot)));

        catalogue.Build(rig.Skeleton);

        AnimationEntry slump = Assert.Single(catalogue.Idle.Entries);
        Assert.Equal(("slump", "fixture", FixtureRoot + "idle/slump.tres"), (slump.ID, slump.PackID, slump.ResourcePath));
        Assert.Equal(FixtureRoot + "idle/slump.tres", catalogue.Idle.GetClip().ResourcePath);
        Assert.Empty(catalogue.Locomotion.Entries);
        catalogue.Free();
    }

    /// <summary>
    /// The extension pack, given the default context by location, overrides a default entry with a log line and adds a
    /// locomotion entry whose set the ANIM-002 loader accepts.
    /// </summary>
    [Fact]
    [Headless]
    public void ExtensionPack_OverridesDefaultAndAddsLocomotion()
    {
        using var rig = new FemaleRig();
        AnimationCatalogue catalogue = Catalogue(rig.Logger, (ExtensionPack, 10, null), (DefaultPack, 0, null));
        catalogue.DefaultLocomotion = "feminine";

        catalogue.Build(rig.Skeleton);

        Assert.Equal("extension", Assert.Single(catalogue.Idle.Entries).PackID);
        Assert.Contains(rig.Logger.Messages, m => m == "Animation catalogue: idle entry 'sad' from pack 'extension' overrides pack 'default'");
        Assert.Equal(["feminine", "stroll"], catalogue.Locomotion.Entries.Select(e => e.ID));
        Assert.Equal("feminine", catalogue.Locomotion.Effective.ID);
        var calibration = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        Assert.NotSame(catalogue.Locomotion.CreateSampler(calibration), catalogue.Locomotion.CreateSampler(calibration, "stroll"));
        catalogue.Free();
    }

    /// <summary>A disabled pack node contributes nothing; a duplicate enabled pack id fails construction.</summary>
    [Fact]
    [Headless]
    public void PackNodes_EnabledOverrideAndDuplicateIDs()
    {
        using var rig = new FemaleRig();
        AnimationCatalogue disabled = Catalogue(rig.Logger, (DefaultPack, 0, null), (ExtensionPack, 10, null));
        disabled.GetChild<AnimationPack>(1).Enabled = false;
        disabled.Build(rig.Skeleton);
        Assert.Equal("default", Assert.Single(disabled.Idle.Entries).PackID);
        disabled.Free();

        AnimationCatalogue duplicate = Catalogue(rig.Logger, (DefaultPack, 0, null), (DefaultPack, 5, null));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => duplicate.Build(rig.Skeleton));
        Assert.Contains("'default'", error.Message, StringComparison.Ordinal);
        Assert.False(duplicate.IsBuilt);
        duplicate.Free();
    }

    /// <summary>Preference beats the template default, which beats the fallback; unknown ids are ignored.</summary>
    [Fact]
    [Headless]
    public void Views_ResolveDefaultsInOrder()
    {
        using var rig = new FemaleRig();
        AnimationCatalogue catalogue = Catalogue(rig.Logger, (DefaultPack, 0, null), (ExtensionPack, 10, null));
        catalogue.DefaultLocomotion = "stroll";
        catalogue.Build(rig.Skeleton);
        LocomotionAnimationView view = catalogue.Locomotion;

        Assert.Equal("stroll", view.Effective.ID);
        view.Preference = "feminine";
        Assert.Equal("feminine", view.Effective.ID);
        view.Preference = "sprint";
        Assert.Equal("stroll", view.Effective.ID);
        Assert.Contains(rig.Logger.Messages, m => m.Contains("agent preference 'sprint'", StringComparison.Ordinal));
        Assert.Equal(["stroll"], view.FindByTag("leisurely").Select(e => e.ID));
        catalogue.Free();
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
            Animation clip = GD.Load<Animation>("res://data/animations/default/idle/sad.tres");
            Assert.True(skeleton.FindBone("breast_l") < 0);
            Assert.Contains(Enumerable.Range(0, clip.GetTrackCount()), i => clip.TrackGetPath(i).GetSubName(0) == "breast_l");

            var logger = new CapturingLogger();
            AnimationCatalogue catalogue = Catalogue(logger, (DefaultPack, 0, null));
            catalogue.Build(skeleton);

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
        AnimationCatalogue catalogue = Catalogue(logger, (DefaultPack, 0, null));
        catalogue.Build(skeleton);

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
        Skeleton3D source = TestUtils.LoadPackedScene("res://data/motionbricks/source-rest/female.tscn").Instantiate<Skeleton3D>();
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

    /// <summary>Templates declare packs and defaults: base locomotion default, NPC idle default, no player idle default.</summary>
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
                AnimationPack pack = Assert.Single(catalogue.GetChildren().OfType<AnimationPack>());
                Assert.Equal((DefaultPack, true, 0), (pack.Manifest, pack.Enabled, pack.Priority));
            }
            finally
            {
                root.Free();
            }
        }
    }

    private static AnimationCatalogue Catalogue(ILogger logger, params (string Manifest, int Priority, ContentContext? Context)[] packs)
    {
        var catalogue = new AnimationCatalogue { LoggerOverride = logger };
        foreach ((string manifest, int priority, ContentContext? context) in packs)
        {
            catalogue.AddChild(new AnimationPack { Manifest = manifest, Priority = priority, ContextOverride = context });
        }

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
