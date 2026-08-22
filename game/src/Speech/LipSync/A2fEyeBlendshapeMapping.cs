using AlleyCat.Vision;
using Godot;

namespace AlleyCat.Speech.LipSync;

/// <summary>
/// Shared ARKit eye blend-shape index resolution and directional-pair writing used by both the batch
/// and streaming Audio2Face inference paths.
/// </summary>
/// <remarks>
/// Extracted as an assembly-internal helper so the streaming record reader and the batch player share
/// one implementation, and so the mapping is unit-testable without a Godot runtime.
/// </remarks>
internal static class A2fEyeBlendshapeMapping
{
    private static readonly IReadOnlySet<string> _eyesControlledBlendshapeNames = CreateEyesControlledBlendshapeNames();

    /// <summary>
    /// Resolves the ARKit eyeLook blend-shape channel indices from a normalized name-to-index map.
    /// </summary>
    internal static bool TryGetEyeBlendshapeIndices(
        Dictionary<string, int> nameToIndex,
        out EyeBlendshapeIndices indices
    )
    {
        if (!nameToIndex.TryGetValue(LipSyncPlayer.NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookInLeftBlendShapeName), out int leftIn)
            || !nameToIndex.TryGetValue(LipSyncPlayer.NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookOutLeftBlendShapeName), out int leftOut)
            || !nameToIndex.TryGetValue(LipSyncPlayer.NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookUpLeftBlendShapeName), out int leftUp)
            || !nameToIndex.TryGetValue(LipSyncPlayer.NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookDownLeftBlendShapeName), out int leftDown)
            || !nameToIndex.TryGetValue(LipSyncPlayer.NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookInRightBlendShapeName), out int rightIn)
            || !nameToIndex.TryGetValue(LipSyncPlayer.NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookOutRightBlendShapeName), out int rightOut)
            || !nameToIndex.TryGetValue(LipSyncPlayer.NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookUpRightBlendShapeName), out int rightUp)
            || !nameToIndex.TryGetValue(LipSyncPlayer.NormalizeBlendshapeName(EyesAnimationTreePaths.EyeLookDownRightBlendShapeName), out int rightDown))
        {
            indices = default;
            return false;
        }

        indices = new EyeBlendshapeIndices(
            leftIn,
            leftOut,
            leftUp,
            leftDown,
            rightIn,
            rightOut,
            rightUp,
            rightDown
        );
        return true;
    }

    /// <summary>
    /// Writes a signed rotation value as an opposing blend-shape pair, clamped to unit weight.
    /// </summary>
    internal static void WriteDirectionalPair(
        float[] frame,
        int positiveIndex,
        int negativeIndex,
        float value,
        float scale
    )
    {
        float scaled = value * scale;
        frame[positiveIndex] = Mathf.Clamp(scaled, 0f, 1f);
        frame[negativeIndex] = Mathf.Clamp(-scaled, 0f, 1f);
    }

    /// <summary>
    /// Returns whether a blend-shape name belongs to the eye channels controlled by the Eyes component,
    /// which must be stripped from inference output before playback.
    /// </summary>
    internal static bool IsEyesControlledBlendshapeName(string blendshapeName)
        => _eyesControlledBlendshapeNames.Contains(LipSyncPlayer.NormalizeBlendshapeName(blendshapeName));

    private static IReadOnlySet<string> CreateEyesControlledBlendshapeNames()
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string blendshapeName in EyesAnimationTreePaths.EyeBlendShapeNames)
        {
            _ = names.Add(LipSyncPlayer.NormalizeBlendshapeName(blendshapeName));
        }

        return names;
    }

    /// <summary>
    /// Indices of the eight ARKit eyeLook blend-shape channels within an inference frame.
    /// </summary>
    internal readonly record struct EyeBlendshapeIndices(
        int LeftIn,
        int LeftOut,
        int LeftUp,
        int LeftDown,
        int RightIn,
        int RightOut,
        int RightUp,
        int RightDown
    );
}
