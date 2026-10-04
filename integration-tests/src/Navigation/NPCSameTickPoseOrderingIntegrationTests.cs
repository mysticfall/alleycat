using System.Globalization;
using System.Text;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Navigation.NAV001ArrivalOutcomeIntegrationTests;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// IK-003 Technical Requirement 8 and Leg-Feet IK Contract Technical Requirements 7–9 on the real NPC in the navigation
/// test scene: on every physics tick the pose the lower-leg modifiers read must be the AnimationTree evaluation of that
/// tick, and the actor must already have moved by that same evaluation's root motion.
/// </summary>
/// <remarks>
/// The observation is engine-level only: <see cref="AnimationMixer.SignalName.MixerApplied"/> (tick and root motion of
/// each evaluation), the <see cref="SkeletonModifier3D.SignalName.ModificationProcessed"/> signal of CharacterIK's foot-provider
/// stage (tick on which foot targets are resolved and whether that tick's evaluation already happened), and <see cref="Skeleton3D.SignalName.SkeletonUpdated"/> (tick and actor
/// transform when the post-modifier pose is published). For every travelling tick the actor's planar step between two
/// published poses is compared with the planar root motion of the same tick's evaluation and of the previous tick's
/// evaluation; only the same-tick hypothesis may fit.
/// </remarks>
public sealed class NPCSameTickPoseOrderingIntegrationTests
{
    private const string EvidenceDirectory = "res://temp/ik003-foot-planting/ordering";
    private const float StepMatchToleranceMetres = 0.0002f;
    private const float DiscriminatingStepChangeMetres = 0.0005f;
    private const int MinimumDiscriminatingTicks = 10;
    private const int RequestTimeoutTicks = 1200;

    /// <summary>A 4 m straight walk exercises entry acceleration, sustained travel, and the exit's deceleration.</summary>
    [Fact]
    public async Task LongStraightWalk_ModifiersAndActorMovementUseSameTickEvaluation()
    {
        SceneTree sceneTree = GetSceneTree();
        int previousStepLimit = Engine.MaxPhysicsStepsPerFrame;
        Engine.MaxPhysicsStepsPerFrame = 1;
        Node3D root = Assert.IsType<Node3D>(LoadPackedScene(NavigationTestScenePath).Instantiate(), exactMatch: false);
        OrderingProbe? probe = null;
        try
        {
            Node3D actor = root.GetNode<Node3D>(ActorNodeName);
            PrepareActorBeforeTreeEntry(actor);
            sceneTree.Root.AddChild(root);
            INavigation navigation = await WaitForReadyNavigationAsync(sceneTree, root, actor);
            Skeleton3D skeleton = actor.GetNode<Skeleton3D>(SkeletonPath);
            AnimationTree tree = actor.GetNode<AnimationTree>("AnimationTree");
            Assert.Equal(Skeleton3D.ModifierCallbackModeProcessEnum.Physics, skeleton.ModifierCallbackModeProcess);

            await WaitForPhysicsFramesAsync(sceneTree, WarmUpPhysicsTicks);
            probe = new OrderingProbe(actor, tree, skeleton);
            probe.Attach();
            await WaitForPhysicsFramesAsync(sceneTree, 2);

            NavigationRequestIdentity identity = IssueRequest(
                navigation,
                new ArrivalRequest(new Vector3(0f, 0f, -4f), 0f),
                "ordering-long-straight",
                0);
            ulong issueTick = Engine.GetPhysicsFrames();
            NavigationTerminalOutcomeRecord? outcome = null;
            for (int tick = 0; tick < RequestTimeoutTicks && outcome is null; tick++)
            {
                await WaitForNextPhysicsFrameAsync(sceneTree);
                outcome = FindOutcome(navigation, identity);
            }

            ulong terminalTick = Engine.GetPhysicsFrames();
            await WaitForFramesAsync(sceneTree, 1);
            probe.Detach();
            Assert.True(outcome is not null, "SETUP: the walk reported no outcome; the ordering was not exercised.");

            // The terminal tick applies the temporary crude-stop snap, which is not root motion; exclude it.
            Report report = probe.Analyse(issueTick + 1, terminalTick - 1);
            string text = report.Describe();
            string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(EvidenceDirectory));
            File.WriteAllText(Path.Combine(directory, "long-straight.txt"), text);

            Assert.True(report.MissingModifierTicks == 0 && report.ModifiersBeforeEvaluation == 0, "Modifier ordering: " + text);
            Assert.True(report.DiscriminatingTicks >= MinimumDiscriminatingTicks, "SETUP: too few ticks with changing root motion: " + text);
            Assert.True(
                report.MaxSameTickError <= StepMatchToleranceMetres,
                "Actor movement does not use the same tick's root motion: " + text);
        }
        finally
        {
            probe?.Detach();
            Engine.MaxPhysicsStepsPerFrame = previousStepLimit;
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    private readonly record struct Report(
        int Ticks,
        int DiscriminatingTicks,
        float MaxSameTickError,
        float MaxPreviousTickError,
        int MissingModifierTicks,
        int ModifiersBeforeEvaluation,
        string Detail)
    {
        public string Describe() => FormattableString.Invariant(
            $"ticks={Ticks} discriminating={DiscriminatingTicks} max|step-rm(N)|={MaxSameTickError * 1000:0.####} mm max|step-rm(N-1)|={MaxPreviousTickError * 1000:0.####} mm missing_modifier_ticks={MissingModifierTicks} modifiers_before_evaluation={ModifiersBeforeEvaluation}\n{Detail}");
    }

    private sealed class OrderingProbe(Node3D actor, AnimationTree tree, Skeleton3D skeleton)
    {
        private readonly Dictionary<ulong, Vector3> _rootMotion = [];
        private readonly Dictionary<ulong, Vector3> _publishedActor = [];
        private readonly Dictionary<ulong, bool> _modifierAfterEvaluation = [];
        private SkeletonModifier3D? _providerStage;
        private bool _attached;

        public void Attach()
        {
            // CharacterIK's foot-provider stage is the modifier that resolves foot targets for the lower-leg solve.
            _providerStage = skeleton.GetNode<SkeletonModifier3D>("CharacterIKFootProviderStage");
            _providerStage.ModificationProcessed += OnModifier;
            tree.MixerApplied += OnMixerApplied;
            skeleton.SkeletonUpdated += OnSkeletonUpdated;
            _attached = true;
        }

        public void Detach()
        {
            if (!_attached)
            {
                return;
            }

            _attached = false;
            if (GodotObject.IsInstanceValid(tree))
            {
                tree.MixerApplied -= OnMixerApplied;
            }

            if (GodotObject.IsInstanceValid(skeleton))
            {
                skeleton.SkeletonUpdated -= OnSkeletonUpdated;
            }

            if (_providerStage is not null && GodotObject.IsInstanceValid(_providerStage))
            {
                _providerStage.ModificationProcessed -= OnModifier;
            }
        }

        public void OnModifier() => _modifierAfterEvaluation[Engine.GetPhysicsFrames()] = _rootMotion.ContainsKey(Engine.GetPhysicsFrames());

        public Report Analyse(ulong first, ulong last)
        {
            var detail = new StringBuilder("tick,actor_step_mm,rm_same_mm,rm_prev_mm\n");
            int ticks = 0;
            int discriminating = 0;
            int missing = 0;
            int early = 0;
            float maxSame = 0f;
            float maxPrevious = 0f;
            for (ulong tick = first; tick <= last; tick++)
            {
                if (!_modifierAfterEvaluation.TryGetValue(tick, out bool afterEvaluation))
                {
                    missing++;
                }
                else if (!afterEvaluation)
                {
                    early++;
                }

                if (!_publishedActor.TryGetValue(tick, out Vector3 current)
                    || !_publishedActor.TryGetValue(tick - 1, out Vector3 previous)
                    || !_rootMotion.TryGetValue(tick, out Vector3 same)
                    || !_rootMotion.TryGetValue(tick - 1, out Vector3 before))
                {
                    continue;
                }

                float step = Planar(current - previous).Length();
                float sameStep = Planar(same).Length();
                float previousStep = Planar(before).Length();
                if (sameStep < 1e-6f && previousStep < 1e-6f)
                {
                    continue;
                }

                ticks++;
                maxSame = Math.Max(maxSame, Math.Abs(step - sameStep));
                maxPrevious = Math.Max(maxPrevious, Math.Abs(step - previousStep));
                if (Math.Abs(sameStep - previousStep) >= DiscriminatingStepChangeMetres)
                {
                    discriminating++;
                }

                _ = detail.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{tick},{step * 1000:0.####},{sameStep * 1000:0.####},{previousStep * 1000:0.####}"));
            }

            return new Report(ticks, discriminating, maxSame, maxPrevious, missing, early, detail.ToString());
        }

        private void OnMixerApplied() => _rootMotion[Engine.GetPhysicsFrames()] = tree.GetRootMotionPosition();

        private void OnSkeletonUpdated() => _publishedActor[Engine.GetPhysicsFrames()] = actor.GlobalPosition;
    }
}
