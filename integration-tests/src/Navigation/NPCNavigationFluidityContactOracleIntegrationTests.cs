using AlleyCat.IntegrationTests.Support;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>Mutation coverage for the fail-closed NAV-001 contact evidence oracle.</summary>
public sealed class NPCNavigationFluidityContactOracleIntegrationTests
{
    [Headless]
    [Fact]
    public void ContactOracle_CleanAuthoritativeSupport_PassesGate()
    {
        NPCFluidityGateResult result = Evaluate(CreateCleanSamples(), CreateIntervals());

        Assert.True(result.Passed, result.BuildReport());
        Assert.Empty(result.RejectedSamples);
    }

    [Headless]
    [Fact]
    public void ContactOracle_Mutations_FailClosedAndRetainReasons()
    {
        AssertRejected(Evaluate(CreateCleanSamples(), []), "applicable expected support population was empty");
        AssertRejected(Evaluate(MutateFrames(_ => null), CreateIntervals()), "missing authoritative contact frame");
        AssertRejected(Evaluate(MutateLeft(_ => null), CreateIntervals()), "missing authoritative contact evidence");
        AssertRejected(Evaluate(MutateLeft(evidence => evidence with { Foot = "right" }), CreateIntervals()), "wrong-foot label");
        AssertRejected(Evaluate(MutateLeft(evidence => evidence with { SourceGeneration = 6 }), CreateIntervals()), "stale contact generation");
        AssertRejected(Evaluate(MutateLeft(evidence => evidence with { Confidence = FluidityContactConfidence.Low }), CreateIntervals()), "non-authoritative confidence");
        AssertRejected(Evaluate(MutateLeft(evidence => evidence with { AnchorWorld = new Vector3(float.NaN, 0f, 0f) }), CreateIntervals()), "non-finite anchor");
        AssertRejected(Evaluate(MutateLeft(evidence => evidence with
        {
            PostIK = evidence.PostIK! with
            {
                FootWorld = new Vector3(0.2f, 0f, 0f)
            },
        }), CreateIntervals()), "translated planted foot");
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
    public void ContactOracle_HistoricalReferenceLimitExceedance_IsDiagnosticOnly()
    {
        List<NPCFluiditySample> samples = CreateCleanSamples();
        samples[1] = samples[1] with
        {
            RootWorld = new Vector3(0.5f, 0f, 0f),
        };
        samples[2] = samples[2] with
        {
            RootWorld = new Vector3(1f, 0f, 0f),
        };

        NPCFluidityGateResult result = Evaluate(samples, CreateIntervals());

        Assert.True(
            result.Statistics.SpecView.MaximumRootSpeedStepMetresPerSecond
                > NPCFluiditySpecView.Limits.MaximumRootSpeedStepMetresPerSecond);
        Assert.True(result.Passed, result.BuildReport());
        Assert.Empty(result.Violations);
        Assert.Contains("Historical NAV-001 reference-limit diagnostic view:", result.BuildReport(), StringComparison.Ordinal);
    }

    private static NPCFluidityGateResult Evaluate(
        IReadOnlyList<NPCFluiditySample> samples,
        IReadOnlyList<FluidityExpectedSupportInterval> intervals)
        => NPCNavigationFluidityGate.Evaluate(
            "synthetic-npc-contact",
            samples,
            [new NPCFluidityNodeSegment("NPCTravel", 0f, 0.2f)],
            NPCFluidityGateThresholds.CalibratedDefaults,
            new FluidityExpectedSupportPopulation(FluiditySupportApplicability.Required, intervals));

    private static List<NPCFluiditySample> CreateCleanSamples()
    {
        List<NPCFluiditySample> samples = [];
        for (int index = 0; index < 3; index++)
        {
            samples.Add(new NPCFluiditySample(
                index * 0.1f,
                "NPCTravel",
                "Synthetic",
                0f,
                Vector2.Zero,
                1f,
                Vector3.Zero,
                new Vector3(1f, 0f, 0f),
                Vector3.Zero,
                new FluidityContactFrame(CreateEvidence("left", Vector3.Zero), CreateEvidence("right", new Vector3(1f, 0f, 0f)))));
        }

        return samples;
    }

    private static IReadOnlyList<FluidityExpectedSupportInterval> CreateIntervals()
        => [new FluidityExpectedSupportInterval("left", 0f, 0.2f, 7), new FluidityExpectedSupportInterval("right", 0f, 0.2f, 7)];

    private static List<NPCFluiditySample> MutateFrames(Func<FluidityContactFrame?, FluidityContactFrame?> mutate)
    {
        List<NPCFluiditySample> samples = CreateCleanSamples();
        for (int index = 0; index < samples.Count; index++)
        {
            samples[index] = samples[index] with
            {
                Contacts = mutate(samples[index].Contacts)
            };
        }

        return samples;
    }

    private static List<NPCFluiditySample> MutateLeft(Func<FluidityFootContactEvidence, FluidityFootContactEvidence?> mutate)
    {
        List<NPCFluiditySample> samples = CreateCleanSamples();
        for (int index = 0; index < samples.Count; index++)
        {
            FluidityContactFrame frame = Assert.IsType<FluidityContactFrame>(samples[index].Contacts, exactMatch: false);
            samples[index] = samples[index] with
            {
                Contacts = frame with
                {
                    Left = mutate(frame.Left!)
                },
            };
        }

        return samples;
    }

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

    private static void AssertRejected(NPCFluidityGateResult result, string reason)
    {
        Assert.False(result.Passed, result.BuildReport());
        Assert.Contains(result.RejectedSamples, rejection => rejection.Reason.Contains(reason, StringComparison.Ordinal));
    }
}
