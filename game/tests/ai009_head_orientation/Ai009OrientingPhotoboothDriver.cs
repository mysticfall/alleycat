using AlleyCat.Character;
using AlleyCat.Common;
using AlleyCat.IK;
using AlleyCat.Mind.Attention;
using AlleyCat.Vision;
using Godot;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.Testing;

/// <summary>
/// Test-only GDScript bridge for AI-009 orienting photobooth scenario control and non-visual runtime probes.
/// </summary>
/// <remarks>
/// <para>
/// The driver simulates the AI-007 gaze-selector role: it assigns and clears the gaze anchor through the installed
/// character's <see cref="IVision"/> exactly as the production selector would, while the template-composed AI-007
/// selector is disabled so it cannot override the authored scenario timeline.
/// </para>
/// <para>
/// Head probes read the solved skeleton viewpoint rather than controller internals so photobooth assertions stay
/// independent of the runtime implementation under verification. The commanded influence is exposed separately as a
/// supporting probe only.
/// </para>
/// </remarks>
[GlobalClass]
public sealed partial class Ai009OrientingPhotoboothDriver : Node
{
    private const float DisabledIntervalSeconds = 99f;
    private const string HorizontalSeekParameter = "parameters/EyesHorizontalLookSeek/seek_request";
    private const string VerticalSeekParameter = "parameters/EyesVerticalLookSeek/seek_request";

    private readonly Dictionary<string, CharacterRig> _characters = new(StringComparer.Ordinal);

    /// <summary>
    /// Commits character component projection for both photobooth NPCs, disables the template-composed AI-007
    /// selector, and configures deterministic eye presentation (no blinks, saccades, or visual surveys).
    /// </summary>
    public void Activate()
    {
        if (_characters.Count > 0)
        {
            return;
        }

        CharacterRig tallRig = CreateRig("TallNpc", "Male");
        CharacterRig shortRig = CreateRig("ShortNpc", "Female");
        _characters[tallRig.Name] = tallRig;
        _characters[shortRig.Name] = shortRig;

        foreach (CharacterRig rig in _characters.Values)
        {
            rig.Character.RefreshComponents();
        }

        // Disable the production selector immediately after projection (same call stack, before any process frame)
        // so its initial evaluation cannot override the driver-authored gaze timeline. The orienting controller
        // stays active: it only consumes the assignments.
        foreach (CharacterRig rig in _characters.Values)
        {
            rig.Selector.ProcessMode = ProcessModeEnum.Disabled;
        }

        foreach (CharacterRig rig in _characters.Values)
        {
            EyesBehaviour eyes = ResolveEyes(rig);
            eyes.MinimumBlinkInterval = DisabledIntervalSeconds;
            eyes.MaximumBlinkInterval = DisabledIntervalSeconds;
            eyes.SaccadeInterval = DisabledIntervalSeconds;
            eyes.SaccadeAmplitude = 0f;
            eyes.VisualSurveyIntervalSeconds = DisabledIntervalSeconds;
            eyes.AnimationTree!.Active = true;
            rig.Eyes = eyes;
        }
    }

    /// <summary>Assigns the gaze anchor for the named character, simulating the AI-007 selector role.</summary>
    public void AssignLookTarget(string characterId, Node3D anchor)
        => Eyes(characterId).SetLookTarget(anchor);

    /// <summary>Clears the gaze anchor for the named character, simulating the AI-007 selector role.</summary>
    public void ClearLookTarget(string characterId)
        => Eyes(characterId).ClearLookTarget();

    /// <summary>Reports whether the named character currently retains an explicit gaze assignment.</summary>
    public bool HasAssignedLookTarget(string characterId)
        => Eyes(characterId).HasRuntimeLookTarget();

    /// <summary>
    /// Returns the angle in degrees between the solved head forward axis and the direction to the current gaze
    /// anchor, or -1 when no anchor is assigned. This is the gaze eccentricity the eyes must carry.
    /// </summary>
    public float GetHeadToAnchorAngleDegrees(string characterId)
    {
        CharacterRig rig = Rig(characterId);
        Node3D? anchor = rig.Eyes?.LookTarget;
        if (anchor is null || !IsInstanceValid(anchor))
        {
            return -1f;
        }

        Vector3 headForward = -rig.Viewpoint.GlobalBasis.Orthonormalized().Z;
        Vector3 anchorDirection = (anchor.GlobalPosition - rig.Viewpoint.GlobalPosition).Normalized();
        return Mathf.RadToDeg(headForward.AngleTo(anchorDirection));
    }

    /// <summary>Returns the solved head yaw in degrees relative to the character rest frame; positive is left.</summary>
    public float GetHeadYawDegrees(string characterId)
        => Mathf.RadToDeg(HeadEuler(characterId).Y);

    /// <summary>Returns the solved head pitch in degrees relative to the character rest frame; positive is up.</summary>
    public float GetHeadPitchDegrees(string characterId)
        => Mathf.RadToDeg(HeadEuler(characterId).X);

    /// <summary>
    /// Returns the current head-intent influence commanded by the orienting controller, as a supporting probe that
    /// is deliberately not the primary photobooth oracle.
    /// </summary>
    public float GetInfluence(string characterId)
        => Rig(characterId).Controller.GetTargetIntent().DesiredInfluence;

    /// <summary>
    /// Returns the horizontal eye-presentation seek value: 0.5 is eye-neutral, 1.0 is full left, 0.0 is full right.
    /// </summary>
    public float GetEyeSeekHorizontal(string characterId)
        => ReadEyeSeek(characterId, HorizontalSeekParameter);

    /// <summary>Returns the vertical eye-presentation seek value: 0.5 is eye-neutral, 1.0 is full down, 0.0 is full up.</summary>
    public float GetEyeSeekVertical(string characterId)
        => ReadEyeSeek(characterId, VerticalSeekParameter);

    /// <summary>Returns the solved viewpoint world height in metres, for geometry sanity probes.</summary>
    public float GetViewpointHeight(string characterId)
        => Rig(characterId).Viewpoint.GlobalPosition.Y;

    private CharacterRig CreateRig(string nodeName, string bodyChildName)
    {
        CharacterHub character = this.RequireNode<CharacterHub>($"../Subject/{nodeName}");
        CharacterIK characterIk = this.RequireNode<CharacterIK>($"../Subject/{nodeName}/CharacterIK");
        if (characterIk.HeadTargetIntentProvider is not OrientingController controller)
        {
            throw new InvalidOperationException(
                $"AI-009 photobooth character '{nodeName}' must wire an {nameof(OrientingController)} into its "
                + $"{nameof(CharacterIK.HeadTargetIntentProvider)} slot.");
        }

        AttentionGazeTargetSelector selector =
            this.RequireNode<AttentionGazeTargetSelector>($"../Subject/{nodeName}/Mind/AttentionGazeTargetSelector");
        Marker3D viewpoint =
            this.RequireNode<Marker3D>($"../Subject/{nodeName}/{bodyChildName}/GeneralSkeleton/Head/Viewpoint");

        // Vision resolution must wait for component projection, so the eyes capability is bound after
        // RefreshComponents through ResolveEyes.
        return new CharacterRig(nodeName, character, controller, selector, viewpoint);
    }

    private static EyesBehaviour ResolveEyes(CharacterRig rig)
    {
        return ((ICharacter)rig.Character).RequireVision() as EyesBehaviour
            ?? throw new InvalidOperationException(
                $"AI-009 photobooth character '{rig.Name}' must expose {nameof(EyesBehaviour)} as its IVision.");
    }

    private Vector3 HeadEuler(string characterId)
        => Rig(characterId).Viewpoint.GlobalBasis.Orthonormalized().GetEuler(EulerOrder.Yxz);

    private float ReadEyeSeek(string characterId, string parameter)
        => Eyes(characterId).AnimationTree?.Get(parameter).AsSingle() ?? float.NaN;

    private CharacterRig Rig(string characterId)
        => _characters.GetValueOrDefault(characterId)
            ?? throw new InvalidOperationException(
                $"AI-009 photobooth driver has no registered character '{characterId}'. Call Activate() first.");

    private EyesBehaviour Eyes(string characterId)
        => Rig(characterId).Eyes ?? throw new InvalidOperationException(
            $"AI-009 photobooth character '{characterId}' has no resolved eyes capability. Call Activate() first.");

    private sealed class CharacterRig(
        string name,
        CharacterHub character,
        OrientingController controller,
        AttentionGazeTargetSelector selector,
        Marker3D viewpoint)
    {
        public string Name => name;

        public CharacterHub Character => character;

        public OrientingController Controller => controller;

        public AttentionGazeTargetSelector Selector => selector;

        public Marker3D Viewpoint => viewpoint;

        public EyesBehaviour? Eyes
        {
            get; set;
        }
    }
}
