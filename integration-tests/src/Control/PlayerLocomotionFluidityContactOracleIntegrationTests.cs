using AlleyCat.IntegrationTests.Support;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Control;

/// <summary>Mutation coverage for the fail-closed CTRL-001 contact evidence oracle.</summary>
public sealed class PlayerLocomotionFluidityContactOracleIntegrationTests
{
    [Headless]
    [Fact]
    public void ContactOracle_CleanAuthoritativeSupport_Passes()
    {
        FluidityGateResult result = Evaluate(CreateCleanSamples(), CreateIntervals());

        Assert.True(result.Passed, result.BuildReport());
        Assert.Empty(result.RejectedSamples);
    }

    [Headless]
    [Fact]
    public void ContactOracle_Mutations_FailClosedAndRetainReasons()
    {
        AssertRejected(Evaluate(CreateCleanSamples(), [new FluidityExpectedSupportInterval("left", 1f, 2f, 7)]), "zero samples");
        AssertRejected(Evaluate(CreateCleanSamples(), []), "applicable expected support population was empty");

        AssertRejected(Evaluate(MutateFrames(_ => null), CreateIntervals()), "missing authoritative contact frame");
        AssertRejected(Evaluate(MutateLeft(_ => null), CreateIntervals()), "missing authoritative contact evidence");

        AssertRejected(Evaluate(MutateLeft(evidence => evidence with
        {
            AnchorWorld = new Vector3(float.NaN, 0f, 0f),
        }), CreateIntervals()), "non-finite anchor");

        AssertRejected(Evaluate(MutateLeft(evidence => evidence with
        {
            Confidence = FluidityContactConfidence.Low,
        }), CreateIntervals()), "non-authoritative confidence");

        AssertRejected(Evaluate(MutateLeft(evidence => evidence with
        {
            Confidence = FluidityContactConfidence.Unknown,
        }), CreateIntervals()), "non-authoritative confidence");

        AssertRejected(Evaluate(MutateLeft(evidence => evidence with
        {
            SourceGeneration = 6,
        }), CreateIntervals()), "stale contact generation");

        AssertRejected(Evaluate(MutateLeft(evidence => evidence with
        {
            Foot = "right",
        }), CreateIntervals()), "wrong-foot label");

        AssertRejected(Evaluate(MutateLeft(evidence => evidence with
        {
            PostIK = evidence.PostIK! with
            {
                FootWorld = new Vector3(0.2f, 0f, 0f),
            },
        }), CreateIntervals()), "translated planted foot");

        AssertRejected(Evaluate(MutateLeft(evidence => evidence with
        {
            Phase = FluidityContactPhase.Release,
        }), CreateIntervals()), "release before required full support");
    }

    [Headless]
    [Fact]
    public void ContactOracle_PreAndPostIKEvidence_MustBeIndependentlyAvailableValidAndProvenanced()
    {
        AssertRejected(Evaluate(MutateLeft(evidence => evidence with { PreIK = null }), CreateIntervals()), "missing pre-IK contact evidence");
        AssertRejected(Evaluate(MutateLeft(evidence => evidence with { PreIK = evidence.PreIK! with { IsAvailable = false } }), CreateIntervals()), "unavailable pre-IK contact evidence");
        AssertRejected(Evaluate(MutateLeft(evidence => evidence with { PreIK = evidence.PreIK! with { IsValid = false } }), CreateIntervals()), "invalid pre-IK contact evidence");
        AssertRejected(Evaluate(MutateLeft(evidence => evidence with { PreIK = evidence.PreIK! with { SourceGeneration = 6 } }), CreateIntervals()), "stale pre-IK contact evidence");
        AssertRejected(Evaluate(MutateLeft(evidence => evidence with { PreIK = evidence.PreIK! with { FootWorld = new Vector3(float.NaN, 0f, 0f) } }), CreateIntervals()), "non-finite pre-IK foot position");

        AssertRejected(Evaluate(MutateLeft(evidence => evidence with { PostIK = null }), CreateIntervals()), "missing post-IK contact evidence");
        AssertRejected(Evaluate(MutateLeft(evidence => evidence with { PostIK = evidence.PostIK! with { IsAvailable = false } }), CreateIntervals()), "unavailable post-IK contact evidence");
        AssertRejected(Evaluate(MutateLeft(evidence => evidence with { PostIK = evidence.PostIK! with { IsValid = false } }), CreateIntervals()), "invalid post-IK contact evidence");
        AssertRejected(Evaluate(MutateLeft(evidence => evidence with { PostIK = evidence.PostIK! with { SourceGeneration = 6 } }), CreateIntervals()), "stale post-IK contact evidence");
        AssertRejected(Evaluate(MutateLeft(evidence => evidence with { PostIK = evidence.PostIK! with { FootWorld = new Vector3(float.NaN, 0f, 0f) } }), CreateIntervals()), "non-finite post-IK foot position");
    }

    [Headless]
    [Fact]
    public void ContactOracle_ExplicitInapplicability_AcceptsNoContactFramesWithoutClaimingSupport()
    {
        FluidityContactValidation result = FluidityContactOracle.Validate(
            CreateCleanSamples().Select(sample => sample with { Contacts = null }).ToList(),
            static sample => sample.TimeSeconds,
            static sample => sample.Contacts,
            new FluidityExpectedSupportPopulation(FluiditySupportApplicability.Inapplicable, null));

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.LeftFullSupport, supported => supported);
        Assert.DoesNotContain(result.RightFullSupport, supported => supported);

        FluidityContactValidation invalidResult = FluidityContactOracle.Validate(
            CreateCleanSamples(),
            static sample => sample.TimeSeconds,
            static sample => sample.Contacts,
            new FluidityExpectedSupportPopulation(FluiditySupportApplicability.Inapplicable, CreateIntervals()));

        Assert.False(invalidResult.IsValid);
        Assert.Contains(invalidResult.Rejections, rejection => rejection.Reason.Contains("inapplicable expected support population", StringComparison.Ordinal));
    }

    [Headless]
    [Fact]
    public void ContactOracle_TranslatedSupport_IsStillMeasuredBeforeThresholdFailure()
    {
        List<FluiditySample> samples = CreateCleanSamples();
        for (int index = 1; index < samples.Count; index++)
        {
            FluidityContactFrame frame = Assert.IsType<FluidityContactFrame>(samples[index].Contacts, exactMatch: false);
            Vector3 translated = new(index * 0.3f, 0f, 0f);
            samples[index] = samples[index] with
            {
                LeftFootWorld = translated,
                Contacts = frame with
                {
                    Left = (frame.Left! with
                    {
                        PostIK = frame.Left.PostIK! with
                        {
                            FootWorld = translated
                        },
                    })
                },
            };
        }

        FluidityGateResult result = Evaluate(
            samples,
            [new FluidityExpectedSupportInterval("left", 0.1f, 0.2f, 7), new FluidityExpectedSupportInterval("right", 0f, 0.2f, 7)]);

        Assert.False(result.Passed, result.BuildReport());
        Assert.Contains(result.Violations, violation => violation.Metric == FluidityMetrics.StanceSlipSpeed);
        Assert.Equal(1, result.Statistics.LeftFoot.StanceIntervalCount);
        Assert.Contains(result.RejectedSamples, rejection => rejection.Reason.Contains("translated planted foot", StringComparison.Ordinal));
    }

    private static FluidityGateResult Evaluate(
        IReadOnlyList<FluiditySample> samples,
        IReadOnlyList<FluidityExpectedSupportInterval> intervals)
        => PlayerLocomotionFluidityGate.Evaluate(
            "synthetic-player-contact",
            samples,
            [new FluidityNodeSegment("PlayerTravel", 0f, 0.2f)],
            FluidityGateThresholds.CalibratedDefaults,
            new FluidityExpectedSupportPopulation(FluiditySupportApplicability.Required, intervals));

    private static List<FluiditySample> CreateCleanSamples()
    {
        List<FluiditySample> samples = [];
        for (int index = 0; index < 3; index++)
        {
            samples.Add(new FluiditySample(
                index * 0.1f,
                "PlayerTravel",
                Vector3.Zero,
                new Vector3(1f, 0f, 0f),
                Vector3.Zero,
                new FluidityContactFrame(CreateEvidence("left", Vector3.Zero), CreateEvidence("right", new Vector3(1f, 0f, 0f)))));
        }

        return samples;
    }

    private static List<FluiditySample> MutateFrames(Func<FluidityContactFrame?, FluidityContactFrame?> mutate)
    {
        List<FluiditySample> samples = CreateCleanSamples();
        for (int index = 0; index < samples.Count; index++)
        {
            samples[index] = samples[index] with
            {
                Contacts = mutate(samples[index].Contacts)
            };
        }

        return samples;
    }

    private static List<FluiditySample> MutateLeft(Func<FluidityFootContactEvidence, FluidityFootContactEvidence?> mutate)
    {
        List<FluiditySample> samples = CreateCleanSamples();
        for (int index = 0; index < samples.Count; index++)
        {
            FluidityContactFrame frame = Assert.IsType<FluidityContactFrame>(samples[index].Contacts, exactMatch: false);
            samples[index] = samples[index] with
            {
                Contacts = frame with
                {
                    Left = mutate(frame.Left!)
                }
            };
        }

        return samples;
    }

    private static IReadOnlyList<FluidityExpectedSupportInterval> CreateIntervals()
        => [new FluidityExpectedSupportInterval("left", 0f, 0.2f, 7), new FluidityExpectedSupportInterval("right", 0f, 0.2f, 7)];

    private static FluidityFootContactEvidence CreateEvidence(string foot, Vector3 anchor)
        => new(
            foot,
            FluidityContactPhase.FullSupport,
            FluidityContactConfidence.Authoritative,
            7,
            7,
            anchor,
            new FluidityContactStageEvidence(true, true, 7, anchor),
            new FluidityContactStageEvidence(true, true, 7, anchor));

    private static void AssertRejected(FluidityGateResult result, string reason)
    {
        Assert.False(result.Passed, result.BuildReport());
        Assert.Contains(result.RejectedSamples, rejection => rejection.Reason.Contains(reason, StringComparison.Ordinal));
    }
}
