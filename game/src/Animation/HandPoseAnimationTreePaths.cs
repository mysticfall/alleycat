using Godot;

namespace AlleyCat.Animation;

/// <summary>
/// Shared AnimationTree node and parameter paths for ANIM-001 hand-pose blending.
/// </summary>
public static class HandPoseAnimationTreePaths
{
    /// <summary>
    /// Root blend-tree node containing the pre-existing pose state machine.
    /// </summary>
    public const string UpstreamNode = "States";

    /// <summary>
    /// Animation node used as the left hand pose source.
    /// </summary>
    public const string LeftHandPoseNode = "LeftHandPose";

    /// <summary>
    /// Animation node used as the right hand pose source.
    /// </summary>
    public const string RightHandPoseNode = "RightHandPose";

    /// <summary>
    /// Filtered left hand blend node appended after upstream pose output.
    /// </summary>
    public const string LeftHandBlendNode = "LeftHandBlend";

    /// <summary>
    /// Filtered right hand blend node appended after the left hand stage.
    /// </summary>
    public const string RightHandBlendNode = "RightHandBlend";

    /// <summary>
    /// AnimationPlayer clip name used as the inert fallback when no pose is configured.
    /// </summary>
    public const string ResetAnimationName = "Reset";

    private const string ParametersPrefix = "parameters/";
    private const string BlendAmountSuffix = "/blend_amount";

    private static readonly string[] _leftFingerBones =
    [
        "LeftIndexProximal",
        "LeftIndexIntermediate",
        "LeftIndexDistal",
        "LeftMiddleProximal",
        "LeftMiddleIntermediate",
        "LeftMiddleDistal",
        "LeftRingProximal",
        "LeftRingIntermediate",
        "LeftRingDistal",
        "LeftLittleProximal",
        "LeftLittleIntermediate",
        "LeftLittleDistal",
        "LeftThumbMetacarpal",
        "LeftThumbProximal",
        "LeftThumbDistal",
    ];

    private static readonly string[] _rightFingerBones =
    [
        "RightIndexProximal",
        "RightIndexIntermediate",
        "RightIndexDistal",
        "RightMiddleProximal",
        "RightMiddleIntermediate",
        "RightMiddleDistal",
        "RightRingProximal",
        "RightRingIntermediate",
        "RightRingDistal",
        "RightLittleProximal",
        "RightLittleIntermediate",
        "RightLittleDistal",
        "RightThumbMetacarpal",
        "RightThumbProximal",
        "RightThumbDistal",
    ];

    /// <summary>
    /// Gets the pose source node name for a hand side.
    /// </summary>
    public static StringName GetPoseAnimationNodeName(HandPoseSide side)
        => new(side == HandPoseSide.Left ? LeftHandPoseNode : RightHandPoseNode);

    /// <summary>
    /// Gets the final filtered hand blend parameter path for a hand side.
    /// </summary>
    public static StringName GetHandBlendParameter(HandPoseSide side)
        => new(GetHandBlendParameterPath(side));

    /// <summary>
    /// Gets the final filtered hand blend parameter path string for a hand side.
    /// </summary>
    public static string GetHandBlendParameterPath(HandPoseSide side)
        => $"{ParametersPrefix}{GetHandBlendNodeName(side)}{BlendAmountSuffix}";

    /// <summary>
    /// Gets the nested state machine playback parameter path for the wrapped upstream tree.
    /// </summary>
    public static StringName GetNestedStateMachinePlaybackParameter()
        => new($"{ParametersPrefix}{UpstreamNode}/playback");

    /// <summary>
    /// Converts a former root state-machine parameter path to the wrapped upstream path.
    /// </summary>
    public static StringName GetNestedStateMachineParameter(string upstreamParameterPath)
        => new(GetNestedStateMachineParameterPath(upstreamParameterPath));

    /// <summary>
    /// Converts a former root state-machine parameter path to the wrapped upstream path string.
    /// </summary>
    public static string GetNestedStateMachineParameterPath(string upstreamParameterPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamParameterPath);

        return upstreamParameterPath.StartsWith(ParametersPrefix, StringComparison.Ordinal)
            ? $"{ParametersPrefix}{UpstreamNode}/{upstreamParameterPath[ParametersPrefix.Length..]}"
            : throw new ArgumentException(
                $"AnimationTree parameter path must start with '{ParametersPrefix}'.",
                nameof(upstreamParameterPath));
    }

    /// <summary>
    /// Gets the exact finger-bone filter paths for a hand side, excluding hand and arm bones.
    /// </summary>
    public static IReadOnlyList<NodePath> GetFingerFilterPaths(HandPoseSide side)
    {
        string[] bones = side == HandPoseSide.Left ? _leftFingerBones : _rightFingerBones;
        var paths = new NodePath[bones.Length];

        for (int index = 0; index < bones.Length; index++)
        {
            paths[index] = new NodePath($"%GeneralSkeleton:{bones[index]}");
        }

        return paths;
    }

    /// <summary>
    /// Returns whether a filter path belongs to the configured finger-only set for a hand side.
    /// </summary>
    public static bool IsFingerFilterPath(HandPoseSide side, NodePath path)
        => IsFingerFilterPath(side, path.ToString());

    /// <summary>
    /// Returns whether a filter path string belongs to the configured finger-only set for a hand side.
    /// </summary>
    public static bool IsFingerFilterPath(HandPoseSide side, string path)
    {
        string[] bones = side == HandPoseSide.Left ? _leftFingerBones : _rightFingerBones;

        for (int index = 0; index < bones.Length; index++)
        {
            if (path.EndsWith($":{bones[index]}", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetHandBlendNodeName(HandPoseSide side)
        => side == HandPoseSide.Left ? LeftHandBlendNode : RightHandBlendNode;
}
