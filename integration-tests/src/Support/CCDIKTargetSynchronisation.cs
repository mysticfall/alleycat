using Godot;

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Provides a signal-time boundary for sampling a CCDIK pose after assigning its target, driven by a
/// deterministic synthetic solver step instead of real frame pacing.
/// </summary>
internal static class CCDIKTargetSynchronisation
{
    private const float TargetPositionToleranceMetres = 0.0001f;

    /// <summary>
    /// Number of process frames pumped after each synthetic advance so the skeleton's deferred
    /// modification update reliably consumes the accumulated synthetic delta.
    /// </summary>
    private const int FramePumpsPerSyntheticStep = 2;

    /// <summary>
    /// Synthetic solver step duration (one 60 Hz frame pass) used to drive <see cref="SkeletonModifier3D"/>
    /// processing through <see cref="Skeleton3D.Advance"/>. The engine CCDIK solve runs once per consumed
    /// advance regardless of the delta value, so a fixed synthetic delta makes the number of solve passes
    /// between two samples deterministic and independent of host frame timing.
    /// </summary>
    public const double SyntheticSolveDeltaSeconds = 1.0 / 60.0;

    /// <summary>
    /// Assigns a target, forces its pending transform propagation, drives one synthetic solver step, and
    /// proves the CCDIK completion observed that exact target assignment before the caller samples the
    /// final <see cref="Skeleton3D"/> pose.
    /// </summary>
    public static async Task AssignTargetAndWaitForCurrentCCDIKAsync(
        SceneTree sceneTree,
        SkeletonModifier3D ccdik,
        Node3D target,
        Node3D marker)
    {
        Transform3D assignedTargetTransform = marker.GlobalTransform;
        target.GlobalTransform = assignedTargetTransform;
        target.ForceUpdateTransform();

        Transform3D? targetTransformAtModificationProcessed = null;
        void RecordCCDIKOutput()
        {
            targetTransformAtModificationProcessed = target.GlobalTransform;
        }

        ccdik.ModificationProcessed += RecordCCDIKOutput;
        try
        {
            ccdik.GetSkeleton()?.Advance(SyntheticSolveDeltaSeconds);
            await TestUtils.WaitForFramesAsync(sceneTree, FramePumpsPerSyntheticStep);
        }
        finally
        {
            ccdik.ModificationProcessed -= RecordCCDIKOutput;
        }

        Transform3D observedTargetTransform = targetTransformAtModificationProcessed
            ?? throw new Xunit.Sdk.XunitException(
                "CCDIK did not emit ModificationProcessed for the assigned target.");
        AssertSignalTimeTargetMatchesAssignment(assignedTargetTransform, observedTargetTransform, marker.Name);
    }

    private static void AssertSignalTimeTargetMatchesAssignment(
        Transform3D expected,
        Transform3D actual,
        StringName markerName)
    {
        float positionError = expected.Origin.DistanceTo(actual.Origin);
        if (positionError > TargetPositionToleranceMetres ||
            !expected.Basis.Orthonormalized().IsEqualApprox(actual.Basis.Orthonormalized()))
        {
            throw new Xunit.Sdk.XunitException(
                $"CCDIK ModificationProcessed observed a stale HeadTarget transform for marker '{markerName}'. " +
                $"Expected origin {expected.Origin}, observed {actual.Origin}, position error {positionError:F6} m.");
        }
    }
}
