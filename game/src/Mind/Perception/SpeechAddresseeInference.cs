using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Vision;
using AlleyCat.XR;
using Godot;

namespace AlleyCat.Mind.Perception;

/// <summary>One candidate addressee resolved from current scene membership with its world-space origin.</summary>
/// <param name="FullId">Canonical character FullId of the candidate.</param>
/// <param name="Origin">Candidate's world-space origin.</param>
internal readonly record struct SpeechAddresseeCandidate(string FullId, Vector3 Origin);

/// <summary>
/// Facing-cone addressee inference for observed speech (AI-006 TR-11/12): every other current-scene character
/// within the speaker's facing cone — bounded by a tunable angle threshold — is an addressee, and an empty cone
/// result is undecided.
/// </summary>
/// <remarks>
/// <para>
/// The orientation source order is contractual while remaining tunable through
/// <see cref="SpeechPerception.FacingConeAngleDegrees"/>: the player's XR head/camera for player speakers first,
/// then the speaker's gaze direction toward its assigned vision look target (the AI-007 attention-gaze machinery's
/// published output; AI-009's solved head pose exposes no public read API), then the speaker's body forward. When
/// no source resolves a usable ground-plane direction, the cone result is empty and the addressee stays undecided.
/// </para>
/// <para>
/// Inference reads orientation only: it never assigns look targets, alters eye presentation, matches vocatives, or
/// makes model requests (AI-006 TR-10/11). Cone membership is evaluated on the world ground plane (XZ) so vertical
/// separation alone never turns an addressed character into a bystander.
/// </para>
/// </remarks>
internal static class SpeechAddresseeInference
{
    /// <summary>Default tunable facing-cone half-angle in degrees, measured on the ground plane.</summary>
    public const float DefaultFacingConeAngleDegrees = 30f;

    /// <summary>
    /// Numerical-noise guard for ground-plane separation: squared lengths below this value are treated as zero
    /// separation, matching the relative-position bearing precedent (AI-006 TR-7).
    /// </summary>
    private const float GroundSeparationSquared = 1e-6f;

    /// <summary>
    /// Infers the addressee set for one recognised speaker from current scene membership: resolves the speaker's
    /// facing (player camera, then gaze look target, then body forward), then collects every other scene character
    /// inside the facing cone. An unresolvable speaker transform or an empty cone yields an empty — undecided — set.
    /// </summary>
    /// <param name="speaker">Recognised speaking character; never the observer itself.</param>
    /// <param name="sceneCharacters">Current scene membership snapshot.</param>
    /// <param name="facingConeAngleDegrees">Facing-cone half-angle threshold in degrees.</param>
    /// <param name="playerCamera">
    /// Live XR camera for player speakers only, or <see langword="null"/> for non-player speakers or when the
    /// camera is unavailable.
    /// </param>
    internal static IReadOnlyList<string> InferAddressees(
        ICharacter speaker,
        IReadOnlyCollection<ICharacter> sceneCharacters,
        float facingConeAngleDegrees,
        IXRCamera? playerCamera)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        ArgumentNullException.ThrowIfNull(sceneCharacters);

        if (!TryGetLiveTransform(speaker, out Transform3D speakerTransform))
        {
            return [];
        }

        Vector3 speakerOrigin = speakerTransform.Origin;
        Vector3? facing = ResolveCameraFacing(playerCamera)
            ?? ResolveGazeFacing(speaker, speakerOrigin)
            ?? ProjectToGroundPlane(-speakerTransform.Basis.Z);
        if (facing is not { } usableFacing)
        {
            return [];
        }

        List<SpeechAddresseeCandidate> candidates = [];
        foreach (ICharacter character in sceneCharacters)
        {
            if (IsSpeaker(character, speaker) || !TryGetLiveTransform(character, out Transform3D transform))
            {
                continue;
            }

            candidates.Add(new SpeechAddresseeCandidate(character.FullId, transform.Origin));
        }

        return ResolveFacingConeAddressees(speakerOrigin, usableFacing, candidates, facingConeAngleDegrees);
    }

    /// <summary>
    /// Resolves the facing cone's addressee membership from pure geometry: a candidate is an addressee when the
    /// ground-plane angle between the speaker's facing and the bearing to the candidate is at or below the
    /// threshold. A candidate with zero ground separation cannot be outside the cone — the same deterministic
    /// zero-separation-as-front rule the relative-position bearing uses (AI-006 TR-7) — and a facing with no
    /// ground-plane component (a vertical gaze) resolves no addressee at all, leaving the speech undecided.
    /// </summary>
    internal static IReadOnlyList<string> ResolveFacingConeAddressees(
        Vector3 speakerOrigin,
        Vector3 speakerFacing,
        IReadOnlyList<SpeechAddresseeCandidate> candidates,
        float facingConeAngleDegrees)
    {
        ValidateConeAngle(facingConeAngleDegrees);

        Vector3? facing = ProjectToGroundPlane(speakerFacing);
        if (facing is not { } usableFacing)
        {
            return [];
        }

        float thresholdRadians = Mathf.DegToRad(facingConeAngleDegrees);
        List<string>? addressees = null;
        foreach (SpeechAddresseeCandidate candidate in candidates)
        {
            Vector3 bearing = candidate.Origin - speakerOrigin;
            bool withinCone = ProjectToGroundPlane(bearing) is not { } groundBearing
                || usableFacing.AngleTo(groundBearing.Normalized()) <= thresholdRadians;
            if (withinCone)
            {
                (addressees ??= []).Add(candidate.FullId);
            }
        }

        return addressees ?? [];
    }

    /// <summary>Validates the tunable cone half-angle as finite within (0, 180] degrees.</summary>
    internal static void ValidateConeAngle(float facingConeAngleDegrees)
    {
        if (!float.IsFinite(facingConeAngleDegrees)
            || facingConeAngleDegrees <= 0f
            || facingConeAngleDegrees > 180f)
        {
            throw new ArgumentException(
                $"The facing-cone angle must be a finite value within (0, 180] degrees; received "
                + $"'{facingConeAngleDegrees}'.",
                nameof(facingConeAngleDegrees));
        }
    }

    /// <summary>Gets the player camera's ground-plane facing, or <see langword="null"/> when it is unusable.</summary>
    private static Vector3? ResolveCameraFacing(IXRCamera? playerCamera)
        => playerCamera?.CameraNode is Node3D cameraNode && IsLiveNode(cameraNode)
            ? ProjectToGroundPlane(-cameraNode.GlobalTransform.Basis.Z)
            : null;

    /// <summary>
    /// Gets the speaker's gaze facing — the ground-plane direction from the speaker toward its assigned vision
    /// look target (AI-007's published gaze output) — or <see langword="null"/> when no live target exists.
    /// </summary>
    private static Vector3? ResolveGazeFacing(ICharacter speaker, Vector3 speakerOrigin)
        => speaker.TryGetVision(out IVision? vision)
            && vision?.LookTarget is VisualCue cue
            && IsLiveNode(cue)
            ? ProjectToGroundPlane(cue.SampleGlobalPosition() - speakerOrigin)
            : null;

    /// <summary>Determines whether a scene character is the inferred speaker itself, excluding it from candidacy.</summary>
    private static bool IsSpeaker(ICharacter character, ICharacter speaker)
        => ReferenceEquals(character, speaker)
            || string.Equals(character.FullId, speaker.FullId, StringComparison.Ordinal);

    /// <summary>
    /// Projects a direction onto the world ground plane (XZ), returning a normalised direction or
    /// <see langword="null"/> when the projection has no usable length.
    /// </summary>
    private static Vector3? ProjectToGroundPlane(Vector3 direction)
    {
        Vector3 ground = new(direction.X, 0f, direction.Z);
        return ground.LengthSquared() < GroundSeparationSquared ? null : ground.Normalized();
    }

    /// <summary>
    /// Resolves a transform through the spatial trait while preserving Godot lifetime safety: freed or out-of-tree
    /// node-backed providers are rejected, and plain trait providers are read directly.
    /// </summary>
    private static bool TryGetLiveTransform(ISpatial spatial, out Transform3D transform)
    {
        ArgumentNullException.ThrowIfNull(spatial);
        transform = default;
        if ((spatial is GodotObject godotObject && !GodotObject.IsInstanceValid(godotObject))
            || (spatial is Node node && !node.IsInsideTree()))
        {
            return false;
        }

        transform = spatial.GlobalTransform;
        return true;
    }

    /// <summary>Determines whether a Godot node is a live, in-tree instance.</summary>
    private static bool IsLiveNode(Node node)
        => GodotObject.IsInstanceValid(node) && node.IsInsideTree();
}
