using AlleyCat.XR.HandTracking;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>
/// Unit coverage for the tracked joint topology that drives parent-relative finger retargeting: the
/// anatomical parent chains, the source-only metacarpal policy, and the 20/15 tracked-versus-destination
/// joint split (XR-002 TR12, TR14).
/// </summary>
public sealed class XRHandJointsTests
{
    /// <summary>
    /// Every non-wrist joint derives relative to its actual anatomical parent: metacarpals parent to the
    /// wrist, non-thumb proximals to their finger's metacarpal, and intermediate/distal joints to the
    /// preceding joint (XR-002 TR14).
    /// </summary>
    [Fact]
    public void GetRequiredSourceParent_FollowsAnatomicalChains()
    {
        Assert.Null(XRHandJoints.GetRequiredSourceParent(XRHandJoint.Wrist));

        Assert.Equal(XRHandJoint.Wrist, XRHandJoints.GetRequiredSourceParent(XRHandJoint.ThumbMetacarpal));
        Assert.Equal(XRHandJoint.ThumbMetacarpal, XRHandJoints.GetRequiredSourceParent(XRHandJoint.ThumbProximal));
        Assert.Equal(XRHandJoint.ThumbProximal, XRHandJoints.GetRequiredSourceParent(XRHandJoint.ThumbDistal));

        foreach ((XRHandJoint metacarpal, XRHandJoint proximal, XRHandJoint intermediate, XRHandJoint distal) in
                 NonThumbChains())
        {
            Assert.Equal(XRHandJoint.Wrist, XRHandJoints.GetRequiredSourceParent(metacarpal));
            Assert.Equal(metacarpal, XRHandJoints.GetRequiredSourceParent(proximal));
            Assert.Equal(proximal, XRHandJoints.GetRequiredSourceParent(intermediate));
            Assert.Equal(intermediate, XRHandJoints.GetRequiredSourceParent(distal));
        }
    }

    /// <summary>
    /// The tracked set covers the wrist, the thumb chain, and each non-thumb finger's metacarpal plus
    /// three-phalanx chain — 20 joints, anatomically ordered so every joint's parent appears earlier
    /// (XR-002 TR14).
    /// </summary>
    [Fact]
    public void TrackedJoints_CoverTwentyAnatomicallyOrderedJoints()
    {
        Assert.Equal(20, XRHandJoints.TrackedJoints.Length);
        Assert.Equal(20, new HashSet<XRHandJoint>(XRHandJoints.TrackedJoints).Count);
        Assert.Equal(XRHandJoint.Wrist, XRHandJoints.TrackedJoints[0]);

        for (int index = 1; index < XRHandJoints.TrackedJoints.Length; index++)
        {
            XRHandJoint parent = XRHandJoints.GetRequiredSourceParent(XRHandJoints.TrackedJoints[index])
                ?? throw new InvalidOperationException("Non-wrist joints always have a source parent.");

            Assert.Contains(parent, XRHandJoints.TrackedJoints[..index]);
        }
    }

    /// <summary>
    /// The destination set is exactly the 15 finger joints that own canonical bones: the thumb chain plus
    /// each non-thumb proximal/intermediate/distal chain. The wrist and the source-only non-thumb
    /// metacarpals are never destination joints (XR-002 TR12, TR14).
    /// </summary>
    [Fact]
    public void DestinationJoints_AreExactlyTheFifteenFingerJoints()
    {
        Assert.Equal(15, XRHandJoints.DestinationJoints.Length);
        Assert.Equal(15, new HashSet<XRHandJoint>(XRHandJoints.DestinationJoints).Count);

        Assert.DoesNotContain(XRHandJoint.Wrist, XRHandJoints.DestinationJoints);
        Assert.DoesNotContain(XRHandJoint.IndexMetacarpal, XRHandJoints.DestinationJoints);
        Assert.DoesNotContain(XRHandJoint.MiddleMetacarpal, XRHandJoints.DestinationJoints);
        Assert.DoesNotContain(XRHandJoint.RingMetacarpal, XRHandJoints.DestinationJoints);
        Assert.DoesNotContain(XRHandJoint.LittleMetacarpal, XRHandJoints.DestinationJoints);

        foreach (XRHandJoint joint in XRHandJoints.DestinationJoints)
        {
            Assert.Contains(joint, XRHandJoints.TrackedJoints);
        }
    }

    private static IEnumerable<(XRHandJoint Metacarpal, XRHandJoint Proximal, XRHandJoint Intermediate, XRHandJoint Distal)>
        NonThumbChains()
        => [
            (XRHandJoint.IndexMetacarpal, XRHandJoint.IndexProximal, XRHandJoint.IndexIntermediate, XRHandJoint.IndexDistal),
            (XRHandJoint.MiddleMetacarpal, XRHandJoint.MiddleProximal, XRHandJoint.MiddleIntermediate, XRHandJoint.MiddleDistal),
            (XRHandJoint.RingMetacarpal, XRHandJoint.RingProximal, XRHandJoint.RingIntermediate, XRHandJoint.RingDistal),
            (XRHandJoint.LittleMetacarpal, XRHandJoint.LittleProximal, XRHandJoint.LittleIntermediate, XRHandJoint.LittleDistal),
        ];
}
