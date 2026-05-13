using Godot;

namespace AlleyCat.Body.Eyes;

/// <summary>
/// Shared AnimationTree node and parameter paths for BODY-004 Eyes blend-shape control.
/// </summary>
public static class EyesAnimationTreePaths
{
    /// <summary>
    /// Horizontal eye look animation source node.
    /// </summary>
    public const string HorizontalLookAnimationNode = "EyesHorizontalLookAnimation";

    /// <summary>
    /// Horizontal eye look time seek node.
    /// </summary>
    public const string HorizontalLookSeekNode = "EyesHorizontalLookSeek";

    /// <summary>
    /// Horizontal eye look partial blend node.
    /// </summary>
    public const string HorizontalLookBlendNode = "EyesHorizontalLookBlend";

    /// <summary>
    /// Vertical eye look animation source node.
    /// </summary>
    public const string VerticalLookAnimationNode = "EyesVerticalLookAnimation";

    /// <summary>
    /// Vertical eye look time seek node.
    /// </summary>
    public const string VerticalLookSeekNode = "EyesVerticalLookSeek";

    /// <summary>
    /// Vertical eye look partial blend node.
    /// </summary>
    public const string VerticalLookBlendNode = "EyesVerticalLookBlend";

    /// <summary>
    /// Blink animation source node.
    /// </summary>
    public const string BlinkAnimationNode = "EyesBlinkAnimation";

    /// <summary>
    /// Blink animation one-shot playback node.
    /// </summary>
    public const string BlinkOneShotNode = "EyesBlinkOneShot";

    /// <summary>
    /// Blink animation time-scale node used to apply the configured visible blink duration.
    /// </summary>
    public const string BlinkTimeScaleNode = "EyesBlinkTimeScale";

    /// <summary>
    /// AnimationPlayer clip name for horizontal eye look blend shapes.
    /// </summary>
    public const string HorizontalLookAnimationName = "eyes/Eyes Right Left";

    /// <summary>
    /// AnimationPlayer clip name for vertical eye look blend shapes.
    /// </summary>
    public const string VerticalLookAnimationName = "eyes/Eyes Up Down";

    /// <summary>
    /// AnimationPlayer clip name for blinking blend shapes.
    /// </summary>
    public const string BlinkAnimationName = "eyes/Eyes Blink";

    private const string ParametersPrefix = "parameters/";
    private const string BlendAmountSuffix = "/blend_amount";
    private const string SeekRequestSuffix = "/seek_request";
    private const string OneShotRequestSuffix = "/request";
    private const string TimeScaleSuffix = "/scale";

    private static readonly string[] _horizontalLookBlendShapeFilterPaths =
    [
        "GeneralSkeleton/Female_high-poly_export:eyeLookInRight",
        "GeneralSkeleton/Female_high-poly_export:eyeLookInLeft",
        "GeneralSkeleton/Female_high-poly_export:eyeLookOutRight",
        "GeneralSkeleton/Female_high-poly_export:eyeLookOutLeft",
        "GeneralSkeleton/Female_eyelashes01_export:eyeLookInRight",
        "GeneralSkeleton/Female_eyelashes01_export:eyeLookInLeft",
        "GeneralSkeleton/Female_eyelashes01_export:eyeLookOutRight",
        "GeneralSkeleton/Female_eyelashes01_export:eyeLookOutLeft",
        "GeneralSkeleton/Female_body_export:eyeLookInRight",
        "GeneralSkeleton/Female_body_export:eyeLookInLeft",
        "GeneralSkeleton/Female_body_export:eyeLookOutRight",
        "GeneralSkeleton/Female_body_export:eyeLookOutLeft",
    ];

    private static readonly string[] _verticalLookBlendShapeFilterPaths =
    [
        "GeneralSkeleton/Female_high-poly_export:eyeLookUpRight",
        "GeneralSkeleton/Female_high-poly_export:eyeLookUpLeft",
        "GeneralSkeleton/Female_high-poly_export:eyeLookDownRight",
        "GeneralSkeleton/Female_high-poly_export:eyeLookDownLeft",
        "GeneralSkeleton/Female_eyelashes01_export:eyeLookUpRight",
        "GeneralSkeleton/Female_eyelashes01_export:eyeLookUpLeft",
        "GeneralSkeleton/Female_eyelashes01_export:eyeLookDownRight",
        "GeneralSkeleton/Female_eyelashes01_export:eyeLookDownLeft",
        "GeneralSkeleton/Female_body_export:eyeLookUpRight",
        "GeneralSkeleton/Female_body_export:eyeLookUpLeft",
        "GeneralSkeleton/Female_body_export:eyeLookDownRight",
        "GeneralSkeleton/Female_body_export:eyeLookDownLeft",
    ];

    private static readonly string[] _blinkBlendShapeFilterPaths =
    [
        "GeneralSkeleton/Female_eyelashes01_export:eyeBlinkLeft",
        "GeneralSkeleton/Female_eyelashes01_export:eyeBlinkRight",
        "GeneralSkeleton/Female_body_export:eyeBlinkLeft",
        "GeneralSkeleton/Female_body_export:eyeBlinkRight",
    ];

    private static readonly IReadOnlyList<string> _horizontalLookBlendShapeFilterPathStrings =
        Array.AsReadOnly(_horizontalLookBlendShapeFilterPaths);

    private static readonly IReadOnlyList<string> _verticalLookBlendShapeFilterPathStrings =
        Array.AsReadOnly(_verticalLookBlendShapeFilterPaths);

    private static readonly IReadOnlyList<string> _blinkBlendShapeFilterPathStrings =
        Array.AsReadOnly(_blinkBlendShapeFilterPaths);

    /// <summary>
    /// Gets the horizontal look seek parameter path.
    /// </summary>
    public static StringName GetHorizontalLookSeekParameter() => new(GetHorizontalLookSeekParameterPath());

    /// <summary>
    /// Gets the horizontal look seek parameter path string.
    /// </summary>
    public static string GetHorizontalLookSeekParameterPath() => $"{ParametersPrefix}{HorizontalLookSeekNode}{SeekRequestSuffix}";

    /// <summary>
    /// Gets the vertical look seek parameter path.
    /// </summary>
    public static StringName GetVerticalLookSeekParameter() => new(GetVerticalLookSeekParameterPath());

    /// <summary>
    /// Gets the vertical look seek parameter path string.
    /// </summary>
    public static string GetVerticalLookSeekParameterPath() => $"{ParametersPrefix}{VerticalLookSeekNode}{SeekRequestSuffix}";

    /// <summary>
    /// Gets the blink one-shot request parameter path.
    /// </summary>
    public static StringName GetBlinkOneShotRequestParameter() => new(GetBlinkOneShotRequestParameterPath());

    /// <summary>
    /// Gets the blink one-shot request parameter path string.
    /// </summary>
    public static string GetBlinkOneShotRequestParameterPath() => $"{ParametersPrefix}{BlinkOneShotNode}{OneShotRequestSuffix}";

    /// <summary>
    /// Gets the blink time-scale parameter path.
    /// </summary>
    public static StringName GetBlinkTimeScaleParameter() => new(GetBlinkTimeScaleParameterPath());

    /// <summary>
    /// Gets the blink time-scale parameter path string.
    /// </summary>
    public static string GetBlinkTimeScaleParameterPath() => $"{ParametersPrefix}{BlinkTimeScaleNode}{TimeScaleSuffix}";

    /// <summary>
    /// Gets the horizontal look blend amount parameter path.
    /// </summary>
    public static StringName GetHorizontalLookBlendParameter() => new(GetHorizontalLookBlendParameterPath());

    /// <summary>
    /// Gets the horizontal look blend amount parameter path string.
    /// </summary>
    public static string GetHorizontalLookBlendParameterPath() => $"{ParametersPrefix}{HorizontalLookBlendNode}{BlendAmountSuffix}";

    /// <summary>
    /// Gets the vertical look blend amount parameter path.
    /// </summary>
    public static StringName GetVerticalLookBlendParameter() => new(GetVerticalLookBlendParameterPath());

    /// <summary>
    /// Gets the vertical look blend amount parameter path string.
    /// </summary>
    public static string GetVerticalLookBlendParameterPath() => $"{ParametersPrefix}{VerticalLookBlendNode}{BlendAmountSuffix}";

    /// <summary>
    /// Gets the exact eye blend-shape filter paths used by the reference eye partial blends.
    /// </summary>
    public static IReadOnlyList<NodePath> GetEyeBlendShapeFilterPaths()
        => ToNodePaths(_horizontalLookBlendShapeFilterPaths, _verticalLookBlendShapeFilterPaths, _blinkBlendShapeFilterPaths);

    /// <summary>
    /// Gets the exact horizontal look blend-shape filter paths used by the reference eye partial blend.
    /// </summary>
    public static IReadOnlyList<NodePath> GetHorizontalLookBlendShapeFilterPaths()
        => ToNodePaths(_horizontalLookBlendShapeFilterPaths);

    /// <summary>
    /// Gets the exact horizontal look blend-shape filter path strings used by the reference eye partial blend.
    /// </summary>
    public static IReadOnlyList<string> GetHorizontalLookBlendShapeFilterPathStrings()
        => _horizontalLookBlendShapeFilterPathStrings;

    /// <summary>
    /// Gets the exact vertical look blend-shape filter paths used by the reference eye partial blend.
    /// </summary>
    public static IReadOnlyList<NodePath> GetVerticalLookBlendShapeFilterPaths()
        => ToNodePaths(_verticalLookBlendShapeFilterPaths);

    /// <summary>
    /// Gets the exact vertical look blend-shape filter path strings used by the reference eye partial blend.
    /// </summary>
    public static IReadOnlyList<string> GetVerticalLookBlendShapeFilterPathStrings()
        => _verticalLookBlendShapeFilterPathStrings;

    /// <summary>
    /// Gets the exact blink blend-shape filter paths used by the reference eye partial blend.
    /// </summary>
    public static IReadOnlyList<NodePath> GetBlinkBlendShapeFilterPaths()
        => ToNodePaths(_blinkBlendShapeFilterPaths);

    /// <summary>
    /// Gets the exact blink blend-shape filter path strings used by the reference eye partial blend.
    /// </summary>
    public static IReadOnlyList<string> GetBlinkBlendShapeFilterPathStrings()
        => _blinkBlendShapeFilterPathStrings;

    /// <summary>
    /// Returns whether a filter path belongs to the configured eye blend-shape set.
    /// </summary>
    public static bool IsEyeBlendShapeFilterPath(string path)
        => ContainsPath(_horizontalLookBlendShapeFilterPaths, path)
        || ContainsPath(_verticalLookBlendShapeFilterPaths, path)
        || ContainsPath(_blinkBlendShapeFilterPaths, path);

    private static bool ContainsPath(IReadOnlyList<string> paths, string path)
    {
        for (int index = 0; index < paths.Count; index++)
        {
            if (string.Equals(path, paths[index], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<NodePath> ToNodePaths(params IReadOnlyList<string>[] pathGroups)
    {
        int pathCount = 0;
        for (int groupIndex = 0; groupIndex < pathGroups.Length; groupIndex++)
        {
            pathCount += pathGroups[groupIndex].Count;
        }

        var paths = new NodePath[pathCount];
        int pathIndex = 0;
        for (int groupIndex = 0; groupIndex < pathGroups.Length; groupIndex++)
        {
            IReadOnlyList<string> pathGroup = pathGroups[groupIndex];
            for (int groupPathIndex = 0; groupPathIndex < pathGroup.Count; groupPathIndex++)
            {
                paths[pathIndex] = new NodePath(pathGroup[groupPathIndex]);
                pathIndex++;
            }
        }

        return paths;
    }
}
