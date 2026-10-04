using AlleyCat.Control.Locomotion;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Control;

/// <summary>
/// Pure tests of the native-rate clip trajectory table: sample timing, linear interpolation, slice frames, motion
/// scale, and catalogue endpoint validation. Expected values are worked by hand from the synthetic samples.
/// </summary>
public sealed class ClipTrajectoryTableTests
{
    private const float Tolerance = 1e-5f;

    [Fact]
    public void ExpectedSampleCount_IncludesAShortFinalInterval()
    {
        Assert.Equal(31, ClipTrajectory.ExpectedSampleCount(1.0, 30.0));
        Assert.Equal(11, ClipTrajectory.ExpectedSampleCount(0.95, 10.0));
        // Float-authored durations a hair above a whole interval do not add an extra sample.
        Assert.Equal(39, ClipTrajectory.ExpectedSampleCount(1.2666667, 30.0));
        Assert.Equal(0.95, ClipTrajectory.SampleTimeOf(10, 0.95, 10.0), 12);
        Assert.Equal(0.5, ClipTrajectory.SampleTimeOf(5, 0.95, 10.0), 12);
    }

    [Fact]
    public void RootPlanarAt_InterpolatesLinearlyRelativeToFirstSampleAndAppliesMotionScale()
    {
        // Duration 0.25 s at 10 Hz: samples at 0, 0.1, 0.2 and a short final interval to 0.25.
        ClipTrajectory trajectory = Create(
            0.25,
            10.0,
            [new(1f, 2f), new(1f, 2.1f), new(1.2f, 2.3f), new(1.2f, 2.4f)],
            [0f, 0f, 0f, 0f],
            ballMotionScale: 0.5f);

        Assert.Equal(4, trajectory.SampleCount);
        AssertClose(Vector2.Zero, trajectory.RootPlanarAt(0d));
        // Halfway between samples 0 and 1: source (1, 2.05) - (1, 2) = (0, 0.05), scaled by 0.5.
        AssertClose(new Vector2(0f, 0.025f), trajectory.RootPlanarAt(0.05));
        // Halfway through the short final interval [0.2, 0.25]: (1.2, 2.35) - (1, 2) = (0.2, 0.35).
        AssertClose(new Vector2(0.1f, 0.175f), trajectory.RootPlanarAt(0.225));
        // Times clamp to the clip.
        AssertClose(new Vector2(0.1f, 0.2f), trajectory.RootPlanarAt(9d));
        AssertClose(Vector2.Zero, trajectory.RootPlanarAt(-1d));
    }

    [Fact]
    public void YawAt_IsRelativeToFirstSampleAndUnscaled()
    {
        ClipTrajectory trajectory = Create(
            0.2,
            10.0,
            [Vector2.Zero, Vector2.Zero, Vector2.Zero],
            [0.1f, 0.3f, 0.7f],
            ballMotionScale: 0.5f);

        Assert.Equal(0f, trajectory.YawAt(0d), Tolerance);
        Assert.Equal(0.1f, trajectory.YawAt(0.05), Tolerance);
        Assert.Equal(0.4f, trajectory.YawAt(0.15), Tolerance);
        Assert.Equal(0.6f, trajectory.Endpoint.Yaw, Tolerance);
    }

    [Fact]
    public void Slice_ExpressesDisplacementInTheRootFrameAtTheStartTime()
    {
        // At 0.1 s the root has turned 90° left; it then moves 1 m towards clip-start left (-right), which is the
        // turned root's forward.
        ClipTrajectory trajectory = Create(
            0.2,
            10.0,
            [Vector2.Zero, Vector2.Zero, new(-1f, 0f)],
            [0f, Mathf.Pi / 2f, Mathf.Pi / 2f]);

        ClipTrajectorySlice slice = trajectory.Slice(0.1, 0.2);

        AssertClose(new Vector2(0f, 1f), slice.Planar);
        Assert.Equal(0f, slice.Yaw, Tolerance);
        ClipTrajectorySlice whole = trajectory.Endpoint;
        AssertClose(new Vector2(-1f, 0f), whole.Planar);
        Assert.Equal(Mathf.Pi / 2f, whole.Yaw, Tolerance);
    }

    [Fact]
    public void RotateSemantic_PositiveYawTurnsForwardTowardsLeft()
    {
        AssertClose(new Vector2(-1f, 0f), ClipTrajectory.RotateSemantic(new Vector2(0f, 1f), Mathf.Pi / 2f));
        AssertClose(new Vector2(0f, 1f), ClipTrajectory.RotateSemantic(new Vector2(1f, 0f), Mathf.Pi / 2f));
    }

    [Fact]
    public void Ball_InterpolatesRuntimeSamplesWithoutRescaling()
    {
        ClipTrajectory trajectory = Create(
            0.1,
            10.0,
            [Vector2.Zero, Vector2.Zero],
            [0f, 0f],
            left: [new(0.1f, 0f, 0f), new(0.1f, 0.2f, -0.4f)],
            right: [new(-0.1f, 0f, 0f), new(-0.1f, 0f, 0f)],
            ballMotionScale: 2f);

        AssertClose(new Vector3(0.1f, 0.1f, -0.2f), trajectory.Ball(StandingLocomotionAnatomicalSide.Left, 0.05));
        AssertClose(new Vector3(-0.1f, 0f, 0f), trajectory.Ball(StandingLocomotionAnatomicalSide.Right, 0.05));
        Assert.Equal(new Vector3(0.1f, 0.2f, -0.4f), trajectory.BallSamples(StandingLocomotionAnatomicalSide.Left)[1]);
        Assert.Equal(2f, trajectory.BallMotionScale);
    }

    [Fact]
    public void Constructor_RequiresBallSamplesForBothFeet()
    {
        _ = Assert.Throws<ArgumentException>(() => Create(0.1, 10.0, [Vector2.Zero, Vector2.Zero], [0f, 0f], right: []));
        _ = Assert.Throws<ArgumentException>(() => new ClipTrajectory(
            "unit_identity",
            "unit_role",
            StandingLocomotionCapability.CyclicForwardPrimary,
            0.1,
            10.0,
            [Vector2.Zero, Vector2.Zero],
            [0f, 0f],
            default,
            [Vector3.Zero, Vector3.Zero],
            1f,
            new ClipTrajectoryErrorBounds(0.0001f, 0.0001f, 0.005f)));
        _ = Assert.Throws<ArgumentException>(() => Create(
            0.1,
            10.0,
            [Vector2.Zero, Vector2.Zero],
            [0f, 0f],
            left: [Vector3.Zero]));
    }

    [Fact]
    public void WithMotionScale_RebindsRootDataOnly()
    {
        ClipTrajectory trajectory = Create(
            0.1,
            10.0,
            [Vector2.Zero, new(0f, 1f)],
            [0f, 0.5f],
            left: [new(0.1f, 0f, 0f), new(0.1f, 0f, 0f)],
            ballMotionScale: 0.9f);

        ClipTrajectory rebound = trajectory.WithMotionScale(0.5f);

        Assert.Equal(0.5f, rebound.Endpoint.Planar.Y, Tolerance);
        Assert.Equal(0.5f, rebound.Endpoint.Yaw, Tolerance);
        AssertClose(new Vector3(0.1f, 0f, 0f), rebound.Ball(StandingLocomotionAnatomicalSide.Left, 0.05));
        Assert.Equal(0.9f, rebound.BallMotionScale);
    }

    [Fact]
    public void Table_BindsRootDataToItsMotionScale()
    {
        ClipTrajectory evaluated = Create(0.1, 10.0, [Vector2.Zero, new(0f, 1f)], [0f, 0f], ballMotionScale: 0.8372f);

        var table = new ClipTrajectoryTable(0.8372f, [evaluated]);

        Assert.Equal(0.8372f, table.MotionScale);
        Assert.Equal(0.8372f, table["unit_role"].Endpoint.Planar.Y, Tolerance);
        Assert.Same(table["unit_role"], table[StandingLocomotionCapability.CyclicForwardPrimary]);
        Assert.True(table.TryGetClip("unit_role", out ClipTrajectory? found));
        Assert.Equal(evaluated.SourceRootPlanar, found!.SourceRootPlanar);
    }

    [Fact]
    public void Table_RejectsBallDataEvaluatedAtAnotherMotionScale()
    {
        // Ball samples evaluated on the female rig cannot serve a table bound to the male rig's scale.
        ClipTrajectory female = Create(0.1, 10.0, [Vector2.Zero, new(0f, 1f)], [0f, 0f], ballMotionScale: 0.8372f);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => new ClipTrajectoryTable(0.9371f, [female]));

        Assert.Contains("ball samples", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorBounds_AreDeclaredPerClipAndRootBoundScales()
    {
        var bounds = new ClipTrajectoryErrorBounds(0.0001f, 0.0002f, 0.005f);
        ClipTrajectory trajectory = Create(0.1, 10.0, [Vector2.Zero, Vector2.Zero], [0f, 0f], ballMotionScale: 0.5f, bounds: bounds);

        Assert.Equal(bounds, trajectory.ErrorBounds);
        Assert.Equal(0.00005f, trajectory.RootPlanarErrorBound, 1e-9f);
        Assert.Equal(0.0001f, trajectory.WithMotionScale(1f).RootPlanarErrorBound, 1e-9f);
        _ = Assert.Throws<ArgumentException>(
            () => Create(0.1, 10.0, [Vector2.Zero, Vector2.Zero], [0f, 0f], bounds: new(-1f, 0f, 0f)));
        _ = Assert.Throws<ArgumentException>(
            () => Create(0.1, 10.0, [Vector2.Zero, Vector2.Zero], [0f, 0f], bounds: new(0f, float.NaN, 0f)));
    }

    [Fact]
    public void Table_RejectsDuplicateRolesAndInvalidScale()
    {
        ClipTrajectory source = Create(0.1, 10.0, [Vector2.Zero, Vector2.Zero], [0f, 0f]);

        _ = Assert.Throws<ArgumentException>(() => new ClipTrajectoryTable(1f, [source, source]));
        _ = Assert.Throws<ArgumentException>(() => new ClipTrajectoryTable(0f, [source]));
        _ = Assert.Throws<ArgumentException>(() => new ClipTrajectoryTable(float.NaN, [source]));
    }

    [Fact]
    public void Constructor_RejectsInconsistentSamples()
    {
        _ = Assert.Throws<ArgumentException>(() => Create(0.2, 10.0, [Vector2.Zero, Vector2.Zero], [0f, 0f]));
        _ = Assert.Throws<ArgumentException>(() => Create(0.1, 10.0, [Vector2.Zero, new(float.NaN, 0f)], [0f, 0f]));
        _ = Assert.Throws<ArgumentException>(() => Create(0.1, 0.0, [Vector2.Zero, Vector2.Zero], [0f, 0f]));
        _ = Assert.Throws<ArgumentException>(
            () => Create(0.1, 10.0, [Vector2.Zero, Vector2.Zero], [0f, 0f]).WithMotionScale(-1f));
        _ = Assert.Throws<ArgumentException>(
            () => Create(0.1, 10.0, [Vector2.Zero, Vector2.Zero], [0f, 0f], ballMotionScale: 0f));
    }

    [Fact]
    public void ValidateAgainst_AcceptsCatalogueEndpointInSourceUnitsAtAnyMotionScale()
    {
        // Source forward travel 1.326 m; the catalogue states it in actor-local Godot axes (-Z forward).
        ClipTrajectory source = Create(0.1, 10.0, [Vector2.Zero, new(0.2f, 1.326f)], [0f, 0.5f]);
        var table = new ClipTrajectoryTable(1f, [source]);

        table.ValidateAgainst([Clip(new Vector3(0.2f, 0f, -1.326f), 0.5f)]);
        Assert.Equal(1.326f, table["unit_role"].Endpoint.Planar.Y, Tolerance);
        // Validation compares source units, so a table re-bound to another scale still validates.
        new ClipTrajectoryTable(0.8372f, [Create(0.1, 10.0, [Vector2.Zero, new(0.2f, 1.326f)], [0f, 0.5f], ballMotionScale: 0.8372f)])
            .ValidateAgainst([Clip(new Vector3(0.2f, 0f, -1.326f), 0.5f)]);
    }

    [Fact]
    public void ValidateAgainst_RejectsEndpointBeyondTolerance()
    {
        ClipTrajectory source = Create(0.1, 10.0, [Vector2.Zero, new(0f, 1f)], [0f, 0f], ballMotionScale: 0.9371f);
        var table = new ClipTrajectoryTable(0.9371f, [source]);

        // 0.2 mm planar mismatch (above the 0.1 mm bound).
        _ = Assert.Throws<InvalidDataException>(() => table.ValidateAgainst([Clip(new Vector3(0f, 0f, -1.0002f), 0f)]));
        // 0.02° yaw mismatch (above the 0.01° bound).
        _ = Assert.Throws<InvalidDataException>(
            () => table.ValidateAgainst([Clip(new Vector3(0f, 0f, -1f), Mathf.DegToRad(0.02f))]));
        // 0.05 mm is within bound.
        table.ValidateAgainst([Clip(new Vector3(0f, 0f, -1.00005f), 0f)]);
    }

    [Fact]
    public void ValidateAgainst_RejectsMissingOrExtraClips()
    {
        ClipTrajectory source = Create(0.1, 10.0, [Vector2.Zero, new(0f, 1f)], [0f, 0f]);
        var table = new ClipTrajectoryTable(1f, [source]);

        _ = Assert.Throws<InvalidDataException>(
            () => table.ValidateAgainst([Clip(new Vector3(0f, 0f, -1f), 0f) with { Role = "other_role" }]));
        _ = Assert.Throws<InvalidDataException>(() => table.ValidateAgainst([]));
        _ = Assert.Throws<InvalidDataException>(
            () => table.ValidateAgainst([Clip(new Vector3(0f, 0f, -1f), 0f) with { DurationSeconds = 0.2 }]));
    }

    private static ClipTrajectory Create(
        double duration,
        double rate,
        Vector2[] planar,
        float[] yaw,
        Vector3[]? left = null,
        Vector3[]? right = null,
        float ballMotionScale = 1f,
        ClipTrajectoryErrorBounds? bounds = null)
        => new(
            "unit_identity",
            "unit_role",
            StandingLocomotionCapability.CyclicForwardPrimary,
            duration,
            rate,
            [.. planar],
            [.. yaw],
            [.. left ?? new Vector3[planar.Length]],
            [.. right ?? new Vector3[planar.Length]],
            ballMotionScale,
            bounds ?? new ClipTrajectoryErrorBounds(0.0001f, 0.0001f, 0.005f));

    private static StandingLocomotionClip Clip(Vector3 endpoint, float yaw)
        => new(
            "unit_identity",
            "unit_role",
            StandingLocomotionBoundaryKind.CyclicTravel,
            true,
            0.1,
            [],
            [],
            endpoint,
            yaw,
            [])
        {
            Capability = StandingLocomotionCapability.CyclicForwardPrimary,
        };

    private static void AssertClose(Vector2 expected, Vector2 actual)
        => Assert.True(expected.DistanceTo(actual) <= Tolerance, $"expected {expected}, actual {actual}");

    private static void AssertClose(Vector3 expected, Vector3 actual)
        => Assert.True(expected.DistanceTo(actual) <= Tolerance, $"expected {expected}, actual {actual}");
}
