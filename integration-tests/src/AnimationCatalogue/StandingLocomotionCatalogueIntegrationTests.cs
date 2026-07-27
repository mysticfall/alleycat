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

    private static readonly IReadOnlyDictionary<string, string> _expectedRoleKeys =
        new Dictionary<string, string>
        {
            ["Idle"] = "mixamo_c9ccf750_b96c_11e4_a802_0aaa78deedf9",
            ["ForwardWalk"] = "mixamo_c9ccf814_b96c_11e4_a802_0aaa78deedf9",
            ["BackwardWalk"] = "mixamo_c9ccf998_b96c_11e4_a802_0aaa78deedf9",
            ["WalkArcLeft"] = "mixamo_c9ccf8d5_b96c_11e4_a802_0aaa78deedf9",
            ["WalkArcRight"] = "derived_mirror_c9ccf8d5_b96c_11e4_a802_0aaa78deedf9",
            ["SideStepLeft"] = "mixamo_c9c9d9d6_b96c_11e4_a802_0aaa78deedf9",
            ["SideStepRight"] = "mixamo_c9c9db9e_b96c_11e4_a802_0aaa78deedf9",
            ["TurnInPlaceLeft90"] = "mixamo_c9ceef5f_b96c_11e4_a802_0aaa78deedf9",
            ["TurnInPlaceRight90"] = "mixamo_c9cef01d_b96c_11e4_a802_0aaa78deedf9",
        };

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

        Assert.Equal(2, root.GetProperty("catalogue_schema_version").GetInt32());
        Assert.Equal(IndexPath, root.GetProperty("source_index").GetString());
        Assert.Equal(2, root.GetProperty("source_index_schema_version").GetInt32());
        Assert.Equal(2, root.GetProperty("metrics_schema_version").GetInt32());
        Assert.Equal(LibraryPath, root.GetProperty("animation_library").GetString());
        Assert.Equal(9, root.GetProperty("clip_count").GetInt32());

        JsonElement.ArrayEnumerator clips = root.GetProperty("clips").EnumerateArray();
        var clipsByKey = clips.ToDictionary(
            clip => Assert.IsType<string>(clip.GetProperty("key").GetString()),
            clip => clip.Clone()
        );
        Assert.Equal(motions.Keys, clipsByKey.Keys.Order());

        foreach ((string action, JsonElement clip) in clipsByKey)
        {
            Assert.Contains(clip.GetProperty("role").GetString(), _expectedRoleKeys.Keys);
            Assert.Equal(action, _expectedRoleKeys[clip.GetProperty("role").GetString()!]);
            Assert.Equal(action, clip.GetProperty("action").GetString());
            Assert.Equal($"{ClipDirectory}/{action}.res", clip.GetProperty("animation_resource").GetString());
            Assert.Equal("locomotion_standing", clip.GetProperty("group").GetString());
            Assert.Equal("locomotion", clip.GetProperty("category").GetString());
            Assert.Equal("reconstructed_root", clip.GetProperty("root_source").GetString());
            Assert.True(clip.GetProperty("root_created").GetBoolean());
            Assert.StartsWith("download/", clip.GetProperty("source_manifest").GetProperty("file").GetString());
            AssertDerivedMirrorSchema(action, clip);
        }

        Assert.Equal(_expectedRoleKeys.Values.Order(), clipsByKey.Keys.Order());

        JsonElement roleMaps = root.GetProperty("role_maps");
        Assert.Equal(["reference_female", "reference_male"], roleMaps.EnumerateObject().Select(property => property.Name).Order());
        foreach (JsonProperty map in roleMaps.EnumerateObject())
        {
            JsonElement.ArrayEnumerator entries = map.Value.EnumerateArray();
            var entriesByRole = entries.ToDictionary(
                entry => Assert.IsType<string>(entry.GetProperty("graph_role").GetString()),
                entry => entry.Clone());
            Assert.Equal(_expectedRoleKeys.Keys.Order(), entriesByRole.Keys.Order());
            foreach ((string role, string key) in _expectedRoleKeys)
            {
                JsonElement entry = entriesByRole[role];
                Assert.Equal(key, entry.GetProperty("library_key").GetString());
                Assert.Equal(role, entry.GetProperty("graph_role").GetString());
                Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("motion_family").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("clip_gender").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("replacement_note").GetString()));
                Assert.Equal(
                    ["clip_gender", "graph_role", "library_key", "motion_family", "replacement_note", "temporary"],
                    entry.EnumerateObject().Select(property => property.Name).Order());
            }
        }

        Assert.DoesNotContain("uid://", catalogueText, StringComparison.Ordinal);
        Assert.DoesNotContain("locomotion_" + "crouch", catalogueText, StringComparison.Ordinal);
        Assert.DoesNotContain("/" + "home/", catalogueText, StringComparison.Ordinal);
        Assert.DoesNotContain("/" + "tmp/", catalogueText, StringComparison.Ordinal);
        Assert.DoesNotContain("target_" + "scene", catalogueText, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public void RepresentativeClips_PreserveImportedDataExceptPrefixAndRootOwnedHipsHeadingNeutralisation()
    {
        string[] representativeActions =
        [
            _expectedRoleKeys["Idle"],
            _expectedRoleKeys["SideStepLeft"],
            _expectedRoleKeys["TurnInPlaceLeft90"],
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
                AssertAuthoredAnimationPreservation(imported, extracted);
                AssertRootOwnsAccumulatedHeading(extracted);
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

        Assert.Equal(9, motions.Count);
        return motions;
    }

    private static void AssertDerivedMirrorSchema(string action, JsonElement clip)
    {
        JsonElement provenance = clip.GetProperty("derived_provenance");
        if (action != _expectedRoleKeys["WalkArcRight"])
        {
            Assert.Equal(JsonValueKind.Object, provenance.ValueKind);
            Assert.Empty(provenance.EnumerateObject());
            return;
        }

        Assert.Equal(action, provenance.GetProperty("derived_identity").GetString());
        Assert.Equal("mixamo_c9ccf8d5_b96c_11e4_a802_0aaa78deedf9", provenance.GetProperty("source_action").GetString());
        Assert.Equal("c9ccf8d5-b96c-11e4-a802-0aaa78deedf9", provenance.GetProperty("source_motion_id").GetString());
        Assert.Equal("sagittal_world_matrix_reflection", provenance.GetProperty("derivation_type").GetString());
        Assert.Matches("^[0-9a-f]{64}$", provenance.GetProperty("source_artifact_sha256").GetString());
        Assert.Matches("^[0-9a-f]{64}$", provenance.GetProperty("recipe_sha256").GetString());
        Assert.Equal(
            "sagittal_world_matrix_reflection",
            provenance.GetProperty("canonical_reflection_recipe").GetProperty("type").GetString());
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

    private static void AssertAuthoredAnimationPreservation(GodotAnimation imported, GodotAnimation extracted)
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
                Variant importedValue = imported.TrackGetKeyValue(trackIndex, keyIndex);
                Variant extractedValue = extracted.TrackGetKeyValue(trackIndex, keyIndex);
                if (IsHipsRotationTrack(imported, trackIndex) && !importedValue.Equals(extractedValue))
                {
                    Quaternion importedRotation = importedValue.AsQuaternion().Normalized();
                    Quaternion extractedRotation = extractedValue.AsQuaternion().Normalized();
                    Quaternion difference = (extractedRotation * importedRotation.Inverse()).Normalized();
                    Assert.InRange(Mathf.Abs(difference.X), 0.0f, 0.0001f);
                    Assert.InRange(Mathf.Abs(difference.Z), 0.0f, 0.0001f);
                }
                else
                {
                    Assert.Equal(importedValue, extractedValue);
                }
            }
        }
    }

    private static void AssertRootOwnsAccumulatedHeading(GodotAnimation animation)
    {
        var rootPath = new NodePath("%GeneralSkeleton:Root");
        var hipsPath = new NodePath("%GeneralSkeleton:Hips");
        int rootTrack = animation.FindTrack(rootPath, GodotAnimation.TrackType.Rotation3D);
        int hipsTrack = animation.FindTrack(hipsPath, GodotAnimation.TrackType.Rotation3D);
        Assert.True(rootTrack >= 0);
        Assert.True(hipsTrack >= 0);

        Quaternion rootStart = animation.RotationTrackInterpolate(rootTrack, 0.0);
        Quaternion rootFinish = animation.RotationTrackInterpolate(rootTrack, animation.Length);
        float rootHeading = SignedHeading(rootFinish * rootStart.Inverse());
        if (Mathf.Abs(rootHeading) <= 0.001f)
        {
            return;
        }

        Quaternion hipsStart = animation.RotationTrackInterpolate(hipsTrack, 0.0);
        Quaternion hipsFinish = animation.RotationTrackInterpolate(hipsTrack, animation.Length);
        Assert.InRange(Mathf.Abs(SignedHeading(hipsFinish * hipsStart.Inverse())), 0.0f, 0.01f);
    }

    private static bool IsHipsRotationTrack(GodotAnimation animation, int trackIndex)
        => animation.TrackGetType(trackIndex) == GodotAnimation.TrackType.Rotation3D
            && NormaliseTrackPath(animation.TrackGetPath(trackIndex)).ToString() == "%GeneralSkeleton:Hips";

    private static float SignedHeading(Quaternion rotation)
    {
        Vector3 vector = new(rotation.X, rotation.Y, rotation.Z);
        Vector3 projected = Vector3.Up * vector.Dot(Vector3.Up);
        Quaternion twist = new(projected.X, projected.Y, projected.Z, rotation.W);
        float magnitude = Mathf.Sqrt(
            (twist.X * twist.X) + (twist.Y * twist.Y) + (twist.Z * twist.Z) + (twist.W * twist.W));
        if (magnitude <= 0.000001f)
        {
            return 0.0f;
        }

        twist = new Quaternion(twist.X / magnitude, twist.Y / magnitude, twist.Z / magnitude, twist.W / magnitude);
        return Mathf.Wrap(2.0f * Mathf.Atan2(new Vector3(twist.X, twist.Y, twist.Z).Dot(Vector3.Up), twist.W), -Mathf.Pi, Mathf.Pi);
    }

    private static NodePath NormaliseTrackPath(NodePath path)
    {
        string value = path.ToString();
        int separator = value.LastIndexOf(':');
        return separator <= 0 || !_acceptedSkeletonPrefixes.Contains(value[..separator])
            ? path
            : new NodePath("%GeneralSkeleton" + value[separator..]);
    }
}
