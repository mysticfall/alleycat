using System.Reflection;
using AlleyCat.Control;
using AlleyCat.Interaction;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using AlleyCat.XR.Mock;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Control;

/// <summary>
/// CTRL-002 and INTR-002 coverage: occupied-candidate exclusion regressions (INTR-001 TR18-TR19; INTR-002 TR3)
/// converted from the retired opt-in probe — a holder held by any hand is unavailable to a new candidate query
/// for both holder implementations and both input provenances, while the current owner's own operations stay
/// unaffected and <c>Grab</c> remains the atomic commit-time ownership guard — plus the controller pause/resume
/// reconciliation regression. Uses the existing explicit component fixture without changing its calibration,
/// offsets or thresholds.
/// </summary>
public sealed class HeldEligibilityPauseDiagnosticIntegrationTests
{
    private const string AnimationPath = "res://assets/characters/reference/female/animations/Grab-ball-40.tres";

    private static readonly (HandGrabInputSource Source, bool Rigid)[] _provenanceHolderCombos =
    [
        (HandGrabInputSource.Controller, false),
        (HandGrabInputSource.Optical, false),
        (HandGrabInputSource.Controller, true),
        (HandGrabInputSource.Optical, true),
    ];

    /// <summary>
    /// INTR-001 TR18 / INTR-002 TR3 regression: while the right hand holds the nearer object A, the left hand's
    /// holder-level query and the real selection surface exclude A, so the grab begins against the farther
    /// available object B and commits there without a freshness rejection, leaving the owner untouched.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HeldNearerObject_ExcludedFromSelection_SecondHandAcquiresFartherAvailableObject()
    {
        SceneTree tree = GetSceneTree();
        foreach ((HandGrabInputSource source, bool rigid) in _provenanceHolderCombos)
        {
            (Fixture fixture, Node3D a) = await CreateWithFirstHandHoldingAsync(tree, source, rigid);
            try
            {
                HandPoseBehaviour first = fixture.Get<HandPoseBehaviour>("RightHand");
                HandPoseBehaviour second = fixture.Get<HandPoseBehaviour>("LeftHand");
                Node3D b = fixture.AddHolder("B", 0.65f, rigid);
                Transform3D secondHand = second.HandTargetNode!.GlobalTransform;

                // Holder query boundary: the held nearer holder yields no candidate; the available farther one does.
                Assert.Null(((IGrabbable)a).GetGrabPoint(LimbSide.Left, secondHand));
                Assert.NotNull(((IGrabbable)b).GetGrabPoint(LimbSide.Left, secondHand));

                // The same exclusion applies through the deterministic selection surface a new grab would use.
                Assert.True(second.TryGetCurrentGrabCandidate(out GrabCandidateObservation? observation));
                Assert.Same(b, observation!.Grabbable);

                _ = second.BeginGrab(source);
                Assert.Equal(HandGrabLifecycleState.Pending, second.GrabLifecycle);
                Assert.Same(b, Pending(second));

                fixture.Place(LimbSide.Left, 0.65f);
                await WaitForFramesAsync(tree, 30);
                Assert.Same(b, second.CurrentGrabbed);
                Assert.True(IsGrabbed(b));
                Assert.Same(second.HandBoneAttachment, b.GetParent());
                Assert.False(second.LastPendingMovableGrabRejectedByFreshness);
                Assert.False(second.LastPendingGrabAbandonedForCandidateLoss);

                // The owner's held state is untouched by the second hand's approach and acquisition.
                Assert.Same(a, first.CurrentGrabbed);
                Assert.True(IsGrabbed(a));
                Assert.Same(first.HandBoneAttachment, a.GetParent());
            }
            finally
            {
                await fixture.DisposeAsync(tree);
            }
        }
    }

    /// <summary>
    /// Ownership-change regression: with the second hand mid-approach to the only available object, releasing
    /// the held nearer object makes it selectable again — the next selection refresh observes the new
    /// availability, and a fresh attempt acquires the released object.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HeldObjectReleasedMidApproach_BecomesSelectableAgainOnNextRefresh()
    {
        SceneTree tree = GetSceneTree();
        foreach ((HandGrabInputSource source, bool rigid) in _provenanceHolderCombos)
        {
            (Fixture fixture, Node3D a) = await CreateWithFirstHandHoldingAsync(tree, source, rigid);
            try
            {
                HandPoseBehaviour first = fixture.Get<HandPoseBehaviour>("RightHand");
                HandPoseBehaviour second = fixture.Get<HandPoseBehaviour>("LeftHand");
                Node3D b = fixture.AddHolder("B", 0.65f, rigid);

                // The second hand approaches the only available object while A stays held.
                _ = second.BeginGrab(source);
                Assert.Equal(HandGrabLifecycleState.Pending, second.GrabLifecycle);
                Assert.Same(b, Pending(second));
                Assert.False(second.LastPendingGrabAbandonedForCandidateLoss);

                // Ownership changes mid-approach: the owner releases A.
                first.Release();
                Assert.False(IsGrabbed(a));
                Assert.Null(first.CurrentGrabbed);

                // The next selection refresh observes the new availability: A is the nearer selectable candidate.
                Assert.True(second.TryGetCurrentGrabCandidate(out GrabCandidateObservation? observation));
                Assert.Same(a, observation!.Grabbable);

                // A fresh attempt after cancelling the stale approach acquires the released object.
                Assert.True(second.CancelPendingGrab());
                _ = second.BeginGrab(source);
                Assert.Equal(HandGrabLifecycleState.Pending, second.GrabLifecycle);
                Assert.Same(a, Pending(second));
                fixture.Place(LimbSide.Left, 0.55f);
                await WaitForFramesAsync(tree, 30);
                Assert.Same(a, second.CurrentGrabbed);
                Assert.True(IsGrabbed(a));
                Assert.Same(second.HandBoneAttachment, a.GetParent());
            }
            finally
            {
                await fixture.DisposeAsync(tree);
            }
        }
    }

    /// <summary>
    /// Owner-isolation regression: the current owner's pending refresh, held state, release, and re-acquisition
    /// after release behave exactly as before the availability rule — the held object stays excluded from new
    /// candidate queries throughout.
    /// </summary>
    [Headless]
    [Fact]
    public async Task OwnerHeldOperations_ReleaseAndReacquisition_UnaffectedByAvailabilityExclusion()
    {
        SceneTree tree = GetSceneTree();
        foreach ((HandGrabInputSource source, bool rigid) in _provenanceHolderCombos)
        {
            (Fixture fixture, Node3D a) = await CreateWithFirstHandHoldingAsync(tree, source, rigid);
            try
            {
                HandPoseBehaviour first = fixture.Get<HandPoseBehaviour>("RightHand");
                HandPoseBehaviour second = fixture.Get<HandPoseBehaviour>("LeftHand");

                // The held state persists across frames while the object is excluded from new queries.
                await WaitForFramesAsync(tree, 10);
                Assert.Equal(HandGrabLifecycleState.Held, first.GrabLifecycle);
                Assert.Same(a, first.CurrentGrabbed);
                Assert.True(((IGrabbable)a).IsGrabbed);
                Assert.Null(((IGrabbable)a).GetGrabPoint(LimbSide.Left, second.HandTargetNode!.GlobalTransform));

                first.Release();
                Assert.Equal(HandGrabLifecycleState.None, first.GrabLifecycle);
                Assert.Null(first.CurrentGrabbed);
                Assert.False(IsGrabbed(a));

                // Same-hand re-acquisition after release selects and commits exactly as before.
                _ = first.BeginGrab(source);
                Assert.Equal(HandGrabLifecycleState.Pending, first.GrabLifecycle);
                Assert.Same(a, Pending(first));
                fixture.Place(LimbSide.Right, 0.55f);
                await WaitForFramesAsync(tree, 30);
                Assert.Equal(HandGrabLifecycleState.Held, first.GrabLifecycle);
                Assert.Same(a, first.CurrentGrabbed);
                Assert.True(IsGrabbed(a));
            }
            finally
            {
                await fixture.DisposeAsync(tree);
            }
        }
    }

    /// <summary>
    /// Commit-race regression (INTR-001 TR19): availability filtering is not a reservation — both hands may go
    /// pending on the same free object, only one commit succeeds, and the loser's next refresh observes the new
    /// unavailability and abandons. A genuine double commit of two query-time candidates against the atomic
    /// ownership guard is rejected without granting double ownership.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CommitRace_BothHandsPendingSameFreeObject_ProducesSingleOwnerAndAtomicDoubleCommitRejection()
    {
        SceneTree tree = GetSceneTree();
        foreach ((HandGrabInputSource source, bool rigid) in _provenanceHolderCombos)
        {
            Fixture fixture = await Fixture.CreateAsync(tree);
            try
            {
                if (source == HandGrabInputSource.Optical)
                {
                    _ = fixture.Get<MockXRRuntimeNode>("Runtime").SetHandObservations(
                        XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
                }

                HandPoseBehaviour first = fixture.Get<HandPoseBehaviour>("RightHand");
                HandPoseBehaviour second = fixture.Get<HandPoseBehaviour>("LeftHand");
                Node3D a = fixture.AddHolder("A", 0.55f, rigid);
                fixture.Place(LimbSide.Left, 0.50f);

                // No reservation: both hands can approach the same free object.
                _ = first.BeginGrab(source);
                _ = second.BeginGrab(source);
                Assert.Equal(HandGrabLifecycleState.Pending, first.GrabLifecycle);
                Assert.Equal(HandGrabLifecycleState.Pending, second.GrabLifecycle);
                Assert.Same(a, Pending(first));
                Assert.Same(a, Pending(second));

                // One hand commits; the loser's next refresh observes the occupancy and abandons for candidate loss.
                fixture.Place(LimbSide.Left, 0.55f);
                await WaitForFramesAsync(tree, 30);
                Assert.Same(a, second.CurrentGrabbed);
                Assert.True(IsGrabbed(a));
                Assert.Same(second.HandBoneAttachment, a.GetParent());
                Assert.Null(first.CurrentGrabbed);
                Assert.Equal(HandGrabLifecycleState.None, first.GrabLifecycle);
                Assert.True(first.LastPendingGrabAbandonedForCandidateLoss);
                Assert.False(first.LastPendingMovableGrabRejectedByFreshness);

                // Genuine double commit at the atomic boundary: both candidates were queried while the holder was
                // free; the second commit is rejected and ownership stays singular.
                Node3D b = fixture.AddHolder("B", 0.65f, rigid);
                GrabPointCandidate winner = ((IGrabbable)b).GetGrabPoint(LimbSide.Right, first.HandTargetNode!.GlobalTransform)!;
                GrabPointCandidate loser = ((IGrabbable)b).GetGrabPoint(LimbSide.Left, second.HandTargetNode!.GlobalTransform)!;
                Assert.NotNull(winner);
                Assert.NotNull(loser);
                Assert.True(((IGrabbable)b).Grab(winner));
                Assert.False(((IGrabbable)b).Grab(loser));
                Assert.True(IsGrabbed(b));
            }
            finally
            {
                await fixture.DisposeAsync(tree);
            }
        }
    }

    /// <summary>
    /// Positive controls: an unheld nearer object remains the selected candidate, and moving it out of reach
    /// selects the farther available object, which is then acquired.
    /// </summary>
    [Headless]
    [Fact]
    public async Task UnheldNearerObjectStaysSelectable_OutOfReachFallback_SelectsAndHoldsFartherAvailableObject()
    {
        SceneTree tree = GetSceneTree();
        foreach ((HandGrabInputSource source, bool rigid) in _provenanceHolderCombos)
        {
            Fixture fixture = await Fixture.CreateAsync(tree);
            try
            {
                if (source == HandGrabInputSource.Optical)
                {
                    _ = fixture.Get<MockXRRuntimeNode>("Runtime").SetHandObservations(
                        XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
                }

                HandPoseBehaviour second = fixture.Get<HandPoseBehaviour>("LeftHand");
                Node3D a = fixture.AddHolder("A", 0.55f, rigid);
                Node3D b = fixture.AddHolder("B", 0.65f, rigid);
                fixture.Place(LimbSide.Left, 0.50f);

                // Unheld, the nearer object is still the selected candidate.
                Assert.True(second.TryGetCurrentGrabCandidate(out GrabCandidateObservation? observation));
                Assert.Same(a, observation!.Grabbable);
                _ = second.BeginGrab(source);
                Assert.Same(a, Pending(second));
                Assert.True(second.CancelPendingGrab());
                Assert.Equal(HandGrabLifecycleState.None, second.GrabLifecycle);

                // With the nearer object out of reach, the farther available object is selected and acquired.
                Fixture.Move(a, 2f);
                _ = second.BeginGrab(source);
                Assert.Same(b, Pending(second));
                fixture.Place(LimbSide.Left, 0.65f);
                await WaitForFramesAsync(tree, 30);
                Assert.Same(b, second.CurrentGrabbed);
                Assert.True(IsGrabbed(b));
                Assert.False(IsGrabbed(a));
            }
            finally
            {
                await fixture.DisposeAsync(tree);
            }
        }
    }

    /// <summary>
    /// CTRL-002 UR9, TR19-TR22 regression (converted from the retired opt-in probe): production mock relay
    /// events and <see cref="Game.Paused" /> drive the real <c>PlayerController</c> and <c>HandPoseBehaviour</c>.
    /// While paused, valid digital/analogue grip state is observed without issuing grab actions; on resume,
    /// reconciliation acts exactly once — an open grip cancels Pending or releases Held, a held grip and an
    /// unobserved grip preserve state, an invalid analogue value is not treated as open, a complete paused
    /// press/release cycle creates no grab, and an unpaused release still works.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ControllerPauseResume_ReconcilesValidGripStateExactlyOnce()
    {
        SceneTree tree = GetSceneTree();

        List<(bool Analogue, bool Held, string Scenario)> cases = [];
        foreach (bool analogue in new[] { false, true })
        {
            foreach (bool held in new[] { false, true })
            {
                cases.Add((analogue, held, "release-paused"));
                cases.Add((analogue, held, "hold-paused"));
                if (analogue)
                {
                    cases.Add((analogue, held, "invalid-paused"));
                }
                else
                {
                    cases.Add((analogue, held, "press-paused"));
                }

                if (!held)
                {
                    cases.Add((analogue, held, "cycle-paused"));
                }

                if (!analogue)
                {
                    cases.Add((analogue, held, "unpaused-release"));
                }
            }
        }

        foreach ((bool analogue, bool held, string scenario) in cases)
        {
            Fixture fixture = await Fixture.CreateAsync(tree);
            try
            {
                HandPoseBehaviour hand = fixture.Get<HandPoseBehaviour>("RightHand");
                Game root = fixture.Get<Game>("Root");
                var locomotion = (Node)Activator.CreateInstance(typeof(HandGrabInputCoordinatorIntegrationTests)
                    .GetNestedType("CountingLocomotion", BindingFlags.NonPublic)!)!;
                root.AddChild(locomotion);
                PlayerController controller = new()
                {
                    HandHolderNode = hand.GetParent(),
                    LocomotionNode = locomotion
                };
                root.AddChild(controller);
                _ = fixture.Get<XRManager>("XRManager").EmitSignal(XRManager.SignalName.Initialised, true);
                await WaitForFramesAsync(tree, 4);
                Assert.True((bool)typeof(PlayerController).GetField("_isBound", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(controller)!, $"{analogue}/{held}/{scenario}: expected the controller to bind.");
                var relay = (MockXRHandControllerNode)fixture.Get<MockXRRuntimeNode>("Runtime").RightHandController;

                void Emit(bool closed)
                {
                    if (analogue)
                    {
                        relay.TriggerActionFloatInputChanged("grip", closed ? 1f : 0f);
                    }
                    else if (closed)
                    {
                        relay.TriggerActionButtonPressed("grip_click");
                    }
                    else
                    {
                        relay.TriggerActionButtonReleased("grip_click");
                    }
                }

                string Case(string observation)
                {
                    return $"{(analogue ? "analogue" : "digital")}/{(held ? "held" : "pending")}/{scenario}: {observation}";
                }

                if (scenario != "cycle-paused")
                {
                    _ = fixture.Add("A", 0.55f);
                    Emit(true);
                    Assert.Equal(HandGrabLifecycleState.Pending, hand.GrabLifecycle);
                    if (held)
                    {
                        fixture.Place(LimbSide.Right, 0.55f);
                        await WaitForFramesAsync(tree, 30);
                        Assert.Equal(HandGrabLifecycleState.Held, hand.GrabLifecycle);
                    }
                }

                bool paused = scenario != "unpaused-release";
                root.Paused = paused;
                await WaitForFramesAsync(tree, 2);

                switch (scenario)
                {
                    case "release-paused":
                        Emit(false);
                        break;

                    case "hold-paused":
                        if (analogue)
                        {
                            Emit(true);
                        }

                        break;

                    case "invalid-paused":
                        relay.TriggerActionFloatInputChanged("grip", float.NaN);
                        break;

                    case "press-paused":
                        Emit(true);
                        break;

                    case "cycle-paused":
                        Emit(true);
                        Emit(false);
                        break;

                    case "unpaused-release":
                        Emit(false);
                        break;

                    default:
                        Assert.Fail($"Unknown pause scenario '{scenario}'.");
                        break;
                }

                if (paused)
                {
                    // Pause suppression: whatever was observed, no grab action may fire while paused.
                    await WaitForFramesAsync(tree, 4);
                    Assert.Equal(
                        scenario == "cycle-paused" ? HandGrabLifecycleState.None : held ? HandGrabLifecycleState.Held : HandGrabLifecycleState.Pending,
                        hand.GrabLifecycle);
                    root.Paused = false;
                }

                await WaitForFramesAsync(tree, 12);
                HandGrabLifecycleState expected = scenario is "release-paused" or "cycle-paused" or "unpaused-release"
                    ? HandGrabLifecycleState.None
                    : held ? HandGrabLifecycleState.Held : HandGrabLifecycleState.Pending;
                Assert.Equal(expected, hand.GrabLifecycle);

                // Normal operation after resume: a fresh press creates a real grab (never a replayed paused
                // press) and a fresh release ends it, for digital and analogue latch semantics alike.
                if (scenario == "cycle-paused")
                {
                    _ = fixture.Add("A", 0.55f);
                }

                Emit(true);
                await WaitForFramesAsync(tree, 4);
                Assert.True(hand.GrabLifecycle != HandGrabLifecycleState.None, Case("post-resume press must begin a grab"));
                Emit(false);
                await WaitForFramesAsync(tree, 4);
                Assert.Equal(HandGrabLifecycleState.None, hand.GrabLifecycle);
            }
            finally
            {
                tree.Paused = false;
                await fixture.DisposeAsync(tree);
            }
        }
    }

    private static IGrabbable? Pending(HandPoseBehaviour hand) => (IGrabbable?)typeof(HandGrabInputCoordinatorIntegrationTests)
        .GetMethod("PendingGrabbable", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [hand]);

    private static bool IsGrabbed(Node3D holder) => ((IGrabbable)holder).IsGrabbed;

    /// <summary>
    /// Creates the fixture with the right hand pending on and then holding the nearer object A at 0.55 m, with the
    /// left hand placed at 0.50 m — the exact calibration the retired probe used, unchanged.
    /// </summary>
    private static async Task<(Fixture Fixture, Node3D Held)> CreateWithFirstHandHoldingAsync(
        SceneTree tree,
        HandGrabInputSource source,
        bool rigid)
    {
        Fixture fixture = await Fixture.CreateAsync(tree);
        if (source == HandGrabInputSource.Optical)
        {
            _ = fixture.Get<MockXRRuntimeNode>("Runtime").SetHandObservations(
                XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
        }

        Node3D held = fixture.AddHolder("A", 0.55f, rigid);
        fixture.Place(LimbSide.Left, 0.50f);
        HandPoseBehaviour first = fixture.Get<HandPoseBehaviour>("RightHand");
        _ = first.BeginGrab(source);
        Assert.Equal(HandGrabLifecycleState.Pending, first.GrabLifecycle);
        fixture.Place(LimbSide.Right, 0.55f);
        await WaitForFramesAsync(tree, 30);
        Assert.Same(held, first.CurrentGrabbed);
        Assert.True(IsGrabbed(held));
        return (fixture, held);
    }

    // Reflection reuses an existing private component fixture without editing prior diagnostics or exposing runtime hooks.
    private sealed class Fixture(object inner)
    {
        private static readonly Type _owner = typeof(HandGrabInputCoordinatorIntegrationTests);
        public T Get<T>(string name) => (T)inner.GetType().GetProperty(name)!.GetValue(inner)!;
        public GrabbableNode Add(string name, float x) => (GrabbableNode)inner.GetType().GetMethod("AddGrabbable")!
            .Invoke(inner, [name, new Vector3(x, 0, 0), AnimationPath])!;
        public Node3D AddHolder(string name, float x, bool rigid)
        {
            GrabbableNode node = Add(name, x);
            if (!rigid)
            {
                return node;
            }

            Node point = node.GetChild(0);
            node.RemoveChild(point);
            node.Free();
            GrabbableRigidBody3D body = new()
            {
                Name = name,
                Position = new Vector3(x, 0, 0),
                GravityScale = 0f
            };
            body.AddChild(point);
            Get<Game>("Root").AddChild(body);
            return body;
        }

        public static void Move(Node3D node, float x)
        {
            node.Position = new Vector3(x, 0, 0);
            Node point = node.GetChild(0);
            point.GetType().GetProperty("TargetOrigin")!.SetValue(point, node.Position);
            point.GetType().GetProperty("HandTargetOrigin")!.SetValue(point, node.Position);
        }
        public void Place(LimbSide side, float x)
        {
            Transform3D world = new(Basis.Identity, new Vector3(x, 0, 0));
            Get<Node3D>(side == LimbSide.Right ? "RightHandTarget" : "LeftHandTarget").GlobalTransform = world;
            _ = _owner.GetMethod("SetHandBoneWorldTransform", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [Get<Skeleton3D>("HandSkeleton"), side == LimbSide.Right ? "RightHand" : "LeftHand", world]);
        }

        public Task DisposeAsync(SceneTree tree) => (Task)inner.GetType().GetMethod("DisposeAsync")!.Invoke(inner, [tree])!;
        public static async Task<Fixture> CreateAsync(SceneTree tree)
        {
            Type type = _owner.GetNestedType("CoordinatorFixture", BindingFlags.NonPublic)!;
            var task = (Task)type.GetMethod("CreateAsync")!.Invoke(null, [tree])!;
            await task;
            Fixture fixture = new(task.GetType().GetProperty("Result")!.GetValue(task)!);
            // This is an ownership/input-routing fixture, not a scheduled optical recognition probe.
            fixture.Get<HandGrabInputCoordinator>("Coordinator").ProcessMode = Node.ProcessModeEnum.Disabled;
            _ = fixture.Get<MockXRRuntimeNode>("Runtime").SetHandObservations(
                XRHandSourceObservation.Controller, XRHandSourceObservation.Controller);
            await WaitForFramesAsync(tree, 2);
            return fixture;
        }
    }
}
