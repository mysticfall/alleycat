using AlleyCat.Contact;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Contact;

/// <summary>
/// Resume coverage of the sweeper's finite-difference velocity sampling (INTR-005 step-8 review): a
/// <see cref="ContactSource" /> stopped through the hosting controller's enable pass-through keeps its
/// previous world-origin sample across the disabled period, so a tracked body that moved while sweeping was
/// stopped must not report the whole disabled-period displacement as one-tick velocity on resume — the
/// spike would cross the impact threshold and classify a spurious Impact at contact Begin.
/// </summary>
public sealed class ContactSourceResumeIntegrationTests
{
    private const float HandSize = 0.12f;
    private const float HandHalfExtent = HandSize * 0.5f;
    private const float ChestHalfDepth = 0.09f;
    private const float TouchZ = ChestHalfDepth + HandHalfExtent - 0.005f;
    private const float FarZ = 0.40f;

    // The disabled period spans real physics frames so the resume is a genuine gap in sweeping.
    private const int DisabledPhysicsFrames = 6;

    private static readonly Vector3 _fixtureOrigin = new(130.0f, 14.0f, 0.0f);

    /// <summary>
    /// Verifies the first classified event after a resume whose disabled period moved the tracked hand is
    /// the gentle Touch entry, never an Impact: the hand comes to rest in the gentle-touch pose while
    /// sweeping is stopped, so the first resumed sweep must re-base the origin sample and report no
    /// velocity instead of differencing the disabled-period displacement over one tick.
    /// </summary>
    [Headless]
    [Fact]
    public async Task SweeperResumedAfterTrackedBodyMoved_BeginsGentleTouchNotImpact()
    {
        SceneTree sceneTree = GetSceneTree();
        ResumeFixture fixture = await ResumeFixture.CreateAsync(sceneTree);

        try
        {
            // Stop the sweeper's physics processing — the exact call the hosting controller's
            // <c>Enabled</c> pass-through applies to its hosted sweepers.
            fixture.HandSource.SetPhysicsProcess(false);
            await ResumeFixture.HoldHandAsync(sceneTree, 1);
            Assert.False(fixture.HandSource.IsPhysicsProcessing(), "Stopping the sweeper should halt its physics processing.");

            // The tracked hand moves 0.255 m while sweeping is stopped — the player's real hand keeps
            // moving while sensing is stopped — and comes to rest in the gentle-touch pose.
            await fixture.MoveHandAsync(sceneTree, HandPose(TouchZ));
            await ResumeFixture.HoldHandAsync(sceneTree, DisabledPhysicsFrames);
            Assert.False(fixture.HandSource.IsPhysicsProcessing());

            // Resume: the hand is at rest in the touch pose, so the first resumed sweep opens a genuine
            // gentle touch. Hold through the Begin delivery and its classification.
            fixture.HandSource.SetPhysicsProcess(true);
            await ResumeFixture.HoldHandAsync(sceneTree, 8);

            RawContactEvent begin = Assert.Single(
                fixture.Monitor.GetRecentRawEvents(),
                rawEvent => rawEvent.Phase == RawContactPhase.Begin);
            ContactEvent first = Assert.Single(fixture.Events);
            Assert.Equal(ContactClass.Touch, first.Class);
            Assert.Null(first.PreviousClass);
            Assert.DoesNotContain(fixture.Events, contactEvent => contactEvent.Class == ContactClass.Impact);

            // Independent oracle: the Begin sample's relative velocity is the resumed hand's own rest
            // state, not the disabled-period displacement differenced over one tick.
            Assert.True(
                begin.Sample.RelativeVelocity.Length() <= 1.5f,
                $"The first resumed sweep must not report the disabled-period displacement as velocity; " +
                $"the Begin sample's relative velocity was {begin.Sample.RelativeVelocity}.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// The control run without a disabled-period move: after a stop/resume cycle during which the tracked
    /// hand never moved, a gentle approach still classifies the Touch entry normally.
    /// </summary>
    [Headless]
    [Fact]
    public async Task SweeperResumedWithoutTrackedBodyMove_ClassifiesGentleTouchNormally()
    {
        SceneTree sceneTree = GetSceneTree();
        ResumeFixture fixture = await ResumeFixture.CreateAsync(sceneTree);

        try
        {
            // Gentle pre-positioning to just outside contact range: 0.02 m steps at 60 Hz stay below the
            // impact threshold, matching the established gentle-approach pattern.
            await fixture.MoveHandAsync(sceneTree, HandPose(0.20f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.18f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.16f));

            // Stop and resume across a genuine disabled period during which the hand does not move.
            fixture.HandSource.SetPhysicsProcess(false);
            await ResumeFixture.HoldHandAsync(sceneTree, DisabledPhysicsFrames);
            Assert.False(fixture.HandSource.IsPhysicsProcessing());
            fixture.HandSource.SetPhysicsProcess(true);

            // Gentle touch: one 0.015 m step (~0.9 m/s at 60 Hz) into the touch pose, then rest.
            await fixture.MoveHandAsync(sceneTree, HandPose(TouchZ));
            await ResumeFixture.HoldHandAsync(sceneTree, 8);

            RawContactEvent begin = Assert.Single(
                fixture.Monitor.GetRecentRawEvents(),
                rawEvent => rawEvent.Phase == RawContactPhase.Begin);
            ContactEvent first = Assert.Single(fixture.Events);
            Assert.Equal(ContactClass.Touch, first.Class);
            Assert.Null(first.PreviousClass);
            Assert.True(
                begin.Sample.RelativeVelocity.Length() <= 1.5f,
                $"The gentle approach's Begin sample relative velocity should stay below the impact " +
                $"threshold; was {begin.Sample.RelativeVelocity}.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static Vector3 HandPose(float approachZ) => _fixtureOrigin + new Vector3(0.0f, 0.0f, approachZ);

    /// <summary>
    /// Builds the lean resume wiring: a static chest surface opted into receiving through the
    /// <c>contact_receiver</c> group with a <see cref="ContactMonitor" /> child, plus a kinematic hand body
    /// carrying a <see cref="ContactSource" /> sweeper — the hand-body parent is the sweeper's tracked
    /// body, and the sweeper's physics processing is stopped and resumed directly, mirroring the call the
    /// hosting controller's enable pass-through applies.
    /// </summary>
    private sealed class ResumeFixture(
        Node3D root,
        ContactMonitor monitor,
        AnimatableBody3D handBody,
        ContactSource handSource,
        List<ContactEvent> events)
    {
        public ContactMonitor Monitor { get; } = monitor;

        public ContactSource HandSource { get; } = handSource;

        public IReadOnlyList<ContactEvent> Events { get; } = events;

        public static async Task<ResumeFixture> CreateAsync(SceneTree sceneTree)
        {
            Node3D root = new()
            {
                Name = "ContactSourceResumeTestRoot",
                GlobalPosition = _fixtureOrigin,
            };
            Node parent = sceneTree.CurrentScene
                ?? throw new InvalidOperationException("Expected an active current scene for 3D integration test setup.");
            parent.AddChild(root);

            StaticBody3D chest = new()
            {
                Name = "Chest",
            };
            chest.AddChild(new CollisionShape3D
            {
                Name = "ChestShape",
                Shape = new BoxShape3D
                {
                    Size = new Vector3(0.40f, 0.44f, 0.18f),
                },
            });
            // World opt-in receiver (TR10): the same eligibility contract generated character body parts
            // join, hosted here by an authored static surface.
            chest.AddToGroup(ContactReceiverGroup.Name);
            root.AddChild(chest);

            ContactMonitor monitor = new()
            {
                Name = "ChestContactMonitor",
                OwnerName = "Sky",
            };
            chest.AddChild(monitor);

            AnimatableBody3D handBody = new()
            {
                Name = "PlayerRightHand",
                CollisionLayer = 8,
                CollisionMask = 0,
                // Direct transform writes must stick instead of being snapped back by physics interpolation.
                SyncToPhysics = false,
            };
            handBody.AddChild(new CollisionShape3D
            {
                Name = "HandShape",
                Shape = new BoxShape3D
                {
                    Size = new Vector3(HandSize, HandSize, HandSize),
                },
            });
            root.AddChild(handBody);
            handBody.GlobalPosition = HandPose(FarZ);

            ContactSource handSource = new()
            {
                Name = "RightHandContactSource",
                PartyDisplayName = "Right Hand",
                OwnerName = "Player",
            };
            handBody.AddChild(handSource);

            List<ContactEvent> events = [];
            monitor.ContactEventEmitted += events.Add;

            // The physics frames prime the sweeper's finite-difference velocity sampling with a still hand.
            await WaitForPhysicsFramesAsync(sceneTree, 5);

            return new ResumeFixture(root, monitor, handBody, handSource, events);
        }

        public async Task MoveHandAsync(SceneTree sceneTree, Vector3 worldPosition)
        {
            handBody.GlobalPosition = worldPosition;
            await WaitForPhysicsFramesAsync(sceneTree, 1);
        }

        public static async Task HoldHandAsync(SceneTree sceneTree, int physicsFrames)
            => await WaitForPhysicsFramesAsync(sceneTree, physicsFrames);

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(root) && root.IsInsideTree())
            {
                root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }
}
