using System.Reflection;
using AlleyCat.Mind.Attention;
using Xunit;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.Tests.Mind.Attention;

/// <summary>Unit coverage for pure attention-driven gaze ranking, dwell, and assignment decisions.</summary>
public sealed class AttentionGazeTargetPolicyTests
{
    /// <summary>Attention domain contracts live beneath the dedicated namespace without changing Mind's public snapshot API.</summary>
    [Fact]
    public void AttentionContracts_AreRelocatedAndPreserveAttentionBehaviour()
    {
        Assert.Equal("AlleyCat.Mind.Attention", typeof(AttentionPolicy).Namespace);
        Assert.Equal("AlleyCat.Mind.Attention", typeof(AttentionSettings).Namespace);
        Assert.Equal("AlleyCat.Mind.Attention", typeof(AttentionSnapshot).Namespace);
        Assert.Equal("AlleyCat.Mind.Attention", typeof(AttentionEffect).Namespace);
        Assert.Equal(typeof(AttentionSnapshot), typeof(MindBase).GetMethod(nameof(MindBase.GetAttentionSnapshot))!.ReturnType);
        Assert.Null(typeof(MindBase).Assembly.GetType("AlleyCat.Mind.AttentionPolicy"));
        Assert.Null(typeof(MindBase).Assembly.GetType("AlleyCat.Mind.AttentionSettings"));
        Assert.Null(typeof(MindBase).Assembly.GetType("AlleyCat.Mind.AttentionSnapshot"));
        Assert.Null(typeof(MindBase).Assembly.GetType("AlleyCat.Mind.Perception.AttentionEffect"));

        double timestamp = 0d;
        AttentionPolicy policy = new(() => timestamp);
        var settings = AttentionSettings.Create(1f, 0.1f, 0.1f, 0.5f);
        policy.Reinforce("char:zulu", 0.5f, settings);
        policy.Reinforce("char:alpha", 1f, settings);
        _ = policy.GetSnapshot(settings);

        timestamp = 2d;
        AttentionSnapshot snapshot = policy.GetSnapshot(settings);

        Assert.Equal(2d, snapshot.Timestamp);
        Assert.Equal(["char:alpha", "char:zulu"], snapshot.Values.Keys);
        Assert.Equal(0.8f, snapshot.Values["char:alpha"], 3);
        Assert.Equal(0.3f, snapshot.Values["char:zulu"], 3);
        Assert.Equal(["char:alpha"], policy.GetContextEligibleIDs(settings));
    }

    /// <summary>Highest attention-times-prominence score wins, then ordinal identity and provider order resolve exact ties.</summary>
    [Fact]
    public void Evaluate_SelectsHighestScoreAndDeterministicTies()
    {
        AttentionGazeTargetPolicy<string> scorePolicy = CreatePolicy(primaryDwellSeconds: 5d);

        AttentionGazeTargetDecision<string> scored = scorePolicy.Evaluate(0d,
        [
            Candidate("char:zulu", "body", 0, "zulu-body", attention: 0.9d, prominence: 2d),
            Candidate("char:alpha", "face", 0, "alpha-face", attention: 0.4d, prominence: 5d),
        ]);

        AssertSet(scored, "alpha-face");

        AttentionGazeTargetPolicy<string> subjectTiePolicy = CreatePolicy(primaryDwellSeconds: 5d);
        AttentionGazeTargetDecision<string> subjectTie = subjectTiePolicy.Evaluate(0d,
        [
            Candidate("char:zulu", "body", 0, "zulu", attention: 1d, prominence: 1d),
            Candidate("char:alpha", "body", 0, "alpha", attention: 1d, prominence: 1d),
        ]);

        AssertSet(subjectTie, "alpha");

        AttentionGazeTargetPolicy<string> cueTiePolicy = CreatePolicy(primaryDwellSeconds: 5d);
        AttentionGazeTargetDecision<string> cueTie = cueTiePolicy.Evaluate(0d,
        [
            Candidate("char:alpha", "later", 1, "later", attention: 1d, prominence: 1d),
            Candidate("char:alpha", "earlier", 0, "earlier", attention: 1d, prominence: 1d),
        ]);

        AssertSet(cueTie, "earlier");
    }

    /// <summary>Multiple subjects and cues remain independently rankable using their adapter-provided target tokens.</summary>
    [Fact]
    public void Evaluate_ConsidersEverySubjectCueCandidate()
    {
        AttentionGazeTargetPolicy<string> policy = CreatePolicy(primaryDwellSeconds: 5d);

        AttentionGazeTargetDecision<string> decision = policy.Evaluate(0d,
        [
            Candidate("char:alpha", "body", 0, "alpha-body", attention: 0.6d, prominence: 1d),
            Candidate("char:alpha", "face", 1, "alpha-face", attention: 0.6d, prominence: 3d),
            Candidate("char:bravo", "body", 0, "bravo-body", attention: 0.9d, prominence: 2d),
        ]);

        AssertSet(decision, "bravo-body");
    }

    /// <summary>Explicitly invalid candidates and disabled or malformed prominence values cannot become gaze targets.</summary>
    [Fact]
    public void Evaluate_RejectsInvalidDisabledAndInvalidProminenceCandidates()
    {
        AttentionGazeTargetPolicy<string> policy = CreatePolicy(primaryDwellSeconds: 5d);

        AttentionGazeTargetDecision<string> decision = policy.Evaluate(0d,
        [
            Candidate("char:disabled", "body", 0, "disabled", attention: 100d, prominence: 1d, isValid: false),
            Candidate("char:zero", "body", 0, "zero", attention: 100d, prominence: 0d),
            Candidate("char:negative", "body", 0, "negative", attention: 100d, prominence: -1d),
            Candidate("char:nan", "body", 0, "nan", attention: 100d, prominence: double.NaN),
            Candidate("char:infinite", "body", 0, "infinite", attention: 100d, prominence: double.PositiveInfinity),
            Candidate("char:invalid-attention", "body", 0, "invalid-attention", attention: double.NaN, prominence: 1d),
            Candidate("char:valid", "body", 0, "valid", attention: 0.1d, prominence: 1d),
        ]);

        AssertSet(decision, "valid");
    }

    /// <summary>The pure candidate contract adds no subject-type, range, visibility, or semantic filtering.</summary>
    [Fact]
    public void Evaluate_DoesNotApplySemanticOrGeometryFiltering()
    {
        AttentionGazeTargetPolicy<string> policy = CreatePolicy(primaryDwellSeconds: 5d);

        AttentionGazeTargetDecision<string> decision = policy.Evaluate(0d,
        [
            Candidate("prop:unseen-far-subject", "conversation-point", 0, "opaque-target", attention: 1d, prominence: 1d),
        ]);

        AssertSet(decision, "opaque-target");
        Assert.DoesNotContain(
            typeof(AttentionGazeTargetCandidate<string>).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.Name is "Distance" or "Range" or "Visible" or "Occluded" or "Angle" or "SubjectType");
    }

    /// <summary>Primary and secondary assignments stay stable through ranking changes until their relevant dwell boundary.</summary>
    [Fact]
    public void Evaluate_PreservesPrimaryAndSecondaryDwellAcrossRankingChanges()
    {
        AttentionGazeTargetPolicy<string> policy = CreatePolicy(
            primaryDwellSeconds: 10d,
            secondaryDwellSeconds: 2d,
            secondaryGlanceProbability: 1d,
            randomSamples: [0.1d]);
        AttentionGazeTargetCandidate<string>[] initial =
        [
            Candidate("char:alpha", "body", 0, "alpha", attention: 3d, prominence: 1d),
            Candidate("char:bravo", "body", 0, "bravo", attention: 1d, prominence: 1d),
        ];

        AssertSet(policy.Evaluate(0d, initial), "alpha");
        AssertNone(policy.Evaluate(9d,
        [
            Candidate("char:alpha", "body", 0, "alpha", attention: 1d, prominence: 1d),
            Candidate("char:bravo", "body", 0, "bravo", attention: 10d, prominence: 1d),
        ]));
        AssertSet(policy.Evaluate(1d,
        [
            Candidate("char:alpha", "body", 0, "alpha", attention: 1d, prominence: 1d),
            Candidate("char:bravo", "body", 0, "bravo", attention: 10d, prominence: 1d),
        ]), "bravo");
        AssertNone(policy.Evaluate(1d,
        [
            Candidate("char:alpha", "body", 0, "alpha", attention: 20d, prominence: 1d),
            Candidate("char:bravo", "body", 0, "bravo", attention: 1d, prominence: 1d),
        ]));
        AssertSet(policy.Evaluate(1d,
        [
            Candidate("char:alpha", "body", 0, "alpha", attention: 20d, prominence: 1d),
            Candidate("char:bravo", "body", 0, "bravo", attention: 1d, prominence: 1d),
        ]), "alpha");
    }

    /// <summary>Secondary choices are score weighted with deterministic samples and have an explicit all-zero fallback.</summary>
    [Fact]
    public void Evaluate_SelectsWeightedSecondaryAndFallsBackForAllZeroWeights()
    {
        AttentionGazeTargetPolicy<string> weightedPolicy = CreatePolicy(
            primaryDwellSeconds: 1d,
            secondaryDwellSeconds: 0.5d,
            secondaryGlanceProbability: 1d,
            randomSamples: [0.9d]);
        AttentionGazeTargetCandidate<string>[] weightedCandidates =
        [
            Candidate("char:alpha", "body", 0, "alpha", attention: 5d, prominence: 1d),
            Candidate("char:bravo", "body", 0, "bravo", attention: 3d, prominence: 1d),
            Candidate("char:charlie", "body", 0, "charlie", attention: 1d, prominence: 1d),
        ];

        AssertSet(weightedPolicy.Evaluate(0d, weightedCandidates), "alpha");
        AssertSet(weightedPolicy.Evaluate(1d, weightedCandidates), "charlie");

        AttentionGazeTargetPolicy<string> earlyWeightedPolicy = CreatePolicy(
            primaryDwellSeconds: 1d,
            secondaryDwellSeconds: 0.5d,
            secondaryGlanceProbability: 1d,
            randomSamples: [0d]);
        AssertSet(earlyWeightedPolicy.Evaluate(0d, weightedCandidates), "alpha");
        AssertSet(earlyWeightedPolicy.Evaluate(1d, weightedCandidates), "bravo");

        AttentionGazeTargetPolicy<string> zeroWeightPolicy = CreatePolicy(
            primaryDwellSeconds: 1d,
            secondaryDwellSeconds: 0.5d,
            secondaryGlanceProbability: 1d);
        AttentionGazeTargetCandidate<string>[] zeroWeightCandidates =
        [
            Candidate("char:alpha", "body", 0, "alpha", attention: 1d, prominence: 1d),
            Candidate("char:bravo", "body", 0, "bravo", attention: 0d, prominence: 1d),
            Candidate("char:charlie", "body", 0, "charlie", attention: 0d, prominence: 1d),
        ];

        AssertSet(zeroWeightPolicy.Evaluate(0d, zeroWeightCandidates), "alpha");
        AssertSet(zeroWeightPolicy.Evaluate(1d, zeroWeightCandidates), "bravo");
    }

    /// <summary>Missing retained targets return, reselect, or clear at subsequent evaluations without stale assignments.</summary>
    [Fact]
    public void Evaluate_ReturnsReselectsAndClearsWhenRetainedTargetsAreRemoved()
    {
        AttentionGazeTargetPolicy<string> policy = CreatePolicy(
            primaryDwellSeconds: 1d,
            secondaryDwellSeconds: 0.5d,
            secondaryGlanceProbability: 1d,
            randomSamples: [0d]);
        AttentionGazeTargetCandidate<string>[] initial =
        [
            Candidate("char:alpha", "body", 0, "alpha", attention: 3d, prominence: 1d),
            Candidate("char:bravo", "body", 0, "bravo", attention: 2d, prominence: 1d),
        ];

        AssertSet(policy.Evaluate(0d, initial), "alpha");
        AssertSet(policy.Evaluate(1d, initial), "bravo");
        AssertSet(policy.Evaluate(0d,
        [
            Candidate("char:alpha", "body", 0, "alpha", attention: 3d, prominence: 1d),
        ]), "alpha");
        AssertSet(policy.Evaluate(0d,
        [
            Candidate("char:charlie", "body", 0, "charlie", attention: 1d, prominence: 1d),
        ]), "charlie");
        AssertClear(policy.Evaluate(0d, []));
        AssertNone(policy.Evaluate(0d, []));
    }

    /// <summary>Configuration, random samples, and evaluation deltas fail clearly, while long deltas make only one transition.</summary>
    [Fact]
    public void Evaluate_ValidatesInputsAndUsesSingleDeltaDrivenBoundary()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new AttentionGazeTargetSettings(0d, 0.5d, 0.5d));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new AttentionGazeTargetSettings(double.PositiveInfinity, 0.5d, 0.5d));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new AttentionGazeTargetSettings(1d, 1d, 0.5d));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new AttentionGazeTargetSettings(1d, 0.5d, -0.1d));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new AttentionGazeTargetSettings(1d, 0.5d, double.NaN));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new AttentionGazeTargetSettings(1d, 0.5d, 1.1d));

        AttentionGazeTargetPolicy<string> policy = CreatePolicy(
            primaryDwellSeconds: 1d,
            secondaryDwellSeconds: 0.5d,
            secondaryGlanceProbability: 1d,
            randomSamples: [0d]);
        AttentionGazeTargetCandidate<string>[] candidates =
        [
            Candidate("char:alpha", "body", 0, "alpha", attention: 2d, prominence: 1d),
            Candidate("char:bravo", "body", 0, "bravo", attention: 1d, prominence: 1d),
        ];

        AssertSet(policy.Evaluate(0d, candidates), "alpha");
        AssertSet(policy.Evaluate(100d, candidates), "bravo");
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => policy.Evaluate(-0.1d, candidates));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => policy.Evaluate(double.NaN, candidates));

        AttentionGazeTargetPolicy<string> noSecondaryPolicy = CreatePolicy(
            primaryDwellSeconds: 1d,
            secondaryDwellSeconds: 0.5d,
            secondaryGlanceProbability: 0d);
        AssertSet(noSecondaryPolicy.Evaluate(0d, candidates), "alpha");
        AssertNone(noSecondaryPolicy.Evaluate(1d, candidates));

        AttentionGazeTargetPolicy<string> invalidRandomPolicy = CreatePolicy(
            primaryDwellSeconds: 1d,
            secondaryDwellSeconds: 0.5d,
            secondaryGlanceProbability: 0.5d,
            randomSamples: [1d]);
        AssertSet(invalidRandomPolicy.Evaluate(0d, candidates), "alpha");
        _ = Assert.Throws<InvalidOperationException>(() => invalidRandomPolicy.Evaluate(1d, candidates));
    }

    private static AttentionGazeTargetPolicy<string> CreatePolicy(
        double primaryDwellSeconds,
        double secondaryDwellSeconds = 1d,
        double secondaryGlanceProbability = 0d,
        double[]? randomSamples = null)
        => new(
            new AttentionGazeTargetSettings(primaryDwellSeconds, secondaryDwellSeconds, secondaryGlanceProbability),
            new ScriptedRandom(randomSamples ?? []));

    private static AttentionGazeTargetCandidate<string> Candidate(
        string subjectFullId,
        string cueKey,
        int cueOrder,
        string target,
        double attention,
        double prominence,
        bool isValid = true)
        => new(subjectFullId, cueKey, cueOrder, target, attention, prominence, isValid);

    private static void AssertSet(AttentionGazeTargetDecision<string> decision, string expectedTarget)
    {
        Assert.Equal(AttentionGazeTargetAction.SetLookTarget, decision.Action);
        Assert.Equal(expectedTarget, decision.Target);
    }

    private static void AssertClear(AttentionGazeTargetDecision<string> decision)
    {
        Assert.Equal(AttentionGazeTargetAction.ClearLookTarget, decision.Action);
        Assert.Null(decision.Target);
    }

    private static void AssertNone(AttentionGazeTargetDecision<string> decision)
    {
        Assert.Equal(AttentionGazeTargetAction.None, decision.Action);
        Assert.Null(decision.Target);
    }

    private sealed class ScriptedRandom(IEnumerable<double> samples) : IAttentionGazeRandom
    {
        private readonly Queue<double> _samples = new(samples);

        public double NextUnitInterval()
            => _samples.Count == 0
                ? throw new InvalidOperationException("No scripted random sample remains.")
                : _samples.Dequeue();
    }
}
