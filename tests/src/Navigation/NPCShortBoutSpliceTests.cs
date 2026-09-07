using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCShortBoutSpliceTests
{
    [Fact]
    public void CorrectionEnvelope_IsZeroOutsideCentralBlendAndBoundedWithinIt()
    {
        NPCShortBoutSplicePlan plan = new(
            new StandingLocomotionShortBoutPair("i2f_r", "release_r", "f2i_l", "release_l"),
            0.3f,
            0.1f,
            0.12f,
            new Vector2(0f, 1f),
            0f,
            1f,
            0.2f);

        Assert.Equal(0f, NPCShortBoutSplice.CorrectionEnvelope(0f, plan));
        Assert.Equal(0f, NPCShortBoutSplice.CorrectionEnvelope(0.18f, plan));
        Assert.Equal(0f, NPCShortBoutSplice.CorrectionEnvelope(0.3f, plan));
        Assert.InRange(NPCShortBoutSplice.CorrectionEnvelope(0.24f, plan), 0.99f, 1f);
        for (float time = 0.18f; time <= 0.3f; time += 0.002f)
        {
            Assert.InRange(NPCShortBoutSplice.CorrectionEnvelope(time, plan), 0f, 1f);
        }
    }

    [Fact]
    public void EffectivelyZeroDistance_IsNeverEligible()
    {
        Assert.False(NPCShortBoutSplice.TryCreate(
            catalogue: null!,
            entryRole: "i2f_r",
            directLength: NPCShortBoutSplice.EffectiveZeroDistance,
            configuration: new NPCShortBoutSpliceConfiguration(),
            plan: out _));
    }

    [Fact]
    public void MostlyLateralRequests_AreDirectionIneligibleDespiteMatchingScalarLength()
    {
        // The i2f/i2b splice forecast is ±Y semantic, so a mostly-lateral
        // request is never spliceable — a 90° scalar-length match would demand an out-of-envelope world correction
        // and invalidate into a silent destination clear. The declared fixture offset must be rejected even though
        // its scalar length fits the splice's scalar band.
        Assert.False(NPCShortBoutSplice.IsDirectionEligible(new Vector2(-0.48f, 0.03f)));
        Assert.False(NPCShortBoutSplice.IsDirectionEligible(new Vector2(0.55f, -0.02f)));
        Assert.False(NPCShortBoutSplice.IsDirectionEligible(new Vector2(0.3f, -0.1f)));

        Assert.True(NPCShortBoutSplice.IsDirectionEligible(new Vector2(-0.08f, -0.46f)));
        Assert.True(NPCShortBoutSplice.IsDirectionEligible(new Vector2(0.05f, 0.4f)));
        Assert.True(NPCShortBoutSplice.IsDirectionEligible(Vector2.Zero));
        Assert.False(NPCShortBoutSplice.IsDirectionEligible(new Vector2(float.NaN, 0.5f)));
    }

    [Fact]
    public void FullCrossfadeOverlap_RejectsEitherInsufficientSourceWindow()
    {
        Assert.True(NPCShortBoutSplice.CanRetainCrossfadeOverlap(0.3f, 0.4f, 1f, 0.12f));
        Assert.False(NPCShortBoutSplice.CanRetainCrossfadeOverlap(0.1f, 0.4f, 1f, 0.12f));
        Assert.False(NPCShortBoutSplice.CanRetainCrossfadeOverlap(0.3f, 0.9f, 1f, 0.12f));
    }

    [Fact]
    public void Forecast_IntegratesAdvancingEntryAndExitDuringLinearCrossfade()
    {
        StandingLocomotionClip entry = LinearClip("i2f_l", 1f, 0.2f);
        StandingLocomotionClip exit = LinearClip("f2i_r", 1f, 0.4f);

        NPCShortBoutRootForecast forecast = NPCShortBoutSplice.Forecast(entry, exit, 0.5f, 0.25f, 0.25f);

        Assert.Equal(new Vector2(0f, 1f), forecast.LocalPlanar, new Vector2Comparer(0.0001f));
        Assert.Equal(0.325f, forecast.Yaw, 4);
    }

    private static StandingLocomotionClip LinearClip(string role, float distance, float yaw)
        => new(
            $"ember_{role}",
            role,
            StandingLocomotionBoundaryKind.Transition,
            false,
            1d,
            [],
            [],
            new Vector3(0f, 0f, -distance),
            yaw,
            [
                new StandingLocomotionResponseSample(0d, Vector2.Zero, 0f, new Vector2(0f, distance), yaw),
                new StandingLocomotionResponseSample(1d, new Vector2(0f, distance), yaw, new Vector2(0f, distance), yaw),
            ]);

    private sealed class Vector2Comparer(float tolerance) : IEqualityComparer<Vector2>
    {
        public bool Equals(Vector2 x, Vector2 y) => x.DistanceTo(y) <= tolerance;
        public int GetHashCode(Vector2 obj) => obj.GetHashCode();
    }

}
