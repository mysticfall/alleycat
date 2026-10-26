using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Characters;

/// <summary>
/// Integration coverage for the MakeHuman retarget metadata guard in the collider
/// post-import module. A collider sidecar without the retarget keys imports collider
/// bone attachments with MakeHuman bone names that the runtime rig skips wholesale,
/// so the guard must reject it before any wrapper or profile generation.
/// </summary>
public sealed class CharacterColliderRetargetGuardIntegrationTests
{
    private const string CharacterColliderImportScriptPath = "res://assets/characters/import/character_collider_profile_import.gd";
    private const string ReferenceFemaleColliderSidecarPath =
        "res://assets/characters/reference/female/reference_female.colliders.blend.import";
    private const string ReferenceMaleBlendPath = "res://assets/characters/reference/male/reference_male.blend";
    private const string ReferenceMaleWrapperPath =
        "res://assets/characters/reference/male/reference_male_colliders.tscn";
    private const string ReferenceMaleProfilePath =
        "res://assets/characters/reference/male/body_collider_profile.tres";
    private const string UserFixtureDirectory = "user://collider-retarget-guard-tests";

    private const string ShippedBugColliderSidecar = """
        [params]

        nodes/root_type=""
        _subresources={}
        blender/nodes/visible=0
        """;

    private const string PartialRetargetColliderSidecar = """
        [params]

        _subresources={
        "nodes": {
        "PATH:Ragdoll/Skeleton3D": {
        "retarget/bone_map": null
        }
        }
        }
        """;

    /// <summary>
    /// The shipped reference female collider sidecar is the healthy green model and must
    /// satisfy the guard's retarget metadata requirement.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleColliderSidecar_SatisfiesRetargetMetadataGuard()
        => Assert.True(CallRetargetMetadataGuard(ReferenceFemaleColliderSidecarPath));

    /// <summary>
    /// The shipped defect state — a collider sidecar with empty <c>_subresources</c> — must
    /// be rejected so no wrapper or profile is generated from un-retargeted imports.
    /// </summary>
    [Headless]
    [Fact]
    public void ColliderSidecarWithEmptySubresources_RejectedByRetargetMetadataGuard()
    {
        string sidecarPath = WriteUserSidecar("shipped-bug.colliders.blend.import", ShippedBugColliderSidecar);
        try
        {
            Assert.False(CallRetargetMetadataGuard(sidecarPath));
        }
        finally
        {
            RemoveUserFile(sidecarPath);
        }
    }

    /// <summary>
    /// A collider sidecar whose Ragdoll skeleton entry carries only part of the required
    /// retarget keys must be rejected.
    /// </summary>
    [Headless]
    [Fact]
    public void ColliderSidecarWithPartialRetargetKeys_RejectedByRetargetMetadataGuard()
    {
        string sidecarPath = WriteUserSidecar("partial-retarget.colliders.blend.import", PartialRetargetColliderSidecar);
        try
        {
            Assert.False(CallRetargetMetadataGuard(sidecarPath));
        }
        finally
        {
            RemoveUserFile(sidecarPath);
        }
    }

    /// <summary>
    /// With healthy retarget metadata the collider import stays idempotent: re-running the
    /// module against the shipped reference male leaves the generated wrapper scene and
    /// body collider profile byte-for-byte unchanged.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceMaleColliderImport_RegeneratesWrapperAndProfileByteForByte()
    {
        string wrapperBefore = Godot.FileAccess.GetFileAsString(ReferenceMaleWrapperPath);
        string profileBefore = Godot.FileAccess.GetFileAsString(ReferenceMaleProfilePath);
        Assert.False(string.IsNullOrEmpty(wrapperBefore));
        Assert.False(string.IsNullOrEmpty(profileBefore));

        GodotObject module = InstantiateColliderImportModule();
        _ = module.Call("apply", ReferenceMaleBlendPath);

        Assert.Equal(wrapperBefore, Godot.FileAccess.GetFileAsString(ReferenceMaleWrapperPath));
        Assert.Equal(profileBefore, Godot.FileAccess.GetFileAsString(ReferenceMaleProfilePath));
    }

    private static bool CallRetargetMetadataGuard(string sidecarPath)
        => InstantiateColliderImportModule().Call("_has_required_retarget_metadata", sidecarPath).AsBool();

    private static GodotObject InstantiateColliderImportModule()
    {
        GDScript script = ResourceLoader.Load<GDScript>(CharacterColliderImportScriptPath)
            ?? throw new InvalidOperationException(
                $"Failed to load collider import script at '{CharacterColliderImportScriptPath}'.");
        return (GodotObject)script.New();
    }

    private static string WriteUserSidecar(string fileName, string content)
    {
        _ = DirAccess.MakeDirRecursiveAbsolute(UserFixtureDirectory);
        string path = $"{UserFixtureDirectory}/{fileName}";
        Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write)
            ?? throw new InvalidOperationException($"Failed to open '{path}' for writing.");
        _ = file.StoreString(content);
        file.Dispose();
        return path;
    }

    private static void RemoveUserFile(string path)
        => _ = DirAccess.RemoveAbsolute(path);
}
