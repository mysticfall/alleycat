using AlleyCat.Control.Locomotion;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Control;

#pragma warning disable CS1591

public sealed class PlayerTravelReleaseOccurrenceTests
{
    public static TheoryData<string, Func<PlayerTravelReleaseOccurrenceV1, PlayerTravelReleaseOccurrenceV1>> RefusedOccurrenceMutations => new()
    {
        { "empty source", value => value with { SourceIdentity = string.Empty } },
        { "source mismatch", value => value with { SourceIdentity = "other-ember" } },
        { "role mismatch", value => value with { EvaluatedRole = SyntheticTravelFixture.ReverseRole } },
        { "clip mismatch", value => value with { EvaluatedClip = "other-ember.res" } },
        { "animation mismatch", value => value with { EvaluatedAnimation = "locomotion/other_ember" } },
        { "ambiguous source", value => value with { SourceAuthority = PlayerTravelEvaluatedSourceAuthority.AmbiguousBlend } },
        { "inferred source", value => value with { SourceAuthority = PlayerTravelEvaluatedSourceAuthority.InferredNearest } },
        { "event mismatch", value => value with { ReleaseEvent = "release_r" } },
        { "unknown event", value => value with { ReleaseEvent = "unknown" } },
        { "side mismatch", value => value with { AnatomicalSide = PlayerTravelReleaseSide.Right } },
        { "unknown side", value => value with { AnatomicalSide = PlayerTravelReleaseSide.Unknown } },
        { "exit role mismatch", value => value with { ExitRole = "b2i_l" } },
        { "exit clip mismatch", value => value with { ExitClip = "other-ember-exit.res" } },
        { "catalogue mismatch", value => value with { CatalogueProvenance = "other-ember-catalogue" } },
        { "authored event time mismatch", value => value with { ClockTransform = value.ClockTransform with { AuthoredEventTimeSeconds = 0.25d } } },
        { "transformed event clock mismatch", value => value with { ClockTransform = value.ClockTransform with { TransformedEventPhase = 0.25d } } },
        { "seam transform mismatch", value => value with { ClockTransform = value.ClockTransform with { SeamStartOffsetSeconds = 0.25d } } },
        { "transform provenance mismatch", value => value with { ClockTransform = value.ClockTransform with { Provenance = PlayerTravelClockTransformProvenance.Unknown } } },
        { "generation mismatch", value => value with { ClockGeneration = value.ClockGeneration + 1 } },
        { "cycle mismatch", value => value with { CycleIdentity = value.CycleIdentity + 1 } },
        { "occurrence mismatch", value => value with { OccurrenceIdentity = value.OccurrenceIdentity + 1 } },
        { "stale tick", value => value with { EvaluationPhysicsTick = value.EvaluationPhysicsTick - 1 } },
        { "unordered interval", value => value with { PreviousClock = value.CurrentClock } },
        { "non-finite interval", value => value with { CurrentClock = double.NaN } },
        { "non-crossing interval", value => value with { AuthoredEventClock = value.CurrentClock + 0.1d } },
        { "phase only", value => value with { EvidenceOrigin = PlayerTravelReleaseEvidenceOrigin.PhaseOnly } },
        { "unknown origin", value => value with { EvidenceOrigin = PlayerTravelReleaseEvidenceOrigin.Unknown } },
        { "static only", value => value with { EvidenceOrigin = PlayerTravelReleaseEvidenceOrigin.StaticSchedule } },
        { "inferred seam", value => value with { EvidenceOrigin = PlayerTravelReleaseEvidenceOrigin.InferredSeam } },
        { "contact lifecycle", value => value with { EvidenceOrigin = PlayerTravelReleaseEvidenceOrigin.ContactLifecycle } },
        { "cached", value => value with { EvidenceOrigin = PlayerTravelReleaseEvidenceOrigin.Cached } },
        { "delayed", value => value with { Freshness = PlayerTravelReleaseFreshness.Delayed } },
        { "stale", value => value with { Freshness = PlayerTravelReleaseFreshness.Stale } },
        { "unknown freshness", value => value with { Freshness = PlayerTravelReleaseFreshness.Unknown } },
        { "missed", value => value with { Crossing = PlayerTravelReleaseCrossing.Missed } },
        { "not crossed", value => value with { Crossing = PlayerTravelReleaseCrossing.NotCrossed } },
        { "unknown crossing", value => value with { Crossing = PlayerTravelReleaseCrossing.Unknown } },
        { "refused", value => value with { Admission = PlayerTravelReleaseAdmission.Refused } },
        { "unknown admission", value => value with { Admission = PlayerTravelReleaseAdmission.Unknown } },
        { "consumed", value => value with { Consumption = PlayerTravelReleaseConsumption.Consumed } },
        { "cancelled", value => value with { Consumption = PlayerTravelReleaseConsumption.Cancelled } },
        { "replaced", value => value with { Consumption = PlayerTravelReleaseConsumption.Replaced } },
        { "unknown consumption", value => value with { Consumption = PlayerTravelReleaseConsumption.Unknown } },
    };

    [Theory]
    [InlineData(PlayerTravelReleaseSide.Left, SyntheticTravelFixture.WalkRole, SyntheticTravelFixture.LeftExitRole, PlayerLocomotionCommand.ExitForward)]
    [InlineData(PlayerTravelReleaseSide.Right, SyntheticTravelFixture.WalkRole, SyntheticTravelFixture.RightExitRole, PlayerLocomotionCommand.ExitForward)]
    [InlineData(PlayerTravelReleaseSide.Left, SyntheticTravelFixture.ReverseRole, "b2i_l", PlayerLocomotionCommand.ExitBackward)]
    [InlineData(PlayerTravelReleaseSide.Right, SyntheticTravelFixture.ReverseRole, "b2i_r", PlayerLocomotionCommand.ExitBackward)]
    public void FreshCrossing_NeutralIntent_ConsumesOnceAndSelectsCompatibleExit(
        PlayerTravelReleaseSide side,
        string role,
        string exitRole,
        PlayerLocomotionCommand expectedCommand)
    {
        PlayerTravelReleaseGate gate = new();
        PlayerTravelReleaseOccurrenceV1 occurrence = Cross(gate, side, role, exitRole);

        Assert.True(gate.TryConsume(occurrence, 2, side, out PlayerTravelReleasePermitV1 permit));
        float lateral = side == PlayerTravelReleaseSide.Right ? 0.2f : 0f;
        PlayerLocomotionController controller = EnterTravel(new Vector2(lateral, role == SyntheticTravelFixture.ReverseRole ? -1f : 1f));
        PlayerLocomotionOutput output = Step(controller, Vector2.Zero, release: permit);

        Assert.Equal(expectedCommand, output.Command);
        Assert.Equal(side == PlayerTravelReleaseSide.Left ? PlayerLocomotionPhaseLane.Left : PlayerLocomotionPhaseLane.Right, output.CommandLane);
        Assert.False(gate.TryConsume(occurrence, 2, side, out _));
    }

    [Fact]
    public void ReverseDerivedAcquireEvent_PreservesDeclaredEventIdentityThroughConsumption()
    {
        PlayerTravelReleaseGate gate = new();
        PlayerTravelReleaseCandidateV1 candidate = Candidate(
            PlayerTravelReleaseSide.Left,
            SyntheticTravelFixture.ReverseRole,
            "b2i_l") with
        {
            ReleaseEvent = "acquire_l",
        };
        Assert.Null(gate.Observe(candidate, Evaluation(
            1,
            0.8d,
            0.9d,
            PlayerTravelReleaseSide.Left,
            SyntheticTravelFixture.ReverseRole)));
        PlayerTravelReleaseOccurrenceV1 occurrence = gate.Observe(candidate, Evaluation(
            2,
            0.9d,
            0.01d,
            PlayerTravelReleaseSide.Left,
            SyntheticTravelFixture.ReverseRole))!.Value;

        Assert.Equal("acquire_l", occurrence.ReleaseEvent);
        Assert.True(gate.TryConsume(occurrence, 2, PlayerTravelReleaseSide.Left, out _));
    }

    [Theory]
    [MemberData(nameof(RefusedOccurrenceMutations))]
    public void OccurrenceMismatch_RefusesClosedFamily(
        string caseName,
        Func<PlayerTravelReleaseOccurrenceV1, PlayerTravelReleaseOccurrenceV1> mutate)
    {
        _ = caseName;
        PlayerTravelReleaseGate gate = new();
        PlayerTravelReleaseOccurrenceV1 occurrence = Cross(gate);

        Assert.False(gate.TryConsume(mutate(occurrence), 2, PlayerTravelReleaseSide.Left, out _));
        Assert.True(gate.TryConsume(occurrence, 2, PlayerTravelReleaseSide.Left, out _));
    }

    [Theory]
    [InlineData(PlayerTravelReleaseEvidenceOrigin.PhaseOnly)]
    [InlineData(PlayerTravelReleaseEvidenceOrigin.StaticSchedule)]
    [InlineData(PlayerTravelReleaseEvidenceOrigin.InferredSeam)]
    [InlineData(PlayerTravelReleaseEvidenceOrigin.ContactLifecycle)]
    [InlineData(PlayerTravelReleaseEvidenceOrigin.Cached)]
    public void NonLiveEvidence_CannotPublish(PlayerTravelReleaseEvidenceOrigin origin)
    {
        PlayerTravelReleaseGate gate = new();
        PlayerTravelReleaseCandidateV1 candidate = Candidate();
        _ = gate.Observe(candidate, Evaluation(1, 0.8d, 0.9d) with
        {
            EvidenceOrigin = origin
        });

        Assert.Null(gate.Observe(candidate, Evaluation(2, 0.9d, 0.01d) with
        {
            EvidenceOrigin = origin
        }));
    }

    [Fact]
    public void MissingMalformedAndMismatchedCandidate_CannotPublish()
    {
        PlayerTravelReleaseGate gate = new();
        PlayerTravelReleaseCandidateV1 valid = Candidate();

        Assert.Null(gate.Observe(valid with
        {
            SourceIdentity = string.Empty
        }, Evaluation(1, 0.8d, 0.9d)));
        Assert.Null(gate.Observe(valid with
        {
            ClockTransform = valid.ClockTransform with
            {
                AuthoredEventTimeSeconds = double.NaN
            }
        }, Evaluation(2, 0.9d, 0.01d)));
        Assert.Null(gate.Observe(valid, Evaluation(3, 0.8d, 0.9d) with
        {
            EvaluatedClip = "unknown.res"
        }));
        Assert.Null(gate.Observe(valid, Evaluation(4, 0.9d, 0.01d)));
    }

    [Fact]
    public void PublishedOccurrence_CarriesCompleteImmutableAdmissionSchema()
    {
        PlayerTravelReleaseGate gate = new();
        PlayerTravelReleaseOccurrenceV1 occurrence = Cross(gate);

        Assert.Equal(SyntheticTravelFixture.SourceIdentity, occurrence.SourceIdentity);
        Assert.Equal(SyntheticTravelFixture.WalkRole, occurrence.EvaluatedRole);
        Assert.Equal("ember/wf.res", occurrence.EvaluatedClip);
        Assert.Equal("locomotion/ember_wf", occurrence.EvaluatedAnimation);
        Assert.Equal(PlayerTravelEvaluatedSourceAuthority.SingularBlendPoint, occurrence.SourceAuthority);
        Assert.Equal("release_l", occurrence.ReleaseEvent);
        Assert.Equal(PlayerTravelReleaseSide.Left, occurrence.AnatomicalSide);
        Assert.Equal(SyntheticTravelFixture.LeftExitRole, occurrence.ExitRole);
        Assert.Equal("ember/f2i_l.res", occurrence.ExitClip);
        Assert.Equal(SyntheticTravelFixture.CatalogueProvenance, occurrence.CatalogueProvenance);
        Assert.True(occurrence.ClockTransform.IsValid());
        Assert.Equal(SyntheticTravelFixture.ReleaseTimeSeconds, occurrence.ClockTransform.AuthoredEventTimeSeconds, 5);
        Assert.Equal(0d, occurrence.ClockTransform.TransformedEventPhase, 5);
        Assert.True(occurrence.ClockGeneration > 0);
        Assert.Equal(1, occurrence.CycleIdentity);
        Assert.True(occurrence.OccurrenceIdentity > 0);
        Assert.Equal(2, occurrence.EvaluationPhysicsTick);
        Assert.Equal(0.9d, occurrence.PreviousClock, 5);
        Assert.Equal(1.01d, occurrence.CurrentClock, 5);
        Assert.Equal(1d, occurrence.AuthoredEventClock, 5);
        Assert.Equal(PlayerTravelReleaseEvidenceOrigin.LiveEvaluatedClock, occurrence.EvidenceOrigin);
        Assert.Equal(PlayerTravelReleaseFreshness.CurrentPhysicsTick, occurrence.Freshness);
        Assert.Equal(PlayerTravelReleaseCrossing.Crossed, occurrence.Crossing);
        Assert.Equal(PlayerTravelReleaseAdmission.Admitted, occurrence.Admission);
        Assert.Equal(PlayerTravelReleaseConsumption.Available, occurrence.Consumption);
    }

    [Fact]
    public void InvalidUnorderedNonContiguousStaleAndMissedIntervals_CannotPublish()
    {
        PlayerTravelReleaseCandidateV1 candidate = Candidate();
        PlayerTravelReleaseGate nonContiguous = Seed(candidate);
        PlayerTravelReleaseGate staleTick = Seed(candidate);
        PlayerTravelReleaseGate unordered = Seed(candidate);
        PlayerTravelReleaseGate missed = Seed(candidate);

        Assert.Null(nonContiguous.Observe(candidate, Evaluation(2, 0.7d, 0.01d)));
        Assert.Null(staleTick.Observe(candidate, Evaluation(3, 0.9d, 0.01d)));
        Assert.Null(unordered.Observe(candidate, Evaluation(2, 0.9d, 0.9d)));
        Assert.Null(missed.Observe(candidate, Evaluation(2, 0.9d, 0.4d) with
        {
            ExpectedAdvance = 0.2d
        }));
    }

    [Fact]
    public void NonCrossingInterval_DoesNotPublishOrCacheAuthority()
    {
        PlayerTravelReleaseGate gate = Seed(Candidate());

        Assert.Null(gate.Observe(Candidate(), Evaluation(2, 0.9d, 0.95d)));
        Assert.False(gate.TryConsume(default, 2, PlayerTravelReleaseSide.Left, out _));
    }

    [Fact]
    public void ExactTransformedEvent_BeforeDoesNotPublish_ExactCrossingPublishes_AfterDoesNotReplay()
    {
        PlayerTravelReleaseCandidateV1 candidate = Candidate(eventPhase: SyntheticTravelFixture.ReleaseTimeSeconds);
        PlayerTravelReleaseGate gate = new();

        Assert.Null(gate.Observe(candidate, Evaluation(1, 0.2d, 0.3d)));
        Assert.Null(gate.Observe(candidate, Evaluation(2, 0.3d, 0.49d)));
        PlayerTravelReleaseOccurrenceV1 occurrence = gate.Observe(candidate, Evaluation(3, 0.49d, 0.5d))!.Value;
        Assert.Equal(SyntheticTravelFixture.ReleaseTimeSeconds, occurrence.AuthoredEventClock, 5);
        Assert.True(gate.TryConsume(occurrence, 3, PlayerTravelReleaseSide.Left, out _));
        Assert.Null(gate.Observe(candidate, Evaluation(4, 0.5d, 0.6d)));
        Assert.False(gate.TryConsume(occurrence, 4, PlayerTravelReleaseSide.Left, out _));
    }

    [Fact]
    public void CyclicBoundary_IsContinuityOnly_AndDoesNotReplaceLaterAuthoredEvent()
    {
        PlayerTravelReleaseCandidateV1 candidate = Candidate(eventPhase: SyntheticTravelFixture.ReleaseTimeSeconds);
        PlayerTravelReleaseGate gate = new();

        Assert.Null(gate.Observe(candidate, Evaluation(1, 0.8d, 0.9d)));
        Assert.Null(gate.Observe(candidate, Evaluation(2, 0.9d, 0.1d)));
        PlayerTravelReleaseOccurrenceV1 occurrence = gate.Observe(candidate, Evaluation(3, 0.1d, 0.51d) with
        {
            ExpectedAdvance = 0.42d
        })!.Value;

        Assert.Equal(1.5d, occurrence.AuthoredEventClock, 5);
        Assert.Equal(2, occurrence.CycleIdentity);
    }

    [Fact]
    public void FiniteEndpointEvent_CrossesAtEndpointWithoutSeamInference()
    {
        PlayerTravelReleaseCandidateV1 candidate = Candidate() with
        {
            ClockTransform = PlayerTravelReleaseClockTransformV1.Create(1d, 1d, 1d, 0d)
        };
        PlayerTravelReleaseGate gate = new();

        Assert.Null(gate.Observe(candidate, Evaluation(1, 0.8d, 0.9d)));
        PlayerTravelReleaseOccurrenceV1 occurrence = gate.Observe(candidate, Evaluation(2, 0.9d, 0.01d))!.Value;

        Assert.Equal(1d, occurrence.ClockTransform.TransformedEventPhase, 5);
        Assert.Equal(1d, occurrence.AuthoredEventClock, 5);
    }

    [Fact]
    public void AlteredFiniteCatalogueEventOrTransformedClock_FailsClosed()
    {
        PlayerTravelReleaseCandidateV1 valid = Candidate(eventPhase: SyntheticTravelFixture.ReleaseTimeSeconds);
        PlayerTravelReleaseCandidateV1 alteredEvent = valid with
        {
            ClockTransform = valid.ClockTransform with
            {
                AuthoredEventTimeSeconds = 0.4d
            }
        };
        PlayerTravelReleaseCandidateV1 alteredClock = valid with
        {
            ClockTransform = valid.ClockTransform with
            {
                TransformedEventPhase = 0.4d
            }
        };

        Assert.Null(new PlayerTravelReleaseGate().Observe(alteredEvent, Evaluation(1, 0.2d, 0.3d)));
        Assert.Null(new PlayerTravelReleaseGate().Observe(alteredClock, Evaluation(1, 0.2d, 0.3d)));
        Assert.Null(new PlayerTravelReleaseGate().Observe(valid, Evaluation(1, 0.2d, 0.3d) with
        {
            EvaluatedCycleSeconds = 0.9d
        }));
    }

    [Theory]
    [InlineData(PlayerTravelEvaluatedSourceAuthority.Unknown)]
    [InlineData(PlayerTravelEvaluatedSourceAuthority.AmbiguousBlend)]
    [InlineData(PlayerTravelEvaluatedSourceAuthority.InferredNearest)]
    public void NonSingularOrInferredEvaluatedSource_FailsClosed(PlayerTravelEvaluatedSourceAuthority authority)
    {
        PlayerTravelReleaseCandidateV1 candidate = Candidate() with
        {
            SourceAuthority = authority
        };
        PlayerTravelClockEvaluationV1 evaluation = Evaluation(1, 0.8d, 0.9d) with
        {
            SourceAuthority = authority
        };
        Assert.Null(new PlayerTravelReleaseGate().Observe(candidate, evaluation));
    }

    [Fact]
    public void CancellationAndReplacementInvalidateOldAuthorityWithoutTransfer()
    {
        PlayerTravelReleaseGate cancelledGate = new();
        PlayerTravelReleaseOccurrenceV1 cancelled = Cross(cancelledGate);
        cancelledGate.Cancel();
        Assert.False(cancelledGate.TryConsume(cancelled, 2, PlayerTravelReleaseSide.Left, out _));

        PlayerTravelReleaseGate replacedGate = new();
        PlayerTravelReleaseOccurrenceV1 replaced = Cross(replacedGate);
        _ = replacedGate.Observe(Candidate(), Evaluation(3, 0.01d, 0.9d) with
        {
            ExpectedAdvance = 0.9d
        });
        PlayerTravelReleaseOccurrenceV1 replacement = replacedGate.Observe(Candidate(), Evaluation(4, 0.9d, 0.01d))!.Value;

        Assert.False(replacedGate.TryConsume(replaced, 4, PlayerTravelReleaseSide.Left, out _));
        Assert.True(replacedGate.TryConsume(replacement, 4, PlayerTravelReleaseSide.Left, out _));
    }

    [Fact]
    public void RepushDoesNotConsumeOccurrenceOrExitTravel()
    {
        PlayerTravelReleaseGate gate = new();
        PlayerTravelReleaseOccurrenceV1 occurrence = Cross(gate);
        Assert.True(gate.TryConsume(occurrence, 2, PlayerTravelReleaseSide.Left, out PlayerTravelReleasePermitV1 permit));
        PlayerLocomotionController controller = EnterTravel(new Vector2(0f, 1f));

        PlayerLocomotionOutput output = Step(controller, new Vector2(0f, 1f), release: permit);

        Assert.Equal(PlayerLocomotionCommand.None, output.Command);
        Assert.Equal(PlayerLocomotionState.TravellingForward, output.State);
    }

    private static PlayerTravelReleaseGate Seed(PlayerTravelReleaseCandidateV1 candidate)
    {
        PlayerTravelReleaseGate gate = new();
        Assert.Null(gate.Observe(candidate, Evaluation(1, 0.8d, 0.9d)));
        return gate;
    }

    private static PlayerTravelReleaseOccurrenceV1 Cross(
        PlayerTravelReleaseGate gate,
        PlayerTravelReleaseSide side = PlayerTravelReleaseSide.Left,
        string role = SyntheticTravelFixture.WalkRole,
        string exitRole = SyntheticTravelFixture.LeftExitRole)
    {
        PlayerTravelReleaseCandidateV1 candidate = SyntheticTravelFixture.Candidate(side, role, exitRole);
        Assert.Null(gate.Observe(candidate, Evaluation(1, 0.8d, 0.9d, side, role)));
        return gate.Observe(candidate, Evaluation(2, 0.9d, 0.01d, side, role))!.Value;
    }

    private static PlayerTravelReleaseCandidateV1 Candidate(
        PlayerTravelReleaseSide side = PlayerTravelReleaseSide.Left,
        string role = SyntheticTravelFixture.WalkRole,
        string exitRole = SyntheticTravelFixture.LeftExitRole,
        double eventPhase = 0d)
        => SyntheticTravelFixture.Candidate(side, role, exitRole, eventPhase);

    private static PlayerTravelClockEvaluationV1 Evaluation(
        long tick,
        double previous,
        double current,
        PlayerTravelReleaseSide side = PlayerTravelReleaseSide.Left,
        string role = SyntheticTravelFixture.WalkRole)
        => SyntheticTravelFixture.Evaluation(tick, previous, current, side, role);

    private static PlayerLocomotionController EnterTravel(Vector2 movement)
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, movement);
        _ = Step(controller, movement, completed: true);
        return controller;
    }

    private static PlayerLocomotionOutput Step(
        PlayerLocomotionController controller,
        Vector2 movement,
        bool completed = false,
        PlayerTravelReleasePermitV1? release = null)
        => controller.Step(
            movement,
            0f,
            LocomotionPermissions.Allowed,
            completed,
            0.75f,
            1.2f,
            release);
}
