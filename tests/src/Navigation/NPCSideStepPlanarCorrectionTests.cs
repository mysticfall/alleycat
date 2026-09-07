using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCSideStepPlanarCorrectionTests
{
    private const float Delta = 1f / 60f;

    [Fact]
    public void CumulativeFraction_HasExactBoundaryValues()
    {
        Assert.Equal(0f, NPCSideStepPlanarCorrection.CumulativeFraction(0f), 7);
        Assert.Equal(1f, NPCSideStepPlanarCorrection.CumulativeFraction(1f), 7);
        Assert.Equal(0f, NPCSideStepPlanarCorrection.CumulativeFraction(-0.5f), 7);
        Assert.Equal(1f, NPCSideStepPlanarCorrection.CumulativeFraction(1.5f), 7);
        Assert.Equal(0f, NPCSideStepPlanarCorrection.CumulativeFraction(float.NaN), 7);
    }

    [Fact]
    public void CumulativeFraction_IsMonotoneAndFrontLoaded()
    {
        float previous = NPCSideStepPlanarCorrection.CumulativeFraction(0f);
        for (int step = 1; step <= 100; step++)
        {
            float current = NPCSideStepPlanarCorrection.CumulativeFraction(step / 100f);
            Assert.True(current >= previous, $"Profile decreased at phase {step / 100f}.");
            previous = current;
        }

        // Front-loaded: more than 40% of the correction lands in the first quarter of the clip.
        Assert.True(
            NPCSideStepPlanarCorrection.CumulativeFraction(0.25f) > 0.4f,
            "The profile must front-load its correction instead of spreading it uniformly.");
        Assert.Equal(0.4375f, NPCSideStepPlanarCorrection.CumulativeFraction(0.25f), 6);
    }

    [Fact]
    public void CumulativeFraction_EndsWithZeroSlope()
    {
        // f(1) - f(1 - h) = h² exactly for f(t) = 1 - (1 - t)²: the end slope vanishes so the synthetic velocity
        // tapers out as the clip finishes instead of snapping to a stop.
        const float step = 1f / 50f;
        float endStep = NPCSideStepPlanarCorrection.CumulativeFraction(1f)
            - NPCSideStepPlanarCorrection.CumulativeFraction(1f - step);
        Assert.Equal(step * step, endStep, 7);
        Assert.True(endStep < 0.001f);
        // Compare against the start step (2h - h²): the profile decelerates from front-load to zero end-slope.
        float startStep = NPCSideStepPlanarCorrection.CumulativeFraction(step)
            - NPCSideStepPlanarCorrection.CumulativeFraction(0f);
        Assert.True(startStep > 20f * endStep);
    }

    [Fact]
    public void ResolveVelocity_IsBoundedByTheMaximumRate()
    {
        // A large remaining total demands more than the ceiling early in the clip.
        Vector2 velocity = NPCSideStepPlanarCorrection.ResolveVelocity(
            new Vector2(2f, 0f),
            Vector2.Zero,
            0.5f,
            2f,
            Delta);
        Assert.Equal(NPCSideStepPlanarCorrection.MaximumRate, velocity.Length(), 6);
        Assert.Equal(0.5f, NPCSideStepPlanarCorrection.MaximumRate, 6);
    }

    [Fact]
    public void ResolveVelocity_CumulativeNeverExceedsTheSnapshotMagnitude()
    {
        Vector2 total = new(0.3f, -0.15f);
        float snapshot = 0.2f;
        Vector2 published = Vector2.Zero;
        for (int frame = 0; frame < 120; frame++)
        {
            Vector2 velocity = NPCSideStepPlanarCorrection.ResolveVelocity(
                total,
                published,
                (frame + 1) / 120f,
                snapshot,
                Delta);
            published += velocity * Delta;
            Assert.True(
                published.Length() <= snapshot + 0.000001f,
                $"Frame {frame}: applied correction {published.Length():F7} exceeded the snapshot {snapshot}.");
        }
    }

    [Fact]
    public void ResolveVelocity_ConvergesOnTheRemainingTotalWithLiveFeedback()
    {
        // Anchored live-feedback model: the recomputed total tracks forecast drift only — the published
        // accumulator carries the applied correction — so the envelope must land the full residual by phase one.
        Vector2 total = new(-0.12f, 0.21f);
        float snapshot = total.Length();
        Vector2 published = Vector2.Zero;
        const int frames = 50;
        for (int frame = 0; frame < frames; frame++)
        {
            Vector2 velocity = NPCSideStepPlanarCorrection.ResolveVelocity(
                total,
                published,
                (frame + 1) / (float)frames,
                snapshot,
                Delta);
            published += velocity * Delta;
        }

        Assert.True(
            (total - published).Length() <= NPCAnimationProgramme.NumericalConvergenceThreshold,
            $"Envelope left {(total - published).Length():F7} m un-applied at clip end: total={total}, published={published}.");
    }

    [Fact]
    public void ResolveVelocity_AbsorbsForecastDriftBeforeTheClipEnds()
    {
        // Live feedback: the forecast under-delivers mid-clip, the recomputed total grows, and the envelope still
        // finishes on the enlarged destination within the (already larger) committed snapshot.
        Vector2 total = new(0.2f, 0f);
        Vector2 published = Vector2.Zero;
        const int frames = 50;
        for (int frame = 0; frame < frames; frame++)
        {
            if (frame == 25)
            {
                total = new(0.26f, 0.02f);
            }

            Vector2 velocity = NPCSideStepPlanarCorrection.ResolveVelocity(
                total,
                published,
                (frame + 1) / (float)frames,
                0.27f,
                Delta);
            published += velocity * Delta;
        }

        Assert.True(
            (total - published).Length() <= 0.002f,
            $"Envelope failed to absorb mid-clip drift: total={total}, published={published}.");
    }

    [Fact]
    public void ResolveVelocity_StopsExactlyOnceTheUnappliedEnvelopeReachesNumericalTolerance()
    {
        Vector2 velocity = NPCSideStepPlanarCorrection.ResolveVelocity(
            new Vector2(0.00005f, 0f),
            Vector2.Zero,
            0.5f,
            0.3f,
            Delta);
        Assert.Equal(Vector2.Zero, velocity);

        velocity = NPCSideStepPlanarCorrection.ResolveVelocity(
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 0f),
            0.5f,
            0.5f,
            Delta);
        Assert.Equal(Vector2.Zero, velocity);
    }

    [Fact]
    public void ResolveVelocity_RejectsDegenerateInputs()
    {
        Assert.Equal(
            Vector2.Zero,
            NPCSideStepPlanarCorrection.ResolveVelocity(new Vector2(1f, 0f), Vector2.Zero, 0.5f, 1f, 0f));
        Assert.Equal(
            Vector2.Zero,
            NPCSideStepPlanarCorrection.ResolveVelocity(new Vector2(float.NaN, 0f), Vector2.Zero, 0.5f, 1f, Delta));
        Assert.Equal(
            Vector2.Zero,
            NPCSideStepPlanarCorrection.ResolveVelocity(new Vector2(1f, 0f), Vector2.Zero, 0.5f, float.NaN, Delta));
        Assert.Equal(
            Vector2.Zero,
            NPCSideStepPlanarCorrection.ResolveVelocity(new Vector2(1f, 0f), Vector2.Zero, 0.5f, 0f, Delta));
    }

    [Fact]
    public void LimitCumulative_PreservesDirectionAndBoundsMagnitude()
    {
        Vector2 limited = NPCSideStepPlanarCorrection.LimitCumulative(new Vector2(3f, 4f), 0.5f);
        Assert.Equal(0.5f, limited.Length(), 7);
        Assert.Equal(new Vector2(0.3f, 0.4f), limited);

        Vector2 inside = NPCSideStepPlanarCorrection.LimitCumulative(new Vector2(0.1f, 0f), 0.5f);
        Assert.Equal(new Vector2(0.1f, 0f), inside);

        Assert.Equal(Vector2.Zero, NPCSideStepPlanarCorrection.LimitCumulative(new Vector2(1f, 1f), 0f));
        Assert.Equal(Vector2.Zero, NPCSideStepPlanarCorrection.LimitCumulative(new Vector2(float.NaN, 0f), 1f));
    }
}
