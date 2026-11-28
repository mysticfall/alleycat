using AlleyCat.Motion;
using Godot;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>
/// Realistic, fully articulated SOMA poses for motion fixtures, read from the reviewed feminine source's normal forward
/// role rather than any idle clip.
/// </summary>
internal static class ReviewedPoseFixture
{
    private const string SourcePath = "res://assets/animations/locomotion/feminine/landing-ablation-01.json";

    /// <summary>
    /// Four consecutive authored poses starting at <paramref name="sourceFrame" /> (default: the role's first frame).
    /// </summary>
    public static Transform3D[][] Window(SomaMotionData data, int? sourceFrame = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var source = new ReviewedMotionSource(ProjectSettings.GlobalizePath(SourcePath), data);
        ReviewedMotionSource.Role role = source.GetRole("feminine", "normal", "forward");
        int joints = data.Names.Length;
        var raw = new Transform3D[joints * 4];
        source.CopyWindow(role, sourceFrame ?? role.First, raw);
        return [.. Enumerable.Range(0, 4).Select(f => raw.AsSpan(f * joints, joints).ToArray())];
    }

    /// <summary>One authored pose: the first frame of <see cref="Window" />.</summary>
    public static Transform3D[] Pose(SomaMotionData data) => Window(data)[0];
}
