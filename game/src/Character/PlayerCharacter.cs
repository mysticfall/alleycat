using AlleyCat.Core;
using AlleyCat.Rigging;
using AlleyCat.XR;
using Godot;

namespace AlleyCat.Character;

/// <summary>
/// Character root for the player role and its independently composed locomotion capability.
/// </summary>
[GlobalClass]
public partial class PlayerCharacter : Character, IPlayer
{
    /// <inheritdoc />
    protected override void ComposeComponents(List<IComponent> components)
    {
        components.Add(RequireComponentReference(Eyes, nameof(Eyes)));
        components.Add(RequireComponentReference(Locomotion, nameof(Locomotion)));
        if (Navigation is not null)
        {
            components.Add(Navigation);
        }

        components.Add(RequireComponentReference(Voice, nameof(Voice)));
        components.Add(RequireHandReference(LeftHand, LimbSide.Left, nameof(LeftHand)));
        components.Add(RequireHandReference(RightHand, LimbSide.Right, nameof(RightHand)));
        InstallVisionCameraResolver();
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        base._Ready();
        InstallVisionCameraResolver();
    }

    private void InstallVisionCameraResolver()
    {
        if (Eyes is { } eyes)
        {
            // Player-role/runtime composition boundary (VISION-001 TR 61): the player's vision
            // camera is exactly the active XR runtime camera, resolved lazily outside
            // AlleyCat.Vision so the vision component stays free of XR service lookups.
            eyes.SetRuntimeVisionCameraResolver(ResolveXRVisionCamera);
        }
    }

    private static Camera3D? ResolveXRVisionCamera()
    {
        try
        {
            return Game.Instance.GetService<XRManager>()?.Runtime?.Camera?.CameraNode;
        }
        catch (InvalidOperationException)
        {
            // No game singleton or registered XR manager yet; capture fails clearly until the
            // XR runtime is available.
            return null;
        }
    }
}
