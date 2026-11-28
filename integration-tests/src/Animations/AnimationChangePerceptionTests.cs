using AlleyCat.Animations;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using CharacterHub = AlleyCat.Character.Character;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Animations;

/// <summary>
/// ANIM-003-MIND-AC-003 observation contracts through the real Eyes → Mind → perception route, with a scripted variant
/// control raising transition-started events: NPC-only subjects (player and self excluded), triggers for every kind
/// only when the target differs from the last delivered entry, the first delivered event, focus gating that leaves the
/// last observed entry unchanged, interruptions, description-only rendered text, and subscription release.
/// </summary>
[Headless]
public sealed class AnimationChangePerceptionTests
{
    private static readonly AnimationEntry _sad = Entry("sad", AnimationCategory.Idle, "stands with slumped shoulders");
    private static readonly AnimationEntry _slump = Entry("slump", AnimationCategory.Idle, "stands slumped onto one hip");
    private static readonly AnimationEntry _feminine = Entry("feminine", AnimationCategory.Locomotion, "walks with an easy gait");
    private static readonly AnimationEntry _stroll = Entry("stroll", AnimationCategory.Locomotion, "strolls along unhurried");

    /// <summary>
    /// Every event kind produces an observation exactly when its target differs from the observer's last delivered
    /// entry for that subject and category; the first delivered event counts as a change; walking off in the same gait
    /// and stopping back into the same idle produce nothing. Each accepted observation reaches the event timeline and
    /// renders the description without the id or tags.
    /// </summary>
    [Fact]
    public async Task FocusedSubject_TriggersOnlyWhenTargetDiffersFromLastObserved()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        fixture.Focus(fixture.SubjectB);

        await fixture.RaiseAsync(fixture.ControlB, _sad, AnimationTransitionKind.IdleToIdle); // first delivered
        await fixture.RaiseAsync(fixture.ControlB, _feminine, AnimationTransitionKind.IdleToLocomotion); // first gait
        await fixture.RaiseAsync(fixture.ControlB, _sad, AnimationTransitionKind.LocomotionToIdle); // same idle: none
        await fixture.RaiseAsync(fixture.ControlB, _feminine, AnimationTransitionKind.IdleToLocomotion); // same gait: none
        await fixture.RaiseAsync(fixture.ControlB, _stroll, AnimationTransitionKind.LocomotionToLocomotion); // switch
        await fixture.RaiseAsync(fixture.ControlB, _slump, AnimationTransitionKind.LocomotionToIdle); // different idle
        await fixture.RaiseAsync(fixture.ControlB, _sad, AnimationTransitionKind.IdleToIdle); // idle→idle

        Assert.Equal(
            [
                ("test:b", AnimationCategory.Idle, _sad.Description),
                ("test:b", AnimationCategory.Locomotion, _feminine.Description),
                ("test:b", AnimationCategory.Locomotion, _stroll.Description),
                ("test:b", AnimationCategory.Idle, _slump.Description),
                ("test:b", AnimationCategory.Idle, _sad.Description),
            ],
            fixture.TimelineChanges().Select(static change => (change.SubjectId, change.Category, change.Description)));
        foreach (AgentObservation observation in fixture.Mind.Timeline)
        {
            string rendered = observation.Render(fixture.Observer);
            Assert.StartsWith("Saw that `test:b` now ", rendered, StringComparison.Ordinal);
            foreach (AnimationEntry entry in new[] { _sad, _slump, _feminine, _stroll })
            {
                Assert.DoesNotContain($"`{entry.ID}`", rendered, StringComparison.Ordinal);
                Assert.DoesNotContain(entry.Tags[0], rendered, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// A change while the subject is not the effective focus is not delivered and leaves the last observed entry
    /// unchanged; once refocused, an event is judged against the last delivered entry.
    /// </summary>
    [Fact]
    public async Task UnfocusedSubject_ProducesNothingAndLeavesLastObservedUnchanged()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        fixture.Focus(fixture.SubjectB);
        await fixture.RaiseAsync(fixture.ControlB, _slump, AnimationTransitionKind.IdleToIdle);

        fixture.Focus(fixture.SubjectC);
        await fixture.DrainAsync();
        Assert.Equal(0, fixture.ControlB.HandlerCount);
        await fixture.RaiseAsync(fixture.ControlB, _sad, AnimationTransitionKind.IdleToIdle); // unfocused
        Assert.Equal("slump", fixture.Perception.GetLastObserved("test:b", AnimationCategory.Idle));

        fixture.Focus(fixture.SubjectB);
        await fixture.DrainAsync();
        // Had the undelivered event updated the ledger, this would be suppressed as unchanged.
        await fixture.RaiseAsync(fixture.ControlB, _sad, AnimationTransitionKind.IdleToIdle);

        Assert.Equal(
            [_slump.Description, _sad.Description],
            fixture.TimelineChanges().Select(static change => change.Description));
    }

    /// <summary>
    /// An interrupted bridge raises a new event for its new target; it is observed only when it differs from the last
    /// observed entry, and the abandoned target produces nothing further.
    /// </summary>
    [Fact]
    public async Task Interruption_ObservedOnlyWhenNewTargetDiffers()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        fixture.Focus(fixture.SubjectB);
        await fixture.RaiseAsync(fixture.ControlB, _sad, AnimationTransitionKind.IdleToIdle);

        await fixture.RaiseAsync(fixture.ControlB, _slump, AnimationTransitionKind.IdleToIdle); // bridge to slump starts
        await fixture.RaiseAsync(fixture.ControlB, _sad, AnimationTransitionKind.IdleToIdle); // interrupted back to sad
        await fixture.RaiseAsync(fixture.ControlB, _sad, AnimationTransitionKind.IdleToIdle); // same target: nothing

        Assert.Equal(
            [_sad.Description, _slump.Description, _sad.Description],
            fixture.TimelineChanges().Select(static change => change.Description));
    }

    /// <summary>
    /// First look at a static, standing NPC produces one observation of its stance only (a standing NPC shows no gait);
    /// a later event with the same entry produces none.
    /// </summary>
    [Fact]
    public async Task FirstLook_StandingSubject_DescribesItsStanceOnce()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        fixture.ControlB.Stand(_slump, _feminine);

        fixture.Focus(fixture.SubjectB);
        await fixture.DrainAsync();
        await fixture.RaiseAsync(fixture.ControlB, _slump, AnimationTransitionKind.IdleToIdle);

        Assert.Equal(
            [("test:b", AnimationCategory.Idle, _slump.Description)],
            fixture.TimelineChanges().Select(static change => (change.SubjectId, change.Category, change.Description)));
        Assert.Equal("slump", fixture.Perception.GetLastObserved("test:b", AnimationCategory.Idle));
        Assert.Null(fixture.Perception.GetLastObserved("test:b", AnimationCategory.Locomotion));
    }

    /// <summary>A moving NPC at first look is described by its gait only (a moving NPC shows no stance).</summary>
    [Fact]
    public async Task FirstLook_MovingSubject_DescribesItsGaitOnly()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        fixture.ControlB.Move(_sad, _stroll);

        fixture.Focus(fixture.SubjectB);
        await fixture.DrainAsync();

        Assert.Equal(
            [("test:b", AnimationCategory.Locomotion, _stroll.Description)],
            fixture.TimelineChanges().Select(static change => (change.SubjectId, change.Category, change.Description)));
        Assert.Null(fixture.Perception.GetLastObserved("test:b", AnimationCategory.Idle));
    }

    /// <summary>
    /// Re-focus on an unchanged subject produces nothing; re-focus after a change missed while out of focus produces one
    /// observation with the new entry.
    /// </summary>
    [Fact]
    public async Task ReFocus_SilentWhenUnchanged_ReportsAMissedChange()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        fixture.ControlB.Stand(_sad, _feminine);
        fixture.Focus(fixture.SubjectB);
        await fixture.DrainAsync();

        fixture.Focus(fixture.SubjectC);
        await fixture.DrainAsync();
        fixture.Focus(fixture.SubjectB); // unchanged
        await fixture.DrainAsync();
        _ = Assert.Single(fixture.TimelineChanges());

        fixture.Focus(fixture.SubjectC);
        await fixture.DrainAsync();
        // The change happens out of focus: its event is not delivered.
        fixture.ControlB.Stand(_slump, _feminine);
        await fixture.RaiseAsync(fixture.ControlB, _slump, AnimationTransitionKind.IdleToIdle);
        Assert.Equal("sad", fixture.Perception.GetLastObserved("test:b", AnimationCategory.Idle));
        fixture.Focus(fixture.SubjectB);
        await fixture.DrainAsync();

        Assert.Equal(
            [_sad.Description, _slump.Description],
            fixture.TimelineChanges().Select(static change => change.Description));
    }

    /// <summary>The player is never a subject, a player observer observes nothing, and an NPC never observes itself.</summary>
    [Fact]
    public async Task PlayerAndSelf_AreExcluded()
    {
        await using (Fixture fixture = await Fixture.StartAsync())
        {
            fixture.SubjectB.AddToGroup(ISceneContext.PlayerGroupName);
            fixture.Focus(fixture.SubjectB);
            await fixture.RaiseAsync(fixture.ControlB, _slump, AnimationTransitionKind.IdleToIdle);
            Assert.Equal(0, fixture.ControlB.HandlerCount);
            Assert.Empty(fixture.TimelineChanges());
        }

        await using (Fixture fixture = await Fixture.StartAsync())
        {
            fixture.Observer.AddToGroup(ISceneContext.PlayerGroupName);
            fixture.Focus(fixture.SubjectB);
            await fixture.RaiseAsync(fixture.ControlB, _slump, AnimationTransitionKind.IdleToIdle);
            Assert.Equal(0, fixture.ControlB.HandlerCount);
            Assert.Empty(fixture.TimelineChanges());
        }

        await using (Fixture fixture = await Fixture.StartAsync())
        {
            fixture.Focus(fixture.Observer);
            await fixture.RaiseAsync(fixture.ObserverControl, _slump, AnimationTransitionKind.IdleToIdle);
            Assert.Equal(0, fixture.ObserverControl.HandlerCount);
            Assert.Empty(fixture.TimelineChanges());
        }
    }

    /// <summary>The subscription is released when the subject or the perception leaves the tree.</summary>
    [Fact]
    public async Task Subscription_ReleasedWhenSubjectOrPerceptionLeavesTheTree()
    {
        await using (Fixture fixture = await Fixture.StartAsync())
        {
            fixture.Focus(fixture.SubjectB);
            await fixture.DrainAsync();
            Assert.Same(fixture.SubjectB, fixture.Perception.ActiveSubject);
            Assert.Equal(1, fixture.ControlB.HandlerCount);
            fixture.SubjectB.GetParent().RemoveChild(fixture.SubjectB);
            Assert.Equal(0, fixture.ControlB.HandlerCount);
            Assert.False(fixture.Perception.IsSubscribed);
            fixture.Root.AddChild(fixture.SubjectB);
        }

        await using (Fixture fixture = await Fixture.StartAsync())
        {
            fixture.Focus(fixture.SubjectB);
            await fixture.DrainAsync();
            Assert.Equal(1, fixture.ControlB.HandlerCount);
            fixture.Mind.RemoveChild(fixture.Perception);
            Assert.Equal(0, fixture.ControlB.HandlerCount);
            fixture.Perception.QueueFree();
        }
    }

    private static AnimationEntry Entry(string id, AnimationCategory category, string description)
        => new(id, category, [$"{id}-tag"], description, "fixture", $"{id}.tres", $"res://{id}.tres");

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SceneTree _tree;
        private readonly CharacterHub _transitionSubject = new();
        private readonly Dictionary<IVisualSubject, ScriptedVariantControl> _controls = [];

        private Fixture(SceneTree tree)
        {
            _tree = tree;
            Root = new Node3D();
            Eyes = new EyesBehaviour { VisualSurveyIntervalSeconds = 30d };
            Observer = new ObserverCharacter(Eyes);
            Mind = new ObserverMind(Observer);
            Perception = new AnimationChangePerception
            {
                VariantControlResolver = subject => _controls.GetValueOrDefault(subject),
            };
            // The Mind requires a faculty for every percept type its Eyes declare.
            Mind.AddChild(new VisualSurveyPerception());
            Mind.AddChild(Perception);
            Observer.AddChild(Eyes);
            Observer.AddChild(Mind);
            ObserverCue = AddCue(Observer);
            Root.AddChild(Observer);
            SubjectB = new TestSubject("b");
            SubjectC = new TestSubject("c");
            CueB = AddCue(SubjectB);
            CueC = AddCue(SubjectC);
            Root.AddChild(SubjectB);
            Root.AddChild(SubjectC);
            ControlB = new ScriptedVariantControl();
            ObserverControl = new ScriptedVariantControl();
            _controls[SubjectB] = ControlB;
            _controls[SubjectC] = new ScriptedVariantControl();
            _controls[Observer] = ObserverControl;
            Mind.SetSceneContextLoaderForTesting(() => new ObserverSceneContext(Observer));
        }

        public Node3D Root
        {
            get;
        }

        public EyesBehaviour Eyes
        {
            get;
        }

        public ObserverCharacter Observer
        {
            get;
        }

        public ObserverMind Mind
        {
            get;
        }

        public AnimationChangePerception Perception
        {
            get;
        }

        public TestSubject SubjectB
        {
            get;
        }

        public TestSubject SubjectC
        {
            get;
        }

        public ScriptedVariantControl ControlB
        {
            get;
        }

        public ScriptedVariantControl ObserverControl
        {
            get;
        }

        private TestCue ObserverCue
        {
            get;
        }

        private TestCue CueB
        {
            get;
        }

        private TestCue CueC
        {
            get;
        }

        public static async Task<Fixture> StartAsync()
        {
            SceneTree tree = TestUtils.GetSceneTree();
            Fixture fixture = new(tree);
            (tree.CurrentScene ?? tree.Root).AddChild(fixture.Root);
            await TestUtils.WaitForFramesAsync(tree, 2);
            return fixture;
        }

        public void Focus(Node3D subject)
            => Eyes.SetLookTarget(ReferenceEquals(subject, SubjectB) ? CueB
                : ReferenceEquals(subject, SubjectC) ? CueC
                : ObserverCue);

        public Task DrainAsync() => Mind.DrainPerceptionsForTestingAsync();

        public async Task RaiseAsync(ScriptedVariantControl control, AnimationEntry entry, AnimationTransitionKind kind)
        {
            await DrainAsync();
            control.Raise(new AnimationTransition(_transitionSubject, entry.Category, entry, kind));
            await DrainAsync();
        }

        public IReadOnlyList<ObservedAnimationChange> TimelineChanges()
            => [.. Mind.Timeline.OfType<ObservedAnimationChange>()];

        public async ValueTask DisposeAsync()
        {
            Root.QueueFree();
            await TestUtils.WaitForFramesAsync(_tree, 2);
            _transitionSubject.Free();
        }

        private static TestCue AddCue(Node3D owner)
        {
            TestCue cue = new();
            owner.AddChild(cue);
            if (owner is TestSubject subject)
            {
                subject.VisualCues = [cue];
            }

            return cue;
        }
    }

    private sealed class ScriptedVariantControl : IAnimationVariantControl
    {
        private Action<AnimationTransition>? _handlers;

        public int HandlerCount => _handlers?.GetInvocationList().Length ?? 0;

        public AnimationCategory? DisplayedCategory
        {
            get; set;
        }

        public AnimationEntry? CurrentIdle
        {
            get; set;
        }

        public AnimationEntry? CurrentLocomotion
        {
            get; set;
        }

        /// <summary>Presents a settled native idle.</summary>
        public void Stand(AnimationEntry idle, AnimationEntry gait)
        {
            DisplayedCategory = AnimationCategory.Idle;
            CurrentIdle = idle;
            CurrentLocomotion = gait;
        }

        /// <summary>Presents locomotion.</summary>
        public void Move(AnimationEntry idle, AnimationEntry gait)
        {
            DisplayedCategory = AnimationCategory.Locomotion;
            CurrentIdle = idle;
            CurrentLocomotion = gait;
        }

        public event Action<AnimationTransition>? TransitionStarted
        {
            add => _handlers += value;
            remove => _handlers -= value;
        }

        public void Raise(AnimationTransition transition) => _handlers?.Invoke(transition);

        public bool SetIdlePreference(string? id) => false;

        public bool SetLocomotionPreference(string? id) => false;
    }

    private sealed partial class TestCue : VisualCue
    {
        public override ValueTask<string> Describe(ISceneContext scene, IHasVision observer)
            => ValueTask.FromResult("A figure.");
    }

    private sealed partial class TestSubject(string id) : Node3D, IVisualSubject
    {
        public string Id { get; set; } = id;

        public string Type => "test";

        public IReadOnlyList<VisualCue> VisualCues { get; set; } = [];
    }

    private sealed partial class ObserverCharacter(EyesBehaviour eyes) : Node3D, ICharacter
    {
        public string Id { get; set; } = "observer";

        public IReadOnlyList<IComponent> Components { get; } = [eyes];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
    }

    private sealed partial class ObserverMind(ICharacter owner) : MindBase
    {
        public IReadOnlyList<AgentObservation> Timeline => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    private sealed class ObserverSceneContext(ICharacter owner) : ISceneContext
    {
        public ICharacter Player => owner;

        public IReadOnlyCollection<ICharacter> Characters => [owner];

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId) => null;

        public IIdentifiable Resolve(string fullId) => throw new InvalidOperationException();
    }
}
