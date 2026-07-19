using System.Text.Json;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

using GodotAnimation = Godot.Animation;

namespace AlleyCat.IntegrationTests.AnimationCatalogue;

/// <summary>
/// Focused integration coverage for the imported and packaged standing-locomotion catalogue.
/// </summary>
public sealed class StandingLocomotionCatalogueIntegrationTests
{
    private const string IndexPath =
        "res://assets/characters/reference/female/animations/processed/mixamo/index.json";
    private const string ImportedScenePath =
        "res://assets/characters/reference/female/animations/processed/mixamo/locomotion_standing.blend";
    private const string ClipDirectory =
        "res://assets/characters/reference/female/animations/locomotion/clips";
    private const string LibraryPath =
        "res://assets/characters/reference/female/animations/locomotion/standing_locomotion_library.tres";
    private const string CataloguePath =
        "res://assets/characters/reference/female/animations/locomotion/standing_locomotion_catalogue.json";
    private const double ExpectedDuration = 106.66666668653485;
    private const double DurationTolerance = 0.00001;

    private static readonly string[] _requiredBones =
    [
        "Root",
        "Hips",
        "LeftFoot",
        "RightFoot",
        "LeftToes",
        "RightToes",
    ];

    private static readonly HashSet<string> _acceptedSkeletonPrefixes =
    [
        "%GeneralSkeleton",
        "GeneralSkeleton",
        "Female/GeneralSkeleton",
    ];

    private static readonly HashSet<string> _requiredLateralMotionIDs =
    [
        "c9c9d9d6-b96c-11e4-a802-0aaa78deedf9",
        "c9c9db9e-b96c-11e4-a802-0aaa78deedf9",
        "c9c9829c-b96c-11e4-a802-0aaa78deedf9",
        "c9c985b7-b96c-11e4-a802-0aaa78deedf9",
        "c9c7ff20-b96c-11e4-a802-0aaa78deedf9",
    ];

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public void ImportedStandingScene_ExposesExactCatalogueShapeAndActionSet()
    {
        SortedDictionary<string, JsonElement> motions = LoadStandingMotions();
        PackedScene packedScene = Assert.IsType<PackedScene>(
            ResourceLoader.Load(ImportedScenePath),
            exactMatch: false
        );
        Node sceneRoot = packedScene.Instantiate();

        try
        {
            Godot.Collections.Array<Node> players = sceneRoot.FindChildren(
                "*",
                nameof(AnimationPlayer),
                recursive: true,
                owned: false
            );
            Godot.Collections.Array<Node> skeletons = sceneRoot.FindChildren(
                "*",
                nameof(Skeleton3D),
                recursive: true,
                owned: false
            );

            AnimationPlayer player = Assert.IsType<AnimationPlayer>(Assert.Single(players), exactMatch: false);
            _ = Assert.Single(skeletons);
            StringName libraryName = Assert.Single(player.GetAnimationLibraryList());
            AnimationLibrary library = player.GetAnimationLibrary(libraryName);
            Assert.NotNull(library);

            Assert.Equal(motions.Keys, library.GetAnimationList().Select(name => name.ToString()).Order());
        }
        finally
        {
            sceneRoot.Free();
        }
    }

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public void ExtractedClipsAndLibrary_AreLoadableAndCorrespondOneToOne()
    {
        SortedDictionary<string, JsonElement> motions = LoadStandingMotions();
        AnimationLibrary library = Assert.IsType<AnimationLibrary>(
            ResourceLoader.Load(LibraryPath),
            exactMatch: false
        );

        Assert.Equal("standing_locomotion_library", library.ResourceName);
        Assert.Equal(motions.Keys, library.GetAnimationList().Select(name => name.ToString()).Order());

        foreach (string action in motions.Keys)
        {
            string clipPath = $"{ClipDirectory}/{action}.res";
            GodotAnimation clip = Assert.IsType<GodotAnimation>(
                ResourceLoader.Load(clipPath),
                exactMatch: false
            );

            Assert.Equal(action, clip.ResourceName);
            Assert.Equal(clipPath, clip.ResourcePath);
            AssertRequiredSkeletonTracks(clip);

            GodotAnimation libraryClip = library.GetAnimation(action);
            Assert.NotNull(libraryClip);
            Assert.Equal(clipPath, libraryClip.ResourcePath);
            Assert.Same(clip, libraryClip);
        }
    }

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public void CatalogueJson_RecordsCompletePortableStandingCollection()
    {
        SortedDictionary<string, JsonElement> motions = LoadStandingMotions();
        string catalogueText = ReadProjectFile(CataloguePath);
        using var document = JsonDocument.Parse(catalogueText);
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("catalogue_schema_version").GetInt32());
        Assert.Equal(IndexPath, root.GetProperty("source_index").GetString());
        Assert.Equal(2, root.GetProperty("source_index_schema_version").GetInt32());
        Assert.Equal(2, root.GetProperty("metrics_schema_version").GetInt32());
        Assert.Equal(LibraryPath, root.GetProperty("animation_library").GetString());
        Assert.Equal(46, root.GetProperty("clip_count").GetInt32());

        JsonElement.ArrayEnumerator clips = root.GetProperty("clips").EnumerateArray();
        var clipsByKey = clips.ToDictionary(
            clip => Assert.IsType<string>(clip.GetProperty("key").GetString()),
            clip => clip.Clone()
        );
        Assert.Equal(motions.Keys, clipsByKey.Keys.Order());

        int sampleCount = 0;
        double duration = 0.0;
        HashSet<string> lateralMotionIDs = [];
        Dictionary<string, int> classCounts = [];
        foreach ((string action, JsonElement clip) in clipsByKey)
        {
            Assert.Equal(action, clip.GetProperty("action").GetString());
            Assert.Equal($"{ClipDirectory}/{action}.res", clip.GetProperty("animation_resource").GetString());
            Assert.Equal("locomotion_standing", clip.GetProperty("group").GetString());
            Assert.Equal("locomotion", clip.GetProperty("category").GetString());
            Assert.Equal("reconstructed_root", clip.GetProperty("root_source").GetString());
            Assert.True(clip.GetProperty("root_created").GetBoolean());
            Assert.StartsWith("download/", clip.GetProperty("source_manifest").GetProperty("file").GetString());

            sampleCount += clip.GetProperty("sample_count").GetInt32();
            duration += clip.GetProperty("length").GetDouble();
            string motionID = Assert.IsType<string>(clip.GetProperty("motion_id").GetString());
            string motionClass = Assert.IsType<string>(clip.GetProperty("motion_class").GetString());
            classCounts[motionClass] = classCounts.GetValueOrDefault(motionClass) + 1;
            if (_requiredLateralMotionIDs.Contains(motionID))
            {
                _ = lateralMotionIDs.Add(motionID);
            }
        }

        Assert.Equal(2612, sampleCount);
        Assert.InRange(duration, ExpectedDuration - DurationTolerance, ExpectedDuration + DurationTolerance);
        Assert.Equal(_requiredLateralMotionIDs, lateralMotionIDs);
        Assert.Equal(4, classCounts["StandingIdle"]);
        Assert.Equal(36, classCounts["StandingLocomotion"]);
        Assert.Equal(6, classCounts["TurnInPlace"]);

        Assert.DoesNotContain("uid://", catalogueText, StringComparison.Ordinal);
        Assert.DoesNotContain("locomotion_" + "crouch", catalogueText, StringComparison.Ordinal);
        Assert.DoesNotContain("/" + "home/", catalogueText, StringComparison.Ordinal);
        Assert.DoesNotContain("/" + "tmp/", catalogueText, StringComparison.Ordinal);
        Assert.DoesNotContain("target_" + "scene", catalogueText, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public void RepresentativeClips_PassThroughImportedAnimationDataExceptAcceptedPrefixNormalisation()
    {
        SortedDictionary<string, JsonElement> motions = LoadStandingMotions();
        string[] representativeActions =
        [
            motions.First(pair => pair.Value.GetProperty("motion_class").GetString() == "StandingIdle").Key,
            motions.Single(pair => pair.Value.GetProperty("motion_id").GetString() == "c9c7ff20-b96c-11e4-a802-0aaa78deedf9").Key,
            motions.First(pair => pair.Value.GetProperty("motion_class").GetString() == "TurnInPlace").Key,
        ];

        PackedScene packedScene = Assert.IsType<PackedScene>(
            ResourceLoader.Load(ImportedScenePath),
            exactMatch: false
        );
        Node sceneRoot = packedScene.Instantiate();
        try
        {
            AnimationPlayer player = Assert.IsType<AnimationPlayer>(
                Assert.Single(sceneRoot.FindChildren("*", nameof(AnimationPlayer), recursive: true, owned: false)),
                exactMatch: false
            );
            AnimationLibrary importedLibrary = player.GetAnimationLibrary(
                Assert.Single(player.GetAnimationLibraryList())
            );
            Assert.NotNull(importedLibrary);

            foreach (string action in representativeActions)
            {
                GodotAnimation imported = importedLibrary.GetAnimation(action);
                Assert.NotNull(imported);
                GodotAnimation extracted = Assert.IsType<GodotAnimation>(
                    ResourceLoader.Load($"{ClipDirectory}/{action}.res"),
                    exactMatch: false
                );
                AssertPassThroughAnimation(imported, extracted);
            }
        }
        finally
        {
            sceneRoot.Free();
        }
    }

    private static SortedDictionary<string, JsonElement> LoadStandingMotions()
    {
        using var document = JsonDocument.Parse(ReadProjectFile(IndexPath));
        SortedDictionary<string, JsonElement> motions = [];
        foreach (JsonProperty property in document.RootElement.GetProperty("motions").EnumerateObject())
        {
            JsonElement motion = property.Value;
            if (
                motion.GetProperty("status").GetString() != "success"
                || motion.GetProperty("group").GetString() != "locomotion_standing"
            )
            {
                continue;
            }

            string action = Assert.IsType<string>(motion.GetProperty("action").GetString());
            Assert.True(motions.TryAdd(action, motion.Clone()), $"Duplicate action: {action}");
        }

        Assert.Equal(46, motions.Count);
        return motions;
    }

    private static string ReadProjectFile(string resourcePath)
        => File.ReadAllText(ProjectSettings.GlobalizePath(resourcePath));

    private static void AssertRequiredSkeletonTracks(GodotAnimation animation)
    {
        HashSet<string> bones = [];
        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
        {
            string path = animation.TrackGetPath(trackIndex).ToString();
            int separator = path.LastIndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            Assert.Contains(path[..separator], _acceptedSkeletonPrefixes);
            _ = bones.Add(path[(separator + 1)..]);
        }

        foreach (string bone in _requiredBones)
        {
            Assert.Contains(bone, bones);
        }
    }

    private static void AssertPassThroughAnimation(GodotAnimation imported, GodotAnimation extracted)
    {
        Assert.Equal(imported.Length, extracted.Length);
        Assert.Equal(imported.LoopMode, extracted.LoopMode);
        Assert.Equal(imported.Step, extracted.Step);
        Assert.Equal(imported.GetTrackCount(), extracted.GetTrackCount());

        for (int trackIndex = 0; trackIndex < imported.GetTrackCount(); trackIndex++)
        {
            Assert.Equal(NormaliseTrackPath(imported.TrackGetPath(trackIndex)), extracted.TrackGetPath(trackIndex));
            Assert.Equal(imported.TrackGetType(trackIndex), extracted.TrackGetType(trackIndex));
            Assert.Equal(imported.TrackIsEnabled(trackIndex), extracted.TrackIsEnabled(trackIndex));
            Assert.Equal(imported.TrackGetInterpolationType(trackIndex), extracted.TrackGetInterpolationType(trackIndex));
            Assert.Equal(
                imported.TrackGetInterpolationLoopWrap(trackIndex),
                extracted.TrackGetInterpolationLoopWrap(trackIndex)
            );

            int keyCount = imported.TrackGetKeyCount(trackIndex);
            Assert.Equal(keyCount, extracted.TrackGetKeyCount(trackIndex));
            for (int keyIndex = 0; keyIndex < keyCount; keyIndex++)
            {
                Assert.Equal(imported.TrackGetKeyTime(trackIndex, keyIndex), extracted.TrackGetKeyTime(trackIndex, keyIndex));
                Assert.Equal(
                    imported.TrackGetKeyTransition(trackIndex, keyIndex),
                    extracted.TrackGetKeyTransition(trackIndex, keyIndex)
                );
                Assert.Equal(
                    imported.TrackGetKeyValue(trackIndex, keyIndex),
                    extracted.TrackGetKeyValue(trackIndex, keyIndex)
                );
            }
        }
    }

    private static NodePath NormaliseTrackPath(NodePath path)
    {
        string value = path.ToString();
        int separator = value.LastIndexOf(':');
        return separator <= 0 || !_acceptedSkeletonPrefixes.Contains(value[..separator])
            ? path
            : new NodePath("GeneralSkeleton" + value[separator..]);
    }
}
