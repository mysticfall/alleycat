using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Navigation;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Tool;

/// <summary>
/// Deterministic coverage of the coordinate navigation controller (AI-015 TR-4–TR-15, TR-19): refusals leave
/// navigation state unchanged, also during an active move; every started move receives exactly one outcome —
/// including replacement and <c>stop</c>; requests carry the mapped pace and the requested or travel-derived
/// facing; and outcome texts end with the shared own-position sentence. Navigation state is NAV-001's production
/// request lifecycle behind a Godot-free facade double.
/// </summary>
public sealed class CoordinateNavigationControllerTests
{
    private readonly LifecycleNavigation _navigation = new();
    private readonly ScriptedNavigationEnvironment _environment = new();
    private readonly List<ObservedNavigationOutcome> _outcomes = [];

    private CoordinateNavigationController CreateController() => new(_navigation, _environment, _outcomes.Add);

    /// <summary>
    /// Every refusal kind during an active walk — locally invalid input and each NAV-001 refusal result — returns a
    /// contained "refused" text and changes nothing: the active request keeps its identity, no outcome is
    /// published, no pace is applied, and locally invalid input never reaches navigation (TR-5, TR-6, TR-10).
    /// </summary>
    [Fact]
    public void Refusals_DuringAnActiveMove_LeaveNavigationStateUnchanged()
    {
        CoordinateNavigationController controller = CreateController();
        Assert.True(controller.WalkTo(3, 4, heading: null, pace: null).Started);
        NavigationRequestIdentity active = _navigation.CurrentStatus.ActiveRequest!.Value;
        int submissions = _navigation.Submitted.Count;
        int speeds = _environment.AppliedSpeeds.Count;

        CoordinateNavigationResponse[] localRefusals =
        [
            controller.WalkTo(double.NaN, 1, null, null),
            controller.WalkTo(1, double.PositiveInfinity, null, null),
            controller.WalkTo(1, 1, double.NaN, null),
            controller.WalkTo(1, 1, null, "sprint"),
            controller.TurnTo(double.NegativeInfinity),
        ];
        Assert.Equal(submissions, _navigation.Submitted.Count);

        List<CoordinateNavigationResponse> navigationRefusals = [];
        foreach (NavigationDestinationResult refusal in (NavigationDestinationResult[])
            [
                NavigationDestinationResult.Invalid,
                NavigationDestinationResult.Unreachable,
                NavigationDestinationResult.NotReady,
                NavigationDestinationResult.OutcomeBackpressure,
            ])
        {
            _navigation.NextResult = refusal;
            navigationRefusals.Add(controller.WalkTo(-2, 5, 90, NavigationPace.Hurry));
            navigationRefusals.Add(controller.TurnTo(45));
        }

        foreach (CoordinateNavigationResponse response in localRefusals.Concat(navigationRefusals))
        {
            Assert.False(response.Started);
            Assert.Contains("refused", response.Message, StringComparison.Ordinal);
            Assert.EndsWith("Nothing changed; any move in progress continues.", response.Message, StringComparison.Ordinal);
        }

        Assert.Equal(active, _navigation.CurrentStatus.ActiveRequest);
        Assert.Empty(_navigation.TerminalOutcomes);
        Assert.Empty(_outcomes);
        Assert.Equal(speeds, _environment.AppliedSpeeds.Count);
        Assert.Equal(1, controller.PendingOutcomeCount);
        Assert.Contains("cannot be reached", navigationRefusals[2].Message, StringComparison.Ordinal);
        Assert.Contains("unknown pace 'sprint'", localRefusals[3].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A new move during motion replaces the active one: the start text states the replacement, the superseded move
    /// receives exactly one <c>replaced</c> outcome at once, and the replacing move later receives exactly one
    /// <c>arrived</c> outcome; repeated polling publishes nothing more and every record is acknowledged (TR-11, TR-13).
    /// </summary>
    [Fact]
    public void Replacement_GivesEachStartedMoveExactlyOneOutcome()
    {
        CoordinateNavigationController controller = CreateController();
        CoordinateNavigationResponse first = controller.WalkTo(1, 1, null, null);
        CoordinateNavigationResponse second = controller.TurnTo(180);

        Assert.DoesNotContain("replaced", first.Message, StringComparison.Ordinal);
        Assert.Contains("This replaced your earlier move.", second.Message, StringComparison.Ordinal);
        ObservedNavigationOutcome replaced = Assert.Single(_outcomes);
        Assert.Equal(NavigationOutcomeKind.Replaced, replaced.Kind);
        Assert.Equal(CoordinateMoveKind.Walk, replaced.Move);

        controller.Poll();
        _ = Assert.Single(_outcomes);
        Assert.True(_navigation.CompleteActive());
        controller.Poll();
        controller.Poll();

        Assert.Equal(2, _outcomes.Count);
        Assert.Equal(NavigationOutcomeKind.Arrived, _outcomes[1].Kind);
        Assert.Equal(CoordinateMoveKind.Turn, _outcomes[1].Move);
        Assert.Empty(_navigation.TerminalOutcomes);
        Assert.Equal(0, controller.PendingOutcomeCount);
    }

    /// <summary>
    /// <c>stop</c> during a move cancels it and gives exactly one <c>stopped</c> outcome; <c>stop</c> with nothing in
    /// progress succeeds as a no-op without clearing anything or producing an outcome (TR-9, TR-13).
    /// </summary>
    [Fact]
    public void Stop_GivesOneStoppedOutcome_AndIsANoOpWhenIdle()
    {
        CoordinateNavigationController controller = CreateController();
        CoordinateNavigationResponse idle = controller.Stop();
        Assert.False(idle.Started);
        Assert.Equal(0, _navigation.ClearCount);
        Assert.Empty(_outcomes);

        Assert.True(controller.WalkTo(2, -3, null, NavigationPace.Stroll).Started);
        CoordinateNavigationResponse stopped = controller.Stop();
        controller.Poll();

        Assert.True(stopped.Started);
        Assert.False(_navigation.CurrentStatus.HasActiveRequest);
        ObservedNavigationOutcome outcome = Assert.Single(_outcomes);
        Assert.Equal(NavigationOutcomeKind.Stopped, outcome.Kind);
        Assert.Null(outcome.FailureReason);

        Assert.False(controller.Stop().Started);
        _ = Assert.Single(_outcomes);
    }

    /// <summary>
    /// Terminals that are not completion, replacement or <c>stop</c> map to <c>failed</c> with a named reason —
    /// convergence failure, a blocked way, a cancellation not caused by <c>stop</c>, and a failure committed inside
    /// the accepting call itself (TR-13).
    /// </summary>
    [Fact]
    public void FailedTerminals_GiveOneFailedOutcomeWithAReason()
    {
        CoordinateNavigationController controller = CreateController();

        Assert.True(controller.WalkTo(1, 1, null, null).Started);
        Assert.True(_navigation.FailActive());
        controller.Poll();
        Assert.True(controller.WalkTo(1, 1, null, null).Started);
        Assert.True(_navigation.BlockActive());
        controller.Poll();
        Assert.True(controller.TurnTo(10).Started);
        _navigation.ClearDestination();
        controller.Poll();
        _navigation.FailImmediatelyOnAccept = true;
        Assert.True(controller.WalkTo(1, 1, null, null).Started);
        controller.Poll();

        Assert.Equal(4, _outcomes.Count);
        Assert.All(_outcomes, outcome => Assert.Equal(NavigationOutcomeKind.Failed, outcome.Kind));
        Assert.Equal(
            [
                "you could not get close enough to the destination",
                "the way was blocked",
                "the move was cancelled",
                "you cannot move right now",
            ],
            _outcomes.Select(static outcome => outcome.FailureReason));
        Assert.Empty(_navigation.TerminalOutcomes);
        Assert.Equal(0, controller.PendingOutcomeCount);
    }

    /// <summary>
    /// Terminal records of requests the controller did not start are neither published nor acknowledged: they stay
    /// with their own consumer (TR-14: the tools add no other delivery path).
    /// </summary>
    [Fact]
    public void Poll_LeavesRecordsOfForeignRequestsUntouched()
    {
        CoordinateNavigationController controller = CreateController();
        Assert.Equal(
            NavigationDestinationResult.Accepted,
            _navigation.SetDestination(new Transform3D(Basis.Identity, new Vector3(5, 0, 5))));
        Assert.True(controller.WalkTo(1, 1, null, null).Started);

        NavigationTerminalOutcomeRecord foreign = Assert.Single(_navigation.TerminalOutcomes);
        Assert.Equal(NavigationTerminalOutcome.Superseded, foreign.Outcome);
        Assert.Empty(_outcomes);
        Assert.True(_navigation.CompleteActive());
        controller.Poll();

        Assert.Equal(NavigationOutcomeKind.Arrived, Assert.Single(_outcomes).Kind);
        Assert.Equal(foreign, Assert.Single(_navigation.TerminalOutcomes));
    }

    /// <summary>
    /// <c>walk_to</c> submits one request at the absolute (x, z) with the navigation-map height, the requested
    /// facing (sin b, 0, −cos b) when a heading is given, and the mapped pace — <c>walk</c> by default (TR-4–TR-7).
    /// </summary>
    [Fact]
    public void WalkTo_WithHeading_SubmitsAbsoluteSpotMapHeightFacingAndPace()
    {
        _environment.SubjectTransform = new Transform3D(Basis.Identity, new Vector3(10, 2, 10));
        _environment.Projection = static point => new Vector3(point.X + 0.3f, 0.5f, point.Z);
        CoordinateNavigationController controller = CreateController();

        CoordinateNavigationResponse response = controller.WalkTo(-1.5, 4, -90, null);
        Assert.True(controller.WalkTo(2, 2, 0, "HURRY ").Started);
        Assert.True(controller.WalkTo(2, 2, 0, NavigationPace.Stroll).Started);

        Assert.True(response.Started);
        Assert.Equal(
            "Started walking to (-1.5, 4) at walk pace, to face 270° on arrival. You will be told when you arrive "
            + "or if the walk fails.",
            response.Message);
        Transform3D submitted = _navigation.Submitted[0];
        Assert.Equal(new Vector3(-1.5f, 0.5f, 4f), submitted.Origin);
        AssertFacing(new Vector3(-1, 0, 0), submitted);
        Assert.Equal([0.75f, 1.0f, 0.5f], _environment.AppliedSpeeds);
    }

    /// <summary>
    /// Without a heading the request's terminal facing is the bearing of the planned route's final segment long
    /// enough to define one, falling back to the straight line, then the current facing (TR-7).
    /// </summary>
    [Fact]
    public void WalkTo_WithoutHeading_FacesTheArrivalTravelDirection()
    {
        _environment.SubjectTransform = new Transform3D(CompassBearing.ToBasis(200), Vector3.Zero);
        CoordinateNavigationController controller = CreateController();

        // Final leg (2, 0, 0) → (2, 0, -3) runs map-up; the trailing zero-length segment is skipped.
        _environment.Path = [Vector3.Zero, new Vector3(2, 0, 0), new Vector3(2, 0, -3), new Vector3(2, 0, -3)];
        CoordinateNavigationResponse routed = controller.WalkTo(2, -3, null, null);
        Assert.True(routed.Started);
        Assert.DoesNotContain("to face", routed.Message, StringComparison.Ordinal);
        // Unknown route: the straight line from start to destination, here map-down-right at 135°.
        _environment.Path = [];
        Assert.True(controller.WalkTo(4, 4, null, null).Started);
        // A destination where the subject already stands keeps the current facing.
        Assert.True(controller.WalkTo(0.01, 0, null, null).Started);

        AssertFacing(new Vector3(0, 0, -1), _navigation.Submitted[0]);
        AssertFacing(new Vector3(1, 0, 1).Normalized(), _navigation.Submitted[1]);
        AssertFacing(CompassBearing.ToWorldFacing(200), _navigation.Submitted[2]);
    }

    /// <summary>
    /// <c>turn_to</c> submits a destination at the subject's current position with the normalised requested facing
    /// and no pace, so NAV-001 turns in place (TR-5, TR-8).
    /// </summary>
    [Fact]
    public void TurnTo_SubmitsTheCurrentPositionWithTheRequestedFacing()
    {
        Vector3 position = new(1.25f, 0.1f, -7f);
        _environment.SubjectTransform = new Transform3D(Basis.Identity, position);
        CoordinateNavigationController controller = CreateController();

        CoordinateNavigationResponse response = controller.TurnTo(450);

        Assert.True(response.Started);
        Assert.StartsWith("Started turning to face 90°.", response.Message, StringComparison.Ordinal);
        Transform3D submitted = Assert.Single(_navigation.Submitted);
        Assert.Equal(position, submitted.Origin);
        AssertFacing(new Vector3(1, 0, 0), submitted);
        Assert.Empty(_environment.AppliedSpeeds);
    }

    /// <summary>
    /// Each outcome text names its kind and ends with the shared own-position sentence built from the realised
    /// terminal transform: position to 0.1 m and the whole-degree bearing of the final yaw (TR-15).
    /// </summary>
    [Fact]
    public void OutcomeText_EndsWithTheFinalOwnPositionAndHeading()
    {
        // Yaw −π/2 faces +X, bearing 90.
        _navigation.RealisedTransform = new NavigationOutcomeTransform(2.04f, 0f, -3.96f, -MathF.PI / 2f);
        CoordinateNavigationController controller = CreateController();
        Assert.True(controller.WalkTo(2, -4, null, null).Started);
        Assert.True(_navigation.CompleteActive());
        controller.Poll();

        string text = Assert.Single(_outcomes).Render(new NamedCharacter());

        Assert.Equal(
            "Your `walk_to` move arrived: you reached the destination. You are at (2.0, -4.0), facing 90°.",
            text);
    }

    /// <summary>
    /// Session teardown releases the controller: its active move is cancelled and every terminal record of its moves is
    /// acknowledged without publishing an outcome, while records of moves it did not start stay with their owner.
    /// </summary>
    [Fact]
    public void Release_CancelsTheActiveMoveAndAcknowledgesItsRecordsWithoutPublishing()
    {
        Assert.Equal(
            NavigationDestinationResult.Accepted,
            _navigation.SetDestination(new Transform3D(Basis.Identity, new Vector3(5, 0, 5))));
        CoordinateNavigationController controller = CreateController();
        Assert.True(controller.WalkTo(1, 1, null, null).Started);
        Assert.True(controller.WalkTo(2, 2, null, null).Started);
        NavigationTerminalOutcomeRecord foreign = _navigation.TerminalOutcomes[0];
        int published = _outcomes.Count;

        controller.Release();

        Assert.False(_navigation.CurrentStatus.HasActiveRequest);
        Assert.Equal(foreign, Assert.Single(_navigation.TerminalOutcomes));
        Assert.Equal(published, _outcomes.Count);
        Assert.Equal(0, controller.PendingOutcomeCount);
    }

    /// <summary>
    /// Repeated sessions on one navigation — each starting a move and ending before its outcome — never exhaust NAV-001's
    /// bounded outcome store (32 records) when each controller is released at teardown.
    /// </summary>
    [Fact]
    public void Release_AcrossManySessionRestarts_NeverCausesOutcomeBackpressure()
    {
        for (int session = 0; session < 40; session++)
        {
            CoordinateNavigationController controller = CreateController();
            CoordinateNavigationResponse response = controller.WalkTo(session % 5, 1, null, null);
            Assert.True(response.Started, $"Session {session}: {response.Message}");
            controller.Release();
        }

        Assert.Empty(_navigation.TerminalOutcomes);
        Assert.False(_navigation.CurrentStatus.HasActiveRequest);
    }

    private static void AssertFacing(Vector3 expected, Transform3D submitted)
    {
        Vector3 facing = -submitted.Basis.Z;
        Assert.True(
            facing.DistanceTo(expected) < 1e-4f,
            $"Expected facing {expected} but the submitted destination faces {facing}.");
    }

    private sealed class NamedCharacter : ICharacter
    {
        public string Id { get; set; } = "subject";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }
}
