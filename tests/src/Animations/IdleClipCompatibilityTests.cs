using AlleyCat.Animations;
using Xunit;

namespace AlleyCat.Tests.Animations;

/// <summary>ANIM-003 TR11: SOMA-mapped tracks must resolve; other tracks are optional.</summary>
public sealed class IdleClipCompatibilityTests
{
    private static readonly HashSet<string> _maleLikeBones = ["Root", "Hips", "Spine", "Chest", "LeftFoot"];

    /// <summary>Tracks on bones outside the SOMA map, such as breasts or Root, are optional no-ops.</summary>
    [Fact]
    public void NonMappedTracksAreOptional()
    {
        IReadOnlyList<string> unresolved = IdleClipCompatibility.FindUnresolvedMappedTracks(
            [("%GeneralSkeleton", "Root"), ("%GeneralSkeleton", "Hips"), ("%GeneralSkeleton", "breast_l"), ("GeneralSkeleton", "breast_r")],
            "GeneralSkeleton",
            _maleLikeBones.Contains);

        Assert.Empty(unresolved);
    }

    /// <summary>A mapped track missing from the skeleton, or bound to another node, makes the clip incompatible.</summary>
    [Fact]
    public void UnresolvedMappedTracksAreReported()
    {
        IReadOnlyList<string> unresolved = IdleClipCompatibility.FindUnresolvedMappedTracks(
            [("%GeneralSkeleton", "Hips"), ("%GeneralSkeleton", "RightFoot"), ("Armature", "LeftFoot")],
            "GeneralSkeleton",
            _maleLikeBones.Contains);

        Assert.Equal(["%GeneralSkeleton:RightFoot", "Armature:LeftFoot"], unresolved);
    }

    /// <summary>
    /// Tracks resolve against the character's actual skeleton node: a clip bound to <c>GeneralSkeleton</c> does not
    /// resolve on a character whose native skeleton has another name, even when the bone names match.
    /// </summary>
    [Fact]
    public void TracksResolveAgainstTheActualSkeletonName()
    {
        (string, string)[] tracks = [("%GeneralSkeleton", "Hips"), ("%GeneralSkeleton", "LeftFoot")];

        Assert.Empty(IdleClipCompatibility.FindUnresolvedMappedTracks(tracks, "GeneralSkeleton", _maleLikeBones.Contains));
        Assert.Equal(
            ["%GeneralSkeleton:Hips", "%GeneralSkeleton:LeftFoot"],
            IdleClipCompatibility.FindUnresolvedMappedTracks(tracks, "Skeleton3D", _maleLikeBones.Contains));
    }
}
