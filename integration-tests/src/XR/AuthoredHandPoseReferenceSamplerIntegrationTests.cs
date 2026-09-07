using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.XR;

/// <summary>
/// Godot-runtime coverage of the generic authored hand-pose reference sampler (XR-002 TR52; INTR-001 TR16):
/// both shipped grab references validate per side through the real resource API, sampling never mutates the
/// resource, and every contract violation — missing, duplicate, disabled, or wrong-type track, wrong key count
/// or key time, invalid quaternion, unusable path — fails closed with the violated rule identifiable.
/// </summary>
public sealed class AuthoredHandPoseReferenceSamplerIntegrationTests
{
    private const string GrabBallAnimationPath =
        "res://assets/characters/reference/female/animations/Grab-ball-40.tres";

    private const string GrabPipeAnimationPath =
        "res://assets/characters/reference/female/animations/Grab-pipe-10.tres";

    private const string TrackPathPrefix = AuthoredThumbReferenceSampler.TrackPathPrefix;

    /// <summary>Both shipped grab references validate per side with 15 finite unit poses in canonical order.</summary>
    [Headless]
    [Fact]
    public void BothShippedGrabReferences_ValidatePerSide()
    {
        foreach (string path in new[] { GrabBallAnimationPath, GrabPipeAnimationPath })
        {
            foreach (LimbSide side in new[] { LimbSide.Left, LimbSide.Right })
            {
                Assert.True(
                    AuthoredHandPoseReferenceSampler.TrySample(path, side, out AuthoredHandPoseSideReference reference, out string error),
                    $"{path} {side}: {error}");

                Assert.Equal(side, reference.Side);
                Assert.Equal(path, reference.ResourcePath);
                Assert.Equal(15, reference.Poses.Length);

                foreach ((XRHandJoint joint, int index) in XRHandJoints.DestinationJoints.Select((joint, index) => (joint, index)))
                {
                    Quaternion pose = reference.Poses[index];
                    Assert.True(
                        float.IsFinite(pose.X) && float.IsFinite(pose.Y) && float.IsFinite(pose.Z) && float.IsFinite(pose.W),
                        $"{path} {side}/{joint} pose must be finite.");
                    Assert.True(
                        Math.Abs(pose.LengthSquared() - 1.0f) <= 0.001f,
                        $"{path} {side}/{joint} pose must be unit; got {pose.LengthSquared():R}.");
                }
            }
        }
    }

    /// <summary>Sampling reads only: every track, key count, key time, and key value is unchanged after sampling.</summary>
    [Headless]
    [Fact]
    public void Sampling_DoesNotMutateTheResource()
    {
        Animation grabBall = ResourceLoader.Load<Animation>(GrabBallAnimationPath)
            ?? throw new Xunit.Sdk.XunitException($"Could not load {GrabBallAnimationPath}.");
        string before = CaptureAnimationState(grabBall);

        Assert.True(AuthoredHandPoseReferenceSampler.TrySample(
            GrabBallAnimationPath,
            LimbSide.Left,
            out AuthoredHandPoseSideReference _,
            out string error), error);
        Assert.True(AuthoredHandPoseReferenceSampler.TrySample(
            GrabBallAnimationPath,
            LimbSide.Right,
            out AuthoredHandPoseSideReference _,
            out error), error);

        Assert.Equal(before, CaptureAnimationState(grabBall));
    }

    /// <summary>A missing required track fails closed naming the side, bone, resource, and rule.</summary>
    [Headless]
    [Fact]
    public void MissingTrack_FailsClosedWithIdentifiableRule()
    {
        Animation animation = BuildCompleteSyntheticAnimation();
        RemoveTrack(animation, TrackPathPrefix + "LeftMiddleDistal");

        AssertFails(animation, LimbSide.Left, "LeftMiddleDistal", "has no track matching it");
    }

    /// <summary>A duplicated enabled rotation track fails closed.</summary>
    [Headless]
    [Fact]
    public void DuplicateTrack_FailsClosedWithIdentifiableRule()
    {
        Animation animation = BuildCompleteSyntheticAnimation();
        DuplicateTrack(animation, TrackPathPrefix + "RightThumbMetacarpal");

        AssertFails(animation, LimbSide.Right, "RightThumbMetacarpal", "multiple enabled Rotation3D tracks");
    }

    /// <summary>A disabled rotation track at the required path fails closed with the disabled rule.</summary>
    [Headless]
    [Fact]
    public void DisabledTrack_FailsClosedWithIdentifiableRule()
    {
        Animation animation = BuildCompleteSyntheticAnimation();
        DisableTrack(animation, TrackPathPrefix + "LeftIndexProximal");

        AssertFails(animation, LimbSide.Left, "LeftIndexProximal", "only a disabled Rotation3D track");
    }

    /// <summary>A non-rotation track at the required path fails closed with the wrong-type rule.</summary>
    [Headless]
    [Fact]
    public void WrongTypeTrack_FailsClosedWithIdentifiableRule()
    {
        Animation animation = BuildCompleteSyntheticAnimation();
        ReplaceWithPositionTrack(animation, TrackPathPrefix + "RightLittleIntermediate");

        AssertFails(animation, LimbSide.Right, "RightLittleIntermediate", "non-Rotation3D track");
    }

    /// <summary>A wrong key count fails closed naming the count.</summary>
    [Headless]
    [Fact]
    public void WrongKeyCount_FailsClosedWithIdentifiableRule()
    {
        Animation animation = BuildCompleteSyntheticAnimation();
        int trackIndex = FindTrack(animation, TrackPathPrefix + "LeftRingProximal");
        _ = animation.TrackInsertKey(trackIndex, 0.02, new Quaternion(Vector3.Up, 0.1f));

        AssertFails(animation, LimbSide.Left, "LeftRingProximal", "holds 2 keys");
    }

    /// <summary>A key away from t=0 fails closed naming the time.</summary>
    [Headless]
    [Fact]
    public void WrongKeyTime_FailsClosedWithIdentifiableRule()
    {
        Animation animation = BuildCompleteSyntheticAnimation();
        int trackIndex = FindTrack(animation, TrackPathPrefix + "RightIndexDistal");
        animation.TrackRemoveKey(trackIndex, 0);
        _ = animation.TrackInsertKey(trackIndex, 0.5, new Quaternion(Vector3.Up, 0.1f));

        AssertFails(animation, LimbSide.Right, "RightIndexDistal", "t=0 is required");
    }

    /// <summary>A non-finite quaternion key fails closed with the quaternion rule.</summary>
    [Headless]
    [Fact]
    public void NonFiniteQuaternion_FailsClosedWithIdentifiableRule()
    {
        Animation animation = BuildCompleteSyntheticAnimation();
        int trackIndex = FindTrack(animation, TrackPathPrefix + "LeftThumbProximal");
        animation.TrackRemoveKey(trackIndex, 0);
        _ = animation.TrackInsertKey(
            trackIndex,
            0.0,
            new Quaternion(float.NaN, 0.0f, 0.0f, float.NaN));

        AssertFails(animation, LimbSide.Left, "LeftThumbProximal", "invalid quaternion");
    }

    /// <summary>A non-unit quaternion key fails the tolerance rule.</summary>
    [Headless]
    [Fact]
    public void NonUnitQuaternion_FailsClosedWithIdentifiableRule()
    {
        Animation animation = BuildCompleteSyntheticAnimation();
        int trackIndex = FindTrack(animation, TrackPathPrefix + "RightMiddleProximal");
        animation.TrackRemoveKey(trackIndex, 0);
        _ = animation.TrackInsertKey(trackIndex, 0.0, new Quaternion(0.0f, 0.5f, 0.0f, 0.5f));

        AssertFails(animation, LimbSide.Right, "RightMiddleProximal", "invalid quaternion");
    }

    /// <summary>An empty path fails closed on the resource contract.</summary>
    [Headless]
    [Fact]
    public void EmptyPath_FailsClosed()
    {
        Assert.False(AuthoredHandPoseReferenceSampler.TrySample("", LimbSide.Left, out _, out string error));
        Assert.Contains("path is empty", error, StringComparison.Ordinal);

        Assert.False(AuthoredHandPoseReferenceSampler.TrySample("   ", LimbSide.Right, out _, out error));
        Assert.Contains("path is empty", error, StringComparison.Ordinal);
    }

    /// <summary>A path resolving to a non-Animation resource fails closed naming the resolved type.</summary>
    [Headless]
    [Fact]
    public void NonAnimationResource_FailsClosed()
    {
        Assert.False(AuthoredHandPoseReferenceSampler.TrySample(
            "res://assets/xr/mock_runtime.tscn",
            LimbSide.Left,
            out _,
            out string error));
        Assert.Contains("rather than an Animation resource", error, StringComparison.Ordinal);
    }

    /// <summary>An unresolvable path fails closed.</summary>
    [Headless]
    [Fact]
    public void MissingResource_FailsClosed()
    {
        Assert.False(AuthoredHandPoseReferenceSampler.TrySample(
            "res://assets/characters/reference/female/animations/Does-Not-Exist.tres",
            LimbSide.Left,
            out _,
            out string error));
        Assert.Contains("could not be loaded", error, StringComparison.Ordinal);
    }

    private static void AssertFails(Animation animation, LimbSide side, string boneName, string expectedRule)
    {
        // The synthetic animation registers itself at a project path through the resource cache — no file is
        // written and no live animation node is involved (XR-002 TR52 direct-load contract).
        const string registeredPath = "res://.test_hand_pose_reference_sampler.tres";
        animation.TakeOverPath(registeredPath);

        try
        {
            Assert.False(AuthoredHandPoseReferenceSampler.TrySample(
                registeredPath,
                side,
                out AuthoredHandPoseSideReference _,
                out string error));
            Assert.Contains(boneName, error, StringComparison.Ordinal);
            Assert.Contains(expectedRule, error, StringComparison.Ordinal);
            Assert.Contains(side.ToString(), error, StringComparison.Ordinal);
            Assert.Contains(registeredPath, error, StringComparison.Ordinal);
        }
        finally
        {
            animation.TakeOverPath("");
        }
    }

    /// <summary>Snapshot of every track's type, enabled flag, path, key count, key times, and key values.</summary>
    private static string CaptureAnimationState(Animation animation)
    {
        System.Text.StringBuilder state = new();
        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
        {
            _ = state.Append(animation.TrackGetType(trackIndex))
                .Append('|')
                .Append(animation.TrackIsEnabled(trackIndex))
                .Append('|')
                .Append(animation.TrackGetPath(trackIndex))
                .Append('|')
                .Append(animation.TrackGetKeyCount(trackIndex));

            for (int keyIndex = 0; keyIndex < animation.TrackGetKeyCount(trackIndex); keyIndex++)
            {
                _ = state.Append('|')
                    .Append(animation.TrackGetKeyTime(trackIndex, keyIndex).ToString("R"))
                    .Append('|')
                    .Append(animation.TrackGetKeyValue(trackIndex, keyIndex).ToString());
            }

            _ = state.Append('\n');
        }

        return state.ToString();
    }

    private static Animation BuildCompleteSyntheticAnimation()
    {
        var animation = new Animation();
        foreach (LimbSide side in new[] { LimbSide.Left, LimbSide.Right })
        {
            foreach (XRHandJoint joint in XRHandJoints.DestinationJoints)
            {
                string path = TrackPathPrefix + AuthoredHandPoseSideReference.GetCanonicalBoneName(side, joint);
                int trackIndex = animation.AddTrack(Animation.TrackType.Rotation3D);
                animation.TrackSetPath(trackIndex, path);
                _ = animation.TrackInsertKey(trackIndex, 0.0, new Quaternion(Vector3.Right, 0.5f));
            }
        }

        return animation;
    }

    private static int FindTrack(Animation animation, string path)
    {
        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
        {
            if (animation.TrackGetPath(trackIndex).ToString() == path)
            {
                return trackIndex;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected synthetic animation to contain track '{path}'.");
    }

    private static void RemoveTrack(Animation animation, string path)
        => animation.RemoveTrack(FindTrack(animation, path));

    private static void DuplicateTrack(Animation animation, string path)
    {
        int duplicateIndex = animation.AddTrack(Animation.TrackType.Rotation3D);
        animation.TrackSetPath(duplicateIndex, path);
        _ = animation.TrackInsertKey(duplicateIndex, 0.0, new Quaternion(Vector3.Up, 0.4f));
    }

    private static void DisableTrack(Animation animation, string path)
        => animation.TrackSetEnabled(FindTrack(animation, path), false);

    private static void ReplaceWithPositionTrack(Animation animation, string path)
    {
        int trackIndex = FindTrack(animation, path);
        animation.RemoveTrack(trackIndex);
        int positionIndex = animation.AddTrack(Animation.TrackType.Position3D);
        animation.TrackSetPath(positionIndex, path);
        _ = animation.TrackInsertKey(positionIndex, 0.0, Vector3.Zero);
    }
}
