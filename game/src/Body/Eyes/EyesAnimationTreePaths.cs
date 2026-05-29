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

    /// <summary>
    /// ARKit blend-shape name for inward right-eye look.
    /// </summary>
    public const string EyeLookInRightBlendShapeName = "eyeLookInRight";

    /// <summary>
    /// ARKit blend-shape name for inward left-eye look.
    /// </summary>
    public const string EyeLookInLeftBlendShapeName = "eyeLookInLeft";

    /// <summary>
    /// ARKit blend-shape name for outward right-eye look.
    /// </summary>
    public const string EyeLookOutRightBlendShapeName = "eyeLookOutRight";

    /// <summary>
    /// ARKit blend-shape name for outward left-eye look.
    /// </summary>
    public const string EyeLookOutLeftBlendShapeName = "eyeLookOutLeft";

    /// <summary>
    /// ARKit blend-shape name for upward right-eye look.
    /// </summary>
    public const string EyeLookUpRightBlendShapeName = "eyeLookUpRight";

    /// <summary>
    /// ARKit blend-shape name for upward left-eye look.
    /// </summary>
    public const string EyeLookUpLeftBlendShapeName = "eyeLookUpLeft";

    /// <summary>
    /// ARKit blend-shape name for downward right-eye look.
    /// </summary>
    public const string EyeLookDownRightBlendShapeName = "eyeLookDownRight";

    /// <summary>
    /// ARKit blend-shape name for downward left-eye look.
    /// </summary>
    public const string EyeLookDownLeftBlendShapeName = "eyeLookDownLeft";

    /// <summary>
    /// ARKit blend-shape name for left-eye blink.
    /// </summary>
    public const string EyeBlinkLeftBlendShapeName = "eyeBlinkLeft";

    /// <summary>
    /// ARKit blend-shape name for right-eye blink.
    /// </summary>
    public const string EyeBlinkRightBlendShapeName = "eyeBlinkRight";

    private static readonly string[] _horizontalLookBlendShapeNames =
    [
        EyeLookInRightBlendShapeName,
        EyeLookInLeftBlendShapeName,
        EyeLookOutRightBlendShapeName,
        EyeLookOutLeftBlendShapeName,
    ];

    private static readonly string[] _verticalLookBlendShapeNames =
    [
        EyeLookUpRightBlendShapeName,
        EyeLookUpLeftBlendShapeName,
        EyeLookDownRightBlendShapeName,
        EyeLookDownLeftBlendShapeName,
    ];

    private static readonly string[] _blinkBlendShapeNames =
    [
        EyeBlinkLeftBlendShapeName,
        EyeBlinkRightBlendShapeName,
    ];

    /// <summary>
    /// Gets the full set of eye blend-shape names owned by the Eyes component.
    /// </summary>
    public static IReadOnlySet<string> EyeBlendShapeNames
    {
        get;
    } = new HashSet<string>(StringComparer.Ordinal)
    {
        EyeLookInRightBlendShapeName,
        EyeLookInLeftBlendShapeName,
        EyeLookOutRightBlendShapeName,
        EyeLookOutLeftBlendShapeName,
        EyeLookUpRightBlendShapeName,
        EyeLookUpLeftBlendShapeName,
        EyeLookDownRightBlendShapeName,
        EyeLookDownLeftBlendShapeName,
        EyeBlinkLeftBlendShapeName,
        EyeBlinkRightBlendShapeName,
    };

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
    /// Builds every eye blend-shape filter path for the supplied skeleton and mesh names.
    /// </summary>
    public static IReadOnlyList<NodePath> BuildEyeBlendShapeFilterPaths(
        string skeletonNodeName,
        IReadOnlyList<string> meshNodeNames
    )
        => ToNodePaths(
            BuildFilterPathStrings(skeletonNodeName, meshNodeNames, _horizontalLookBlendShapeNames),
            BuildFilterPathStrings(skeletonNodeName, meshNodeNames, _verticalLookBlendShapeNames),
            BuildFilterPathStrings(skeletonNodeName, meshNodeNames, _blinkBlendShapeNames));

    /// <summary>
    /// Builds horizontal look blend-shape filter paths for the supplied skeleton and mesh names.
    /// </summary>
    public static IReadOnlyList<NodePath> BuildHorizontalLookBlendShapeFilterPaths(
        string skeletonNodeName,
        IReadOnlyList<string> meshNodeNames
    )
        => ToNodePaths(BuildHorizontalLookBlendShapeFilterPathStrings(skeletonNodeName, meshNodeNames));

    /// <summary>
    /// Builds horizontal look blend-shape filter path strings for the supplied skeleton and mesh names.
    /// </summary>
    public static IReadOnlyList<string> BuildHorizontalLookBlendShapeFilterPathStrings(
        string skeletonNodeName,
        IReadOnlyList<string> meshNodeNames
    )
        => BuildFilterPathStrings(skeletonNodeName, meshNodeNames, _horizontalLookBlendShapeNames);

    /// <summary>
    /// Builds vertical look blend-shape filter paths for the supplied skeleton and mesh names.
    /// </summary>
    public static IReadOnlyList<NodePath> BuildVerticalLookBlendShapeFilterPaths(
        string skeletonNodeName,
        IReadOnlyList<string> meshNodeNames
    )
        => ToNodePaths(BuildVerticalLookBlendShapeFilterPathStrings(skeletonNodeName, meshNodeNames));

    /// <summary>
    /// Builds vertical look blend-shape filter path strings for the supplied skeleton and mesh names.
    /// </summary>
    public static IReadOnlyList<string> BuildVerticalLookBlendShapeFilterPathStrings(
        string skeletonNodeName,
        IReadOnlyList<string> meshNodeNames
    )
        => BuildFilterPathStrings(skeletonNodeName, meshNodeNames, _verticalLookBlendShapeNames);

    /// <summary>
    /// Builds blink blend-shape filter paths for the supplied skeleton and mesh names.
    /// </summary>
    public static IReadOnlyList<NodePath> BuildBlinkBlendShapeFilterPaths(
        string skeletonNodeName,
        IReadOnlyList<string> meshNodeNames
    )
        => ToNodePaths(BuildBlinkBlendShapeFilterPathStrings(skeletonNodeName, meshNodeNames));

    /// <summary>
    /// Builds blink blend-shape filter path strings for the supplied skeleton and mesh names.
    /// </summary>
    public static IReadOnlyList<string> BuildBlinkBlendShapeFilterPathStrings(
        string skeletonNodeName,
        IReadOnlyList<string> meshNodeNames
    )
        => BuildFilterPathStrings(skeletonNodeName, meshNodeNames, _blinkBlendShapeNames);

    /// <summary>
    /// Returns whether a filter path belongs to the supplied eye blend-shape set.
    /// </summary>
    public static bool IsEyeBlendShapeFilterPath(
        string path,
        string skeletonNodeName,
        IReadOnlyList<string> meshNodeNames
    )
    {
        IReadOnlyList<string> horizontalPaths = BuildHorizontalLookBlendShapeFilterPathStrings(skeletonNodeName, meshNodeNames);
        IReadOnlyList<string> verticalPaths = BuildVerticalLookBlendShapeFilterPathStrings(skeletonNodeName, meshNodeNames);
        IReadOnlyList<string> blinkPaths = BuildBlinkBlendShapeFilterPathStrings(skeletonNodeName, meshNodeNames);
        return ContainsPath(horizontalPaths, path) || ContainsPath(verticalPaths, path) || ContainsPath(blinkPaths, path);
    }

    private static IReadOnlyList<string> BuildFilterPathStrings(
        string skeletonNodeName,
        IReadOnlyList<string> meshNodeNames,
        IReadOnlyList<string> blendShapeNames
    )
    {
        string trimmedSkeletonNodeName = string.IsNullOrWhiteSpace(skeletonNodeName)
            ? throw new ArgumentException("Skeleton node name must not be empty.", nameof(skeletonNodeName))
            : skeletonNodeName.Trim();

        var paths = new List<string>(meshNodeNames.Count * blendShapeNames.Count);
        for (int meshIndex = 0; meshIndex < meshNodeNames.Count; meshIndex++)
        {
            string meshNodeName = string.IsNullOrWhiteSpace(meshNodeNames[meshIndex])
                ? throw new ArgumentException("Mesh node names must not be empty.", nameof(meshNodeNames))
                : meshNodeNames[meshIndex].Trim();

            for (int blendShapeIndex = 0; blendShapeIndex < blendShapeNames.Count; blendShapeIndex++)
            {
                paths.Add($"{trimmedSkeletonNodeName}/{meshNodeName}:{blendShapeNames[blendShapeIndex]}");
            }
        }

        return paths;
    }

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
