using System.Text.Json;
using AlleyCat.Control.Locomotion;
using AlleyCat.IK;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Navigation;
using AlleyCat.Rigging;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>Content-independent behavioural navigation coverage using the declared Ember fixture package.</summary>
public sealed class NPCNavigationIntegrationTests
{
    /// <inheritdoc/>
    [Fact]
    public async Task SyntheticRuntime_ActivatesAgainstDeclaredCatalogueAndBareNavigationMap()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(SyntheticRuntime_ActivatesAgainstDeclaredCatalogueAndBareNavigationMap));
        SyntheticNavigationRuntime runtime = fixture.CreateNavigationRuntime();
        SceneTree sceneTree = GetSceneTree();
        Rid map = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetActive(map, true);
        runtime.Navigation.SetNavigationMap(map);
        sceneTree.Root.AddChild(runtime.Root);
        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            runtime.Navigation.ActivateRuntimeBinding();

            Assert.True(runtime.Navigation.IsRuntimeBindingActive);
            Assert.Equal(map, runtime.Navigation.GetNavigationMap());
            Assert.Equal(new NodePath("%GeneralSkeleton:Root"), runtime.AnimationTree.RootMotionTrack);
            Assert.Equal(AnimationMixer.AnimationCallbackModeProcess.Physics, runtime.AnimationTree.CallbackModeProcess);
            Assert.All(runtime.Manifest.Clips, clip => Assert.True(runtime.Player.HasAnimation($"locomotion/{clip.Identity}")));
        }
        finally
        {
            runtime.Root.QueueFree();
            NavigationServer3D.FreeRid(map);
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <inheritdoc/>
    [Fact]
    public async Task DeclaredRoles_RouteEntryExitCrossfadeAndFiniteCompletionWithoutRestart()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(DeclaredRoles_RouteEntryExitCrossfadeAndFiniteCompletionWithoutRestart));
        SyntheticNavigationRuntime runtime = fixture.CreateNavigationRuntime();
        SceneTree sceneTree = GetSceneTree();
        sceneTree.Root.AddChild(runtime.Root);
        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            AnimationNodeBlendTree root = Assert.IsType<AnimationNodeBlendTree>(runtime.AnimationTree.TreeRoot, exactMatch: false);
            AnimationNodeStateMachine states = Assert.IsType<AnimationNodeStateMachine>(root.GetNode("States"), exactMatch: false);
            SyntheticNavigationClipDeclaration entry = runtime.Manifest["i2f_l"];
            SyntheticNavigationClipDeclaration exit = runtime.Manifest["f2i_r"];
            Assert.Equal($"locomotion/{entry.Identity}", Assert.IsType<AnimationNodeAnimation>(states.GetNode("NPCI2FL"), exactMatch: false).Animation);
            Assert.Equal($"locomotion/{exit.Identity}", Assert.IsType<AnimationNodeAnimation>(states.GetNode("NPCF2IR"), exactMatch: false).Animation);

            AnimationNodeStateMachineTransition transition = FindTransition(states, "NPCTravel", "NPCF2IR");
            Assert.True(transition.XfadeTime > 0f);
            Assert.Equal(AnimationNodeStateMachineTransition.AdvanceModeEnum.Enabled, transition.AdvanceMode);

            Animation exitAnimation = runtime.Player.GetAnimation($"locomotion/{exit.Identity}");
            int rootTrack = exitAnimation.FindTrack(new NodePath("%GeneralSkeleton:Root"), Animation.TrackType.Position3D);
            Assert.True(rootTrack >= 0);
            Assert.Equal(Vector3.Zero, exitAnimation.PositionTrackInterpolate(rootTrack, 0d));
            Assert.Equal(exit.SkeletonLocalRootResponse, exitAnimation.PositionTrackInterpolate(rootTrack, exit.DurationSeconds));

            Assert.Equal(Animation.LoopModeEnum.None, exitAnimation.LoopMode);
            Assert.Equal(2, exitAnimation.TrackGetKeyCount(rootTrack));
            Assert.Equal(exit.DurationSeconds, exitAnimation.TrackGetKeyTime(rootTrack, 1));
        }
        finally
        {
            runtime.Root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>Guards the test fixture's reference-rig coordinate contract, including reflected-axis mutations.</summary>
    [Fact]
    public async Task SyntheticNavigationFixture_ReferenceRigParityRejectsIdentityReferenceMutation()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(SyntheticNavigationFixture_ReferenceRigParityRejectsIdentityReferenceMutation));
        SyntheticNavigationRuntime runtime = fixture.CreateNavigationRuntime();
        SceneTree sceneTree = GetSceneTree();
        sceneTree.Root.AddChild(runtime.Root);
        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            var catalogue = StandingLocomotionCatalogue.Parse(
                File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath)),
                fixture.NavigationCataloguePath);

            AssertDirection(runtime.RootMotionReference.GlobalBasis * Vector3.Back, Vector3.Forward, "reference avatar forward");
            AssertDirection(runtime.RootMotionReference.GlobalBasis * Vector3.Left, Vector3.Right, "reference avatar right");
            Assert.Equal(1f, runtime.Skeleton.MotionScale);

            foreach (SyntheticNavigationClipDeclaration declaration in runtime.Manifest.Clips)
            {
                StandingLocomotionClip catalogueClip = catalogue[declaration.Capability];
                Animation animation = runtime.Player.GetAnimation($"locomotion/{declaration.Identity}");
                int rotationTrack = animation.FindTrack(new NodePath("%GeneralSkeleton:Root"), Animation.TrackType.Rotation3D);
                Vector3 evaluatedWorld = runtime.RootMotionReference.GlobalBasis
                    * (declaration.SkeletonLocalRootResponse * runtime.Skeleton.MotionScale);
                Vector3 catalogueWorld = runtime.Actor.GlobalBasis * SemanticResponseToActorLocal(declaration.SemanticCatalogueRootResponse);
                AssertVectorClose(catalogueWorld, evaluatedWorld, declaration.Role);
                Assert.Equal(declaration.SkeletonLocalRootResponse, catalogueClip.EndpointRootDisplacement);
                Assert.Equal(declaration.YawRadians, catalogueClip.EndpointYaw, 6);
                Assert.True(rotationTrack >= 0, $"Expected a Root yaw track for {declaration.Role}.");
                Quaternion evaluatedYaw = new NPCNavigationSemanticFrame(
                    runtime.Actor.GlobalBasis,
                    runtime.RootMotionReference.GlobalBasis)
                    .EvaluatedRootRotationToActorLocal(animation.RotationTrackInterpolate(rotationTrack, declaration.DurationSeconds));
                Assert.Equal(declaration.YawRadians, evaluatedYaw.GetEuler().Y, 6);
            }

            AssertDirection(EvaluatedWorldRoot(runtime, runtime.Manifest["i2f_l"]), Vector3.Forward, "forward entry");
            AssertDirection(EvaluatedWorldRoot(runtime, runtime.Manifest["wf"]), Vector3.Forward, "forward travel");
            AssertDirection(EvaluatedWorldRoot(runtime, runtime.Manifest["f2i_r"]), Vector3.Forward, "forward exit");
            AssertDirection(EvaluatedWorldRoot(runtime, runtime.Manifest["sl"]), Vector3.Left, "left lateral");
            AssertDirection(EvaluatedWorldRoot(runtime, runtime.Manifest["sr"]), Vector3.Right, "right lateral");

            // Targeted mutation oracle: treating the reference rig as identity reflects both planar axes and must
            // disagree with the declared semantic catalogue response on both longitudinal and lateral samples.
            Assert.True(
                EvaluatedWorldRoot(runtime, runtime.Manifest["wf"])
                    .DistanceTo(runtime.Actor.GlobalBasis * runtime.Manifest["wf"].SkeletonLocalRootResponse) > 0.1f,
                "Identity-reference mutation was not rejected on the longitudinal axis.");
            Assert.True(
                EvaluatedWorldRoot(runtime, runtime.Manifest["sl"])
                    .DistanceTo(runtime.Actor.GlobalBasis * runtime.Manifest["sl"].SkeletonLocalRootResponse) > 0.1f,
                "Identity-reference mutation was not rejected on the lateral axis.");
        }
        finally
        {
            runtime.Root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void DeclaredObliqueTurnSupportAndForecastInputs_AreDerivedFromFixtureManifest()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(DeclaredObliqueTurnSupportAndForecastInputs_AreDerivedFromFixtureManifest));
        SyntheticNavigationManifest manifest = fixture.Declaration.NavigationManifest;
        SyntheticNavigationClipDeclaration leftArc = manifest["al_l"];
        SyntheticNavigationClipDeclaration rightArc = manifest["ar_r"];
        SyntheticNavigationClipDeclaration pivot = manifest["pl45"];

        Assert.True(leftArc.SkeletonLocalRootResponse.X > 0f && leftArc.SkeletonLocalRootResponse.Z > 0f);
        Assert.True(rightArc.SkeletonLocalRootResponse.X < 0f && rightArc.SkeletonLocalRootResponse.Z > 0f);
        Assert.True(pivot.SkeletonLocalRootResponse.IsZeroApprox());
        Assert.True(pivot.YawRadians > 0f);
        Assert.Equal("ember_token_07", manifest["i2f_l"].Identity);

        var route = NPCNavigationRoutePlan.Compile(
            [Vector3.Zero, SemanticResponseToActorLocal(leftArc.SemanticCatalogueRootResponse), SemanticResponseToActorLocal(leftArc.SemanticCatalogueRootResponse + rightArc.SemanticCatalogueRootResponse)],
            0,
            new Transform3D(new Basis(Vector3.Up, pivot.YawRadians), SemanticResponseToActorLocal(leftArc.SemanticCatalogueRootResponse + rightArc.SemanticCatalogueRootResponse)),
            7,
            3,
            authoredExitDistance: 0.2f);
        _ = Assert.Single(route.Corners);
        Assert.True(float.IsFinite(route.Corners[0].SignedAngle));
        Assert.Equal(7, route.DestinationRequestGeneration);
        Assert.Equal(3, route.RouteRevision);
    }

    /// <inheritdoc/>
    [Fact]
    public void InactiveNavigation_PerformsNoPhysicalStep()
    {
        var navigation = new NPCNavigation();
        navigation.DeactivateMovementAuthority();
        navigation._PhysicsProcess(1d / 60d);
        Assert.Equal(0, navigation.PhysicalStepCount);
        navigation.Free();
    }

    /// <inheritdoc/>
    [Fact]
    public async Task SyntheticRuntime_MinimalLane_RejectsVelocityMismatchedFiniteCandidates()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(SyntheticRuntime_MinimalLane_RejectsVelocityMismatchedFiniteCandidates));
        SyntheticNavigationRuntime runtime = fixture.CreateNavigationRuntime();
        SceneTree sceneTree = GetSceneTree();
        Rid map = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetActive(map, true);
        fixture.Root.AddChild(CreateNavigationLane(map));
        runtime.Navigation.SetNavigationMap(map);
        sceneTree.Root.AddChild(runtime.Root);
        try
        {
            await WaitForFramesAsync(sceneTree, 3);
            await WaitForNavigationLaneSyncAsync(sceneTree, map, new Vector3(0f, 0f, -2f));
            runtime.Navigation.ActivateRuntimeBinding();
            runtime.AnimationTree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            runtime.AnimationTree.Advance(0d);
            runtime.Navigation.SetPhysicsProcess(false);
            runtime.AnimationPhysicsDriver.SetPhysicsProcess(false);
            runtime.Navigation.CaptureCompleteMovementDiagnostics = true;
            runtime.Navigation.CaptureBoundedTerminalCheckDiagnostics = true;
            runtime.Navigation.YawMotionAllowed = false;
            Assert.Equal(
                NavigationDestinationResult.Accepted,
                runtime.Navigation.SetDestination(new Transform3D(Basis.Identity, new Vector3(0f, 0f, -2f))));

            List<TerminalTraceFrame> trace = [];
            int producerAdvanceCount = 1; // Bootstrap evaluation commits the initial state without consuming time.
            Assert.False(runtime.AnimationPhysicsDriver.IsPhysicsProcessing());
            Assert.Equal(AnimationMixer.AnimationCallbackModeProcess.Manual, runtime.AnimationTree.CallbackModeProcess);
            for (int frame = 0;
                frame < 900 && !trace.Any(item => item.Candidate.HasCandidateEvaluation);
                frame++)
            {
                await WaitForNextPhysicsFrameAsync(sceneTree);
                runtime.Navigation._PhysicsProcess(1d / 60d);
                // This is the navigation consumer's tick. Do not describe the post-advance state as though it had
                // already been consumed; the producer below supplies the next tick's animation state.
                trace.Add(TerminalTraceFrame.Capture(runtime.Navigation, producerAdvanceCount));
                runtime.AnimationTree.Advance(1d / 60d);
                producerAdvanceCount++;
            }
            Assert.Equal(trace.Count + 1, producerAdvanceCount);
            WriteDiagnosticLedger(fixture, runtime, trace, producerAdvanceCount);

            Assert.NotEmpty(trace);
            Assert.All(trace, frame => Assert.True(frame.IsFinite));
            TerminalTraceFrame candidateFrame = trace.FirstOrDefault(frame => frame.Candidate.HasCandidateEvaluation);
            Assert.True(
                candidateFrame.Candidate.HasCandidateEvaluation,
                $"No finite candidate was evaluated. {trace[^1].BuildReport()}; bounded={runtime.Navigation.LastBoundedTerminalCheckDiagnostics}");
            Assert.False(candidateFrame.Candidate.SchedulerCommit);
            Assert.False(candidateFrame.IsFiniteExit);
            Assert.Empty(runtime.Navigation.TerminalOutcomes);
            Assert.True(candidateFrame.Candidate.OppositeLaneCandidate.EndpointServiceable);
            Assert.False(candidateFrame.Candidate.OppositeLaneCandidate.VelocityCompatible);
            Assert.True(candidateFrame.Candidate.OppositeLaneCandidate.RejectedVelocityMismatch);
            Assert.True((candidateFrame.Candidate.SelectedSchedulerRejection & NPCTerminalCandidateRejection.VelocityMismatch) != 0);
        }
        finally
        {
            runtime.Root.QueueFree();
            NavigationServer3D.FreeRid(map);
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>Verifies the independently declared serviceable profile completes against live navigation telemetry.</summary>
    [Fact]
    public async Task SyntheticRuntime_HealthyFiniteExitProfile_CompletesLiveTerminalTrace()
    {
        SyntheticNavigationFixtureProfile profile = SyntheticNavigationFixtureProfile.HealthyFiniteExit;
        AssertHealthyProfileArithmetic(profile);
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(nameof(SyntheticRuntime_HealthyFiniteExitProfile_CompletesLiveTerminalTrace), profile);
        SyntheticNavigationRuntime runtime = fixture.CreateNavigationRuntime();
        SceneTree sceneTree = GetSceneTree();
        Rid map = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetActive(map, true);
        fixture.Root.AddChild(CreateNavigationLane(map));
        runtime.Navigation.SetNavigationMap(map);
        sceneTree.Root.AddChild(runtime.Root);
        try
        {
            await WaitForFramesAsync(sceneTree, 3);
            await WaitForNavigationLaneSyncAsync(sceneTree, map, profile.Destination.Origin);
            runtime.Navigation.ActivateRuntimeBinding();
            runtime.AnimationTree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            runtime.AnimationTree.Advance(0d);
            runtime.Navigation.SetPhysicsProcess(false);
            runtime.AnimationPhysicsDriver.SetPhysicsProcess(false);
            runtime.Navigation.CaptureCompleteMovementDiagnostics = true;
            runtime.Navigation.CaptureBoundedTerminalCheckDiagnostics = true;
            runtime.Navigation.YawMotionAllowed = false;
            Assert.Equal(NavigationDestinationResult.Accepted, runtime.Navigation.SetDestination(profile.Destination));

            using var contactLock = SyntheticNavigationContactLockBinding.Create(fixture, runtime);
            Assert.Same(contactLock.Coordinator, runtime.Navigation.ContactLockCoordinator);
            var footSampler = LiveFootTraceSampler.Create(runtime, contactLock.Coordinator);
            List<TerminalTraceFrame> trace = [];
            int producerAdvanceCount = 1;
            for (int frame = 0; frame < 900 && runtime.Navigation.TerminalOutcomes.Count == 0; frame++)
            {
                await WaitForNextPhysicsFrameAsync(sceneTree);
                contactLock.PublishLiveState(runtime.Navigation, 1d / 60d);
                runtime.Navigation._PhysicsProcess(1d / 60d);
                trace.Add(TerminalTraceFrame.Capture(runtime.Navigation, producerAdvanceCount, footSampler));
                runtime.AnimationTree.Advance(1d / 60d);
                producerAdvanceCount++;
            }

            WriteDiagnosticLedger(fixture, runtime, trace, producerAdvanceCount, profile.Destination.Origin);
            AssertTraceValid(fixture, profile, runtime, trace, producerAdvanceCount);
            NavigationTerminalOutcomeRecord outcome = Assert.Single(runtime.Navigation.TerminalOutcomes);
            AssertTerminalOutcome(fixture, profile, runtime, trace, producerAdvanceCount, outcome);

            List<TerminalTraceFrame> postOutcomeHold = [];
            for (int frame = 0; frame < 2; frame++)
            {
                await WaitForNextPhysicsFrameAsync(sceneTree);
                runtime.Navigation._PhysicsProcess(1d / 60d);
                postOutcomeHold.Add(TerminalTraceFrame.Capture(runtime.Navigation, producerAdvanceCount, footSampler));
                runtime.AnimationTree.Advance(1d / 60d);
                producerAdvanceCount++;
            }
            AssertPostOutcomeHold(fixture, profile, runtime, trace, postOutcomeHold, producerAdvanceCount);
            AssertTraceMutationsFail(profile, runtime, trace, producerAdvanceCount, outcome);
        }
        finally
        {
            runtime.Root.QueueFree();
            NavigationServer3D.FreeRid(map);
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>Guards live-foot trace sampling against treating skeleton-relative global poses as rest-relative poses.</summary>
    [Fact]
    public async Task SyntheticNavigationFixture_FootWorldPoseUsesNonIdentitySkeletonTransformWithoutRestComposition()
    {
        using var fixture = SyntheticLocomotionRuntimeFixture.Create(
            nameof(SyntheticNavigationFixture_FootWorldPoseUsesNonIdentitySkeletonTransformWithoutRestComposition));
        SyntheticNavigationRuntime runtime = fixture.CreateNavigationRuntime();
        SceneTree sceneTree = GetSceneTree();
        sceneTree.Root.AddChild(runtime.Root);
        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            runtime.Skeleton.Transform = new Transform3D(
                new Basis(Vector3.Up, 0.37f),
                new Vector3(1.4f, 0.3f, -0.8f));
            await WaitForNextFrameAsync(sceneTree);

            int foot = runtime.Skeleton.FindBone("LeftFoot");
            Assert.True(foot >= 0);
            Transform3D actual = GetBoneWorldPose(runtime.Skeleton, foot);
            Transform3D expected = runtime.Skeleton.GlobalTransform * runtime.Skeleton.GetBoneGlobalPose(foot);
            Transform3D restComposed = runtime.Skeleton.GlobalTransform
                * runtime.Skeleton.GetBoneGlobalRest(foot)
                * runtime.Skeleton.GetBoneGlobalPose(foot);

            Assert.Equal(expected, actual);
            Assert.True(
                actual.Origin.DistanceTo(restComposed.Origin) > 0.1f,
                "The non-identity skeleton transform must expose erroneous rest-pose composition.");
        }
        finally
        {
            runtime.Root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>Guards profile derivation against selecting the opposite finite exit's endpoint response.</summary>
    [Fact]
    public void SyntheticNavigationFixtureProfile_DerivesSelectedExitEndpointFromDeclaredMapping()
    {
        SyntheticNavigationManifest source = SyntheticNavigationManifest.HealthyFiniteExit;
        SyntheticNavigationClipDeclaration[] clips = [.. source.Clips];
        int leftExitIndex = Array.FindIndex(
            clips,
            clip => clip.Capability == StandingLocomotionCapability.ExitForwardLeft);
        int rightExitIndex = Array.FindIndex(
            clips,
            clip => clip.Capability == StandingLocomotionCapability.ExitForwardRight);
        clips[leftExitIndex] = clips[leftExitIndex] with
        {
            SkeletonLocalRootResponse = new Vector3(0f, 0f, 0.11f),
        };
        clips[rightExitIndex] = clips[rightExitIndex] with
        {
            SkeletonLocalRootResponse = new Vector3(0f, 0f, 0.31f),
        };
        SyntheticNavigationManifest manifest = source with
        {
            Clips = clips,
        };
        SyntheticNavigationClipDeclaration leftExit = manifest["f2i_l"];
        SyntheticNavigationClipDeclaration rightExit = manifest["f2i_r"];
        Transform3D destination = new(Basis.Identity, new Vector3(0f, 0f, -2f));
        const int ordinaryForwardCycles = 6;
        var profile = SyntheticNavigationFixtureProfile.Create(
            "distinct-forward-exit-endpoints",
            manifest,
            destination,
            rightExit.Role,
            ordinaryForwardCycles);

        float selectedEndpointRemainder = destination.Origin.Length()
            - ((ordinaryForwardCycles * manifest["wf"].SemanticCatalogueRootResponse.Length())
                + rightExit.SemanticCatalogueRootResponse.Length());
        float oppositeEndpointRemainder = destination.Origin.Length()
            - ((ordinaryForwardCycles * manifest["wf"].SemanticCatalogueRootResponse.Length())
                + leftExit.SemanticCatalogueRootResponse.Length());

        Assert.NotEqual(leftExit.SemanticCatalogueRootResponse, rightExit.SemanticCatalogueRootResponse);
        Assert.Equal(rightExit.Role, profile.ExpectedExitRole);
        Assert.Equal(NPCAnimationPhaseLane.Right, profile.ExpectedLane);
        Assert.Equal("release_r", profile.ExpectedReleaseEvent);
        Assert.Equal(rightExit.SemanticCatalogueRootResponse, profile.ExpectedExitEndpointResponse);
        Assert.Equal(selectedEndpointRemainder, profile.ExpectedUncorrectedTerminalRemainderMetres, 5);
        Assert.NotEqual(oppositeEndpointRemainder, profile.ExpectedUncorrectedTerminalRemainderMetres, 5);
    }

    private static AnimationNodeStateMachineTransition FindTransition(AnimationNodeStateMachine states, StringName from, StringName to)
    {
        for (int index = 0; index < states.GetTransitionCount(); index++)
        {
            if (states.GetTransitionFrom(index) == from && states.GetTransitionTo(index) == to)
            {
                return states.GetTransition(index);
            }
        }
        throw new Xunit.Sdk.XunitException($"Missing fixture transition '{from}' to '{to}'.");
    }

    /// <summary>
    /// Waits until the freshly created navigation lane is serviceable. NavigationServer applies map activation and
    /// region uploads across successive physics-frame syncs, so a fixed process-frame wait races the sync on sessions
    /// with fast frames and turns <see cref="NavigationBase.SetDestination" /> results order-dependent.
    /// </summary>
    private static async Task WaitForNavigationLaneSyncAsync(SceneTree sceneTree, Rid map, Vector3 destination)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            if (NavigationServer3D.MapGetPath(map, Vector3.Zero, destination, true).Length > 0)
            {
                return;
            }

            await WaitForNextPhysicsFrameAsync(sceneTree);
        }

        throw new TimeoutException("Navigation lane did not become serviceable within 30 physics frames.");
    }

    private static NavigationRegion3D CreateNavigationLane(Rid map)
    {
        var mesh = new NavigationMesh();
        mesh.SetVertices([
            new Vector3(-1f, 0f, -3f),
            new Vector3(1f, 0f, -3f),
            new Vector3(1f, 0f, 1f),
            new Vector3(-1f, 0f, 1f),
        ]);
        mesh.AddPolygon([0, 1, 2]);
        mesh.AddPolygon([0, 2, 3]);
        var lane = new NavigationRegion3D
        {
            Name = "MinimalNavigationLane",
            NavigationMesh = mesh,
        };
        lane.SetNavigationMap(map);
        return lane;
    }

    private static Vector3 EvaluatedWorldRoot(SyntheticNavigationRuntime runtime, SyntheticNavigationClipDeclaration declaration)
        => runtime.RootMotionReference.GlobalBasis * (declaration.SkeletonLocalRootResponse * runtime.Skeleton.MotionScale);

    private static Vector3 SemanticResponseToActorLocal(Vector2 response) => new(response.X, 0f, -response.Y);

    private static ExpectedFiniteExitMapping ResolveExpectedFiniteExitMapping(SyntheticNavigationFixtureProfile profile)
        => ResolveFiniteExitMapping(
            profile,
            profile.Manifest.Clips.Single(clip => clip.Role == profile.ExpectedExitRole));

    private static ExpectedFiniteExitMapping ResolveDivergentFiniteExitMapping(SyntheticNavigationFixtureProfile profile)
    {
        ExpectedFiniteExitMapping expectedExit = ResolveExpectedFiniteExitMapping(profile);
        StandingLocomotionCapability divergentCapability = expectedExit.Lane switch
        {
            NPCAnimationPhaseLane.Left => StandingLocomotionCapability.ExitForwardRight,
            NPCAnimationPhaseLane.Right => StandingLocomotionCapability.ExitForwardLeft,
            _ => throw new Xunit.Sdk.XunitException(
                $"Profile '{profile.Name}' selected exit role '{expectedExit.Declaration.Role}' has no opposite forward-exit mapping."),
        };
        return ResolveFiniteExitMapping(profile, profile.Manifest[divergentCapability]);
    }

    private static ExpectedFiniteExitMapping ResolveFiniteExitMapping(
        SyntheticNavigationFixtureProfile profile,
        SyntheticNavigationClipDeclaration declaration)
    {
        SyntheticPhaseEvent release = (declaration.DeclaredPhaseEvents ?? [])
            .Single(@event => @event.Kind == "Release" && @event.BoundaryContext == "start");
        NPCAnimationPhaseLane lane = release.Side switch
        {
            "Left" => NPCAnimationPhaseLane.Left,
            "Right" => NPCAnimationPhaseLane.Right,
            _ => throw new Xunit.Sdk.XunitException(
                $"Profile '{profile.Name}' selected exit role '{declaration.Role}' has no declared left/right release mapping."),
        };
        StringName state = lane == NPCAnimationPhaseLane.Left ? new StringName("NPCF2IL") : new StringName("NPCF2IR");
        return new(declaration, lane, release.ID, state);
    }

    private static void AssertDirection(Vector3 observed, Vector3 expected, string description)
    {
        Assert.True(observed.LengthSquared() > 0f, $"Expected {description} to have planar motion.");
        Assert.True(observed.Normalized().Dot(expected) > 0.9999f, $"Expected {description} to be {expected}; observed {observed}.");
    }

    private static void AssertVectorClose(Vector3 expected, Vector3 actual, string description)
        => Assert.True(expected.DistanceTo(actual) <= 0.00001f, $"Expected {description} {expected}; observed {actual}.");

    private static void WriteDiagnosticLedger(
        SyntheticLocomotionRuntimeFixture fixture,
        SyntheticNavigationRuntime runtime,
        IReadOnlyList<TerminalTraceFrame> trace,
        int producerAdvanceCount,
        Vector3? target = null)
    {
        string path = ProjectSettings.GlobalizePath("res://temp/navigation/synthetic-minimal-lane-ledger.json");
        string? directory = Path.GetDirectoryName(path);
        Assert.NotNull(directory);
        _ = Directory.CreateDirectory(directory!);
        File.WriteAllText(path, BuildDiagnosticLedger(fixture, runtime, trace, producerAdvanceCount, target ?? new Vector3(0f, 0f, -2f)));
    }

    private static string BuildDiagnosticLedger(
        SyntheticLocomotionRuntimeFixture fixture,
        SyntheticNavigationRuntime runtime,
        IReadOnlyList<TerminalTraceFrame> trace,
        int producerAdvanceCount,
        Vector3 target)
    {
        var ledger = new
        {
            package = fixture.Declaration.NavigationManifest.Package,
            coordinateFrames = new
            {
                actorBasis = LedgerBasis(runtime.Actor.GlobalBasis),
                referenceBasis = LedgerBasis(runtime.RootMotionReference.GlobalBasis),
                skeletonMotionScale = runtime.Skeleton.MotionScale,
            },
            producer = new
            {
                callbackMode = runtime.AnimationTree.CallbackModeProcess.ToString(),
                sharedDriverEnabled = runtime.AnimationPhysicsDriver.IsPhysicsProcessing(),
                manualAdvanceCount = producerAdvanceCount,
            },
            intervals = trace.Select(frame => new
            {
                consumerTick = frame.Movement.PhysicsFrame,
                consumedProducerAdvance = frame.ConsumedProducerAdvance,
                actor = LedgerVector(frame.Actor.Origin),
                remainingError = LedgerVector(target - frame.Actor.Origin),
                selected = new
                {
                    state = frame.State.ToString(),
                    role = frame.ExitRole,
                    lane = frame.Lane.ToString(),
                    releaseEvent = frame.ReleaseEvent,
                    blend = LedgerVector(frame.Published.BlendPoint),
                    playbackRate = frame.Published.PlaybackRate,
                    mappingEvent = frame.Published.Event.ToString(),
                },
                evaluated = new
                {
                    rawRoot = LedgerVector(frame.Movement.RawRootDelta),
                    semanticRootWorld = LedgerVector(frame.Movement.SemanticProjectedRootWorldDelta),
                    authoredRootWorld = LedgerVector(frame.Movement.AuthoredRootWorldDelta),
                    programmeDesiredWorld = LedgerVector(frame.Movement.ProgrammeDesiredWorldDelta),
                    programmeDesiredContribution = LedgerVector(frame.Movement.ProgrammeDesiredContribution),
                    syntheticWorld = LedgerVector(frame.Movement.SyntheticWorldDelta),
                    contactWorld = LedgerVector(frame.Movement.ContactWorldDelta),
                    requestedVelocity = LedgerVector(frame.Movement.RequestedVelocity),
                    requestedWorld = LedgerVector(frame.Movement.RequestedWorldDelta),
                    collisionTravel = LedgerVector(frame.Movement.CollisionTravel),
                    collisionRemainder = LedgerVector(frame.Movement.CollisionRemainder),
                    realisedWorld = LedgerVector(frame.Movement.RealisedWorldDelta),
                    collided = frame.Movement.Collided,
                    compositionError = LedgerVector(frame.Movement.CompositionError),
                    realisationError = LedgerVector(frame.Movement.RealisationError),
                },
                forecast = new
                {
                    resolved = frame.SelectedCandidate.ProjectionResolved,
                    predictedEndpointResidual = LedgerVector(frame.SelectedCandidate.PredictedEndpointResidualWorld),
                    predictedEndpointYawResidual = frame.SelectedCandidate.PredictedEndpointYawResidual,
                    eligible = frame.SelectedCandidate.FinalDecisionEligible,
                    serviceable = frame.SelectedCandidate.EndpointServiceable,
                    schedulerRejection = frame.Candidate.SelectedSchedulerRejection.ToString(),
                },
                correction = new
                {
                    required = LedgerVector(frame.SelectedCandidate.RequiredCorrectionWorld),
                    reachable = LedgerVector(frame.SelectedCandidate.ReachableCorrectionWorld),
                    applied = LedgerVector(frame.Movement.SyntheticWorldDelta),
                    correctionCapacityRejected = frame.SelectedCandidate.RejectedCorrectionBeyondCapacity,
                },
                boundary = new
                {
                    state = frame.BoundaryState.ToString(),
                    confirmationFrameCount = frame.ConfirmationFrameCount,
                    finalMovingBoundary = frame.IsFinalMovingBoundary,
                    stationaryConfirmation = frame.IsStationaryConfirmation,
                },
                feet = frame.Feet is LiveFootTrace feet ? new
                {
                    mapping = feet.Mapping,
                    rootWorld = LedgerVector(feet.Motion.PostIK.RootWorld.Origin),
                    hipsWorld = LedgerVector(feet.Motion.PostIK.HipsWorld.Origin),
                    left = LedgerFoot(feet.LeftFoot),
                    right = LedgerFoot(feet.RightFoot),
                } : null,
            }),
            outcome = runtime.Navigation.TerminalOutcomes.Select(record => new
            {
                sequence = record.Sequence,
                request = record.Request,
                revision = record.Revision,
                outcome = record.Outcome.ToString(),
                reason = record.Reason.ToString(),
                requested = record.RequestedTransform,
                final = record.FinalTransform,
                error = record.Error,
                tolerances = record.Tolerances,
                margins = record.Margins,
                acceptanceTick = record.AcceptancePhysicsTick,
                boundaryTick = record.BoundaryPhysicsTick,
                terminalTick = record.TerminalPhysicsTick,
                occurrence = record.Occurrence,
            }),
        };
        return JsonSerializer.Serialize(ledger, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void AssertHealthyProfileArithmetic(SyntheticNavigationFixtureProfile profile)
    {
        SyntheticNavigationClipDeclaration gait = profile.Manifest["wf"];
        ExpectedFiniteExitMapping expectedExit = ResolveExpectedFiniteExitMapping(profile);
        SyntheticNavigationClipDeclaration exit = expectedExit.Declaration;
        Assert.Equal(profile.ExpectedExitRole, exit.Role);
        Assert.Equal(expectedExit.Lane, profile.ExpectedLane);
        Assert.Equal(expectedExit.ReleaseEvent, profile.ExpectedReleaseEvent);
        Assert.Equal(new Vector3(0f, 0f, 0.2857143f), gait.SkeletonLocalRootResponse);
        Assert.Equal(new Vector3(0f, 0f, 0.20f), exit.SkeletonLocalRootResponse);
        Assert.Equal(gait.SemanticCatalogueRootResponse.Y / gait.DurationSeconds,
            exit.SemanticCatalogueRootResponse.Y / exit.DurationSeconds, 5);
        Assert.Equal(Vector2.Down, gait.SemanticCatalogueRootResponse.Normalized());
        Assert.Equal(Vector2.Down, exit.SemanticCatalogueRootResponse.Normalized());
        Assert.Equal(-2f, profile.Destination.Origin.Z);
        float expectedRemainder = profile.Destination.Origin.Length()
            - ((profile.OrdinaryForwardCyclesBeforeExit * gait.SemanticCatalogueRootResponse.Length())
                + exit.SemanticCatalogueRootResponse.Length());
        Assert.Equal(expectedRemainder, profile.ExpectedUncorrectedTerminalRemainderMetres, 5);
    }

    private static void AssertTraceValid(
        SyntheticLocomotionRuntimeFixture fixture,
        SyntheticNavigationFixtureProfile profile,
        SyntheticNavigationRuntime runtime,
        IReadOnlyList<TerminalTraceFrame> trace,
        int producerAdvanceCount)
    {
        List<string> failures = ValidateTrace(profile, trace, runtime.Navigation.TerminalOutcomes);
        ExpectedFiniteExitMapping expectedExit = ResolveExpectedFiniteExitMapping(profile);
        if (!runtime.Player.HasAnimation($"locomotion/{profile.Manifest["wf"].Identity}")
            || !runtime.Player.HasAnimation($"locomotion/{expectedExit.Declaration.Identity}"))
        {
            failures.Add("The declared healthy gait or selected finite-exit identity was not loaded by the live fixture.");
        }
        AssertLiveFootTraceValid(profile, runtime, trace, failures);

        FailForTraceFailures(failures, fixture, runtime, trace, producerAdvanceCount, profile.Destination.Origin);
    }

    private static void AssertTerminalOutcome(
        SyntheticLocomotionRuntimeFixture fixture,
        SyntheticNavigationFixtureProfile profile,
        SyntheticNavigationRuntime runtime,
        IReadOnlyList<TerminalTraceFrame> trace,
        int producerAdvanceCount,
        NavigationTerminalOutcomeRecord outcome)
    {
        List<string> failures = [];
        if (outcome.Outcome != NavigationTerminalOutcome.Completed || outcome.Reason != NavigationTerminalReason.DestinationReached)
        {
            failures.Add($"Expected one completed DestinationReached outcome; observed {outcome.Outcome}/{outcome.Reason}.");
        }
        if (outcome.Error.PlanarDistance > outcome.Tolerances.PlanarDistance
            || outcome.Error.YawRadians > outcome.Tolerances.YawRadians)
        {
            failures.Add("The realised terminal transform was outside the unchanged outcome tolerance.");
        }
        if (outcome.RequestedTransform.X != profile.Destination.Origin.X
            || outcome.RequestedTransform.Y != profile.Destination.Origin.Y
            || outcome.RequestedTransform.Z != profile.Destination.Origin.Z)
        {
            failures.Add("The committed outcome did not retain the accepted request transform.");
        }

        FailForTraceFailures(failures, fixture, runtime, trace, producerAdvanceCount, profile.Destination.Origin);
    }

    private static void AssertLiveFootTraceValid(
        SyntheticNavigationFixtureProfile profile,
        SyntheticNavigationRuntime runtime,
        IReadOnlyList<TerminalTraceFrame> trace,
        List<string> failures)
    {
        if (trace.Any(frame => frame.Feet is null))
        {
            failures.Add("Every terminal trace frame must retain evaluated left/right foot samples.");
            return;
        }

        for (int index = 0; index < trace.Count; index++)
        {
            TerminalTraceFrame frame = trace[index];
            LiveFootTrace feet = frame.Feet!;
            if (!ReferenceEquals(runtime.Navigation.ContactLockCoordinator, feet.Coordinator))
            {
                failures.Add($"Terminal frame {frame.Movement.PhysicsFrame} was not sampled from the navigation-bound contact-lock coordinator.");
            }
            if (feet.CoordinatorLiveState.PhysicsTick == 0
                || (index > 0
                    && feet.CoordinatorLiveState.PhysicsTick != trace[index - 1].Feet!.CoordinatorLiveState.PhysicsTick + 1))
            {
                failures.Add($"Terminal frame {frame.Movement.PhysicsFrame} lacks consecutive live coordinator evidence.");
            }
            if (feet.Mapping.Role != feet.CoordinatorLiveState.Role
                || feet.Mapping.Identity != feet.CoordinatorLiveState.Identity
                || feet.Mapping.Phase != feet.CoordinatorLiveState.Phase)
            {
                failures.Add($"Terminal frame {frame.Movement.PhysicsFrame} did not retain the sampled coordinator lifecycle state.");
            }
        }

        ExpectedFiniteExitMapping expectedExit = ResolveExpectedFiniteExitMapping(profile);
        NPCFootMotionCalibration calibration = NPCFootRawClipBaselineLoader.Load(
            runtime.Player,
            runtime.Skeleton,
            [
                $"locomotion/{profile.Manifest["wf"].Identity}",
                $"locomotion/{expectedExit.Declaration.Identity}",
            ]);
        NPCFootMotionContinuityResult continuity = NPCFootMotionContinuityDetector.Evaluate(
            "healthy-live-terminal-trace",
            [.. trace.Select(frame => frame.Feet!.Motion)],
            calibration);
        if (!continuity.EvidenceValid || continuity.Events.Count > 0)
        {
            failures.Add($"Live per-foot continuity failed. {continuity.BuildReport()}");
        }

        failures.AddRange(ValidateLiveFootSeam(profile, trace));
    }

    private static void AssertPostOutcomeHold(
        SyntheticLocomotionRuntimeFixture fixture,
        SyntheticNavigationFixtureProfile profile,
        SyntheticNavigationRuntime runtime,
        IReadOnlyList<TerminalTraceFrame> trace,
        IReadOnlyList<TerminalTraceFrame> postOutcomeHold,
        int producerAdvanceCount)
    {
        List<string> failures = [];
        TerminalTraceFrame terminal = trace[^1];
        foreach (TerminalTraceFrame frame in postOutcomeHold)
        {
            if (!HasNoMotion(frame.Movement))
            {
                failures.Add($"Post-outcome frame {frame.Movement.PhysicsFrame} contained motion.");
            }
            if (!SameTransform(terminal.Actor, frame.Actor))
            {
                failures.Add($"Post-outcome frame {frame.Movement.PhysicsFrame} changed the terminal actor transform.");
            }
        }

        FailForTraceFailures(failures, fixture, runtime, [.. trace, .. postOutcomeHold], producerAdvanceCount, profile.Destination.Origin);
    }

    private static void AssertTraceMutationsFail(
        SyntheticNavigationFixtureProfile profile,
        SyntheticNavigationRuntime runtime,
        IReadOnlyList<TerminalTraceFrame> trace,
        int producerAdvanceCount,
        NavigationTerminalOutcomeRecord outcome)
    {
        ExpectedFiniteExitMapping expectedExit = ResolveExpectedFiniteExitMapping(profile);
        int confirmation = trace.Select((frame, index) => (frame, index))
            .Single(item => item.frame.ConfirmationFrameCount == 1).index;
        int finite = trace.Select((frame, index) => (frame, index))
            .First(item => item.frame.State == expectedExit.State).index;

        TerminalTraceFrame[] postBoundaryMotion = [.. trace];
        postBoundaryMotion[confirmation] = postBoundaryMotion[confirmation] with
        {
            Movement = postBoundaryMotion[confirmation].Movement with
            {
                SyntheticWorldDelta = Vector3.Forward,
            },
        };
        AssertMutationRejected("post-boundary movement", profile, runtime, postBoundaryMotion, producerAdvanceCount, outcome);

        TerminalTraceFrame[] postBoundaryYaw = [.. trace];
        postBoundaryYaw[confirmation] = postBoundaryYaw[confirmation] with
        {
            Actor = new Transform3D(new Basis(Vector3.Up, 0.1f), postBoundaryYaw[confirmation].Actor.Origin),
        };
        AssertMutationRejected("post-boundary yaw", profile, runtime, postBoundaryYaw, producerAdvanceCount, outcome);

        NavigationOutcomeTransform endpoint = outcome.FinalTransform with
        {
            Z = outcome.FinalTransform.Z + 1f,
        };
        NavigationTerminalOutcomeRecord endpointMutation = outcome with
        {
            FinalTransform = endpoint,
        };
        AssertMutationRejected("terminal endpoint", profile, runtime, trace, producerAdvanceCount, endpointMutation);

        TerminalTraceFrame[] continuityMutation = [.. trace];
        continuityMutation[finite] = continuityMutation[finite] with
        {
            Actor = new Transform3D(continuityMutation[finite].Actor.Basis,
                continuityMutation[finite].Actor.Origin + Vector3.Right),
        };
        AssertMutationRejected("movement continuity", profile, runtime, continuityMutation, producerAdvanceCount, outcome);

        TerminalTraceFrame[] ownershipMutation = [.. trace];
        ownershipMutation[finite] = ownershipMutation[finite] with
        {
            ConsumedProducerAdvance = ownershipMutation[finite].ConsumedProducerAdvance + 2,
        };
        AssertMutationRejected("finite interval ownership", profile, runtime, ownershipMutation, producerAdvanceCount, outcome);

        ExpectedFiniteExitMapping divergentExit = ResolveDivergentFiniteExitMapping(profile);
        TerminalTraceFrame[] selectedRoleMutation = [.. trace];
        for (int index = finite;
            index < selectedRoleMutation.Length && selectedRoleMutation[index].State == expectedExit.State;
            index++)
        {
            selectedRoleMutation[index] = selectedRoleMutation[index] with
            {
                ExitRole = divergentExit.Declaration.Role,
            };
        }
        AssertMutationRejected("selected finite-exit role", profile, runtime, selectedRoleMutation, producerAdvanceCount, outcome);

        TerminalTraceFrame[] footSeamMutation = [.. trace];
        footSeamMutation[finite] = footSeamMutation[finite] with
        {
            Feet = footSeamMutation[finite].Feet! with
            {
                LeftFoot = footSeamMutation[finite].Feet!.LeftFoot with
                {
                    WorldPose = new Transform3D(
                        footSeamMutation[finite].Feet!.LeftFoot.WorldPose.Basis,
                        footSeamMutation[finite].Feet!.LeftFoot.WorldPose.Origin + Vector3.Right),
                    WorldVelocity = new Vector3(60f, 0f, 0f),
                },
            },
        };
        AssertLiveFootTraceMutationRejected("live left-foot seam", profile, footSeamMutation);

        TerminalTraceFrame[] lifecycleMutation = [.. trace];
        lifecycleMutation[finite] = lifecycleMutation[finite] with
        {
            Feet = lifecycleMutation[finite].Feet! with
            {
                CoordinatorLiveState = lifecycleMutation[finite].Feet!.CoordinatorLiveState with
                {
                    Enabled = false,
                    Role = string.Empty,
                    Clip = string.Empty,
                    Identity = string.Empty,
                    Generation = string.Empty,
                },
            },
        };
        AssertLiveFootTraceMutationRejected("live coordinator lifecycle", profile, lifecycleMutation);
    }

    private static void AssertMutationRejected(
        string mutation,
        SyntheticNavigationFixtureProfile profile,
        SyntheticNavigationRuntime runtime,
        IReadOnlyList<TerminalTraceFrame> trace,
        int producerAdvanceCount,
        NavigationTerminalOutcomeRecord outcome)
    {
        List<string> failures = ValidateTrace(profile, trace, [outcome]);
        if (failures.Count == 0)
        {
            throw new Xunit.Sdk.XunitException($"Trace mutation '{mutation}' escaped validation.\n"
                + BuildDiagnosticLedgerForMutation(profile, runtime, trace, producerAdvanceCount, outcome));
        }
    }

    private static void AssertLiveFootTraceMutationRejected(
        string mutation,
        SyntheticNavigationFixtureProfile profile,
        IReadOnlyList<TerminalTraceFrame> trace)
    {
        List<string> failures = ValidateLiveFootSeam(profile, trace);
        if (failures.Count == 0)
        {
            throw new Xunit.Sdk.XunitException($"Live foot-trace mutation '{mutation}' escaped validation.");
        }
    }

    private static List<string> ValidateLiveFootSeam(
        SyntheticNavigationFixtureProfile profile,
        IReadOnlyList<TerminalTraceFrame> trace)
    {
        List<string> failures = [];
        ExpectedFiniteExitMapping expectedExit = ResolveExpectedFiniteExitMapping(profile);
        int seam = FindIndex(trace, frame => frame.State == expectedExit.State);
        if (seam < 3 || trace.Count < seam + 3 || trace.Any(frame => frame.Feet is null))
        {
            failures.Add("Live trace does not retain the required three-frame travel/f2i seam window.");
            return failures;
        }

        for (int index = seam - 3; index <= seam + 2; index++)
        {
            TerminalTraceFrame current = trace[index];
            LiveFootTrace frame = current.Feet!;
            bool isExpectedFiniteExit = current.State == expectedExit.State;
            StringName expectedState = isExpectedFiniteExit ? expectedExit.State : current.State;
            if (frame.Mapping.State != expectedState.ToString())
            {
                failures.Add($"Live foot mapping diverged from evaluated state at trace index {index}.");
            }
            if (!frame.IsFinite)
            {
                failures.Add($"Non-finite live foot telemetry at trace index {index}.");
            }
            if (frame.Mapping.Role != frame.CoordinatorLiveState.Role
                || frame.Mapping.Identity != frame.CoordinatorLiveState.Identity
                || frame.Mapping.Phase != frame.CoordinatorLiveState.Phase)
            {
                failures.Add($"Live coordinator state diverged from the retained mapping at trace index {index}.");
            }
            if (index == seam && (!frame.CoordinatorLiveState.Enabled || !trace[index - 1].Feet!.CoordinatorLiveState.Enabled))
            {
                failures.Add("The evaluated travel-to-f2i seam lacks live coordinator lifecycle publication for the selected support foot.");
            }
            if (index == seam)
            {
                continue;
            }

            LiveFootTrace previous = trace[index - 1].Feet!;
            foreach ((string side, LiveFootSample before, LiveFootSample after) in new[]
                     {
                         ("left", previous.LeftFoot, frame.LeftFoot),
                         ("right", previous.RightFoot, frame.RightFoot),
                     })
            {
                float step = after.WorldPose.Origin.DistanceTo(before.WorldPose.Origin);
                float velocityChange = after.WorldVelocity.DistanceTo(before.WorldVelocity);
                if (step > NPCWFToF2ISeamThresholds.DimensionalDefaults.MaximumFootStepMetres)
                {
                    failures.Add($"Live {side}-foot seam step {step:F6} m exceeded the established dimensional bound.");
                }
                if (velocityChange > NPCWFToF2ISeamThresholds.DimensionalDefaults.MaximumVelocityChangeMetresPerSecond)
                {
                    failures.Add($"Live {side}-foot seam velocity change {velocityChange:F6} m/s exceeded the established continuity bound.");
                }
            }
        }
        return failures;
    }

    private static List<string> ValidateTrace(
        SyntheticNavigationFixtureProfile profile,
        IReadOnlyList<TerminalTraceFrame> trace,
        IReadOnlyList<NavigationTerminalOutcomeRecord> outcomes)
    {
        List<string> failures = [];
        ExpectedFiniteExitMapping expectedExit = ResolveExpectedFiniteExitMapping(profile);
        if (trace.Count == 0)
        {
            failures.Add("No consumer-tick telemetry was captured.");
            return failures;
        }
        if (outcomes.Count != 1)
        {
            failures.Add($"Expected exactly one terminal outcome; observed {outcomes.Count}.");
            return failures;
        }

        NavigationTerminalOutcomeRecord outcome = outcomes[0];
        for (int index = 0; index < trace.Count; index++)
        {
            TerminalTraceFrame current = trace[index];
            if (!current.IsFinite)
            {
                failures.Add($"Non-finite telemetry at trace index {index}.");
            }
            if (index > 0)
            {
                TerminalTraceFrame previous = trace[index - 1];
                if (current.Movement.PhysicsFrame != previous.Movement.PhysicsFrame + 1)
                {
                    failures.Add($"Consumer physics tick ownership is discontinuous at trace index {index}.");
                }
                if (current.ConsumedProducerAdvance != previous.ConsumedProducerAdvance + 1)
                {
                    failures.Add($"Producer advance ownership is discontinuous at trace index {index}.");
                }
                if (!SameMovement(current.Actor.Origin - previous.Actor.Origin, current.Movement.RealisedWorldDelta))
                {
                    failures.Add($"Actor transform and realised movement disagree at trace index {index}.");
                }
            }
        }

        int firstFinite = FindIndex(trace, frame => frame.State == expectedExit.State);
        int boundary = FindIndex(trace, frame => frame.IsFinalMovingBoundary);
        if (firstFinite < 0 || boundary < 0 || firstFinite >= boundary)
        {
            failures.Add("The trace lacks the declared finite-exit interval followed by its final movement boundary.");
            return failures;
        }
        if (!trace.Take(firstFinite).Any(frame => frame.State == "NPCTravel" && frame.BoundaryState == NPCTerminalBoundaryState.Inactive))
        {
            failures.Add("The trace lacks ordinary NPCTravel before the declared finite exit.");
        }
        TerminalTraceFrame publishedStart = trace.FirstOrDefault(frame => frame.Published.Event == NPCAnimationEvent.StartExit);
        if (publishedStart.Published.Event != NPCAnimationEvent.StartExit
            || publishedStart.Published.State != expectedExit.State)
        {
            failures.Add("The first live f2i sample did not publish the matching StartExit mapping.");
        }
        for (int index = firstFinite; index <= boundary; index++)
        {
            if (trace[index].State != expectedExit.State
                || trace[index].ExitRole != expectedExit.Declaration.Role
                || trace[index].Lane != expectedExit.Lane
                || trace[index].ReleaseEvent != expectedExit.ReleaseEvent)
            {
                failures.Add($"Declared finite-exit mapping has a gap at trace index {index}.");
                break;
            }
        }

        TerminalTraceFrame candidate = trace.FirstOrDefault(frame => frame.Candidate.SchedulerCommit);
        if (!candidate.Candidate.SchedulerCommit
            || candidate.Lane != expectedExit.Lane
            || candidate.ExitRole != expectedExit.Declaration.Role
            || candidate.ReleaseEvent != expectedExit.ReleaseEvent
            || candidate.SelectedCandidate.ReleaseLane != expectedExit.Lane
            || candidate.SelectedCandidate.ExitRole != expectedExit.Declaration.Role
            || candidate.SelectedCandidate.ReleaseEvent != expectedExit.ReleaseEvent)
        {
            failures.Add("The accepted candidate did not retain the profile's lane, release event, and f2i role mapping.");
        }
        if (candidate.Candidate.DestinationRequestGeneration != outcome.Request.Generation
            || candidate.Candidate.RouteRevision != outcome.Revision)
        {
            failures.Add("The accepted candidate did not retain the accepted request/route identity.");
        }

        int[] confirmations = [.. trace.Skip(boundary + 1)
            .Where(frame => frame.IsStationaryConfirmation || frame.BoundaryState == NPCTerminalBoundaryState.Completed)
            .Select(frame => frame.ConfirmationOrdinal)];
        if (!confirmations.SequenceEqual([1, 2, 3, 4]))
        {
            failures.Add("Stationary confirmation did not contain exactly frames 1, 2, 3, and 4 after the movement boundary.");
        }
        for (int index = boundary + 1; index < trace.Count; index++)
        {
            if (!HasNoMotion(trace[index].Movement))
            {
                failures.Add($"Post-boundary movement was published at trace index {index}.");
            }
            if (!SameTransform(trace[boundary].Actor, trace[index].Actor))
            {
                failures.Add($"Post-boundary actor transform or yaw changed at trace index {index}.");
            }
        }
        if (outcome.Outcome != NavigationTerminalOutcome.Completed
            || outcome.Error.PlanarDistance > outcome.Tolerances.PlanarDistance
            || outcome.Error.YawRadians > outcome.Tolerances.YawRadians)
        {
            failures.Add("The terminal outcome was not a completed in-tolerance result.");
        }
        if (MathF.Abs(outcome.FinalTransform.Z - profile.Destination.Origin.Z) > outcome.Tolerances.PlanarDistance)
        {
            failures.Add("The persisted terminal endpoint does not match the requested destination within its unchanged tolerance.");
        }
        return failures;
    }

    private static void FailForTraceFailures(
        IReadOnlyList<string> failures,
        SyntheticLocomotionRuntimeFixture fixture,
        SyntheticNavigationRuntime runtime,
        IReadOnlyList<TerminalTraceFrame> trace,
        int producerAdvanceCount,
        Vector3 target)
    {
        if (failures.Count > 0)
        {
            throw new Xunit.Sdk.XunitException(string.Join(System.Environment.NewLine, failures) + System.Environment.NewLine
                + BuildDiagnosticLedger(fixture, runtime, trace, producerAdvanceCount, target));
        }
    }

    private static string BuildDiagnosticLedgerForMutation(
        SyntheticNavigationFixtureProfile profile,
        SyntheticNavigationRuntime runtime,
        IReadOnlyList<TerminalTraceFrame> trace,
        int producerAdvanceCount,
        NavigationTerminalOutcomeRecord outcome)
        => JsonSerializer.Serialize(new
        {
            trace = trace.Select(frame => new
            {
                state = frame.State.ToString(),
                mapping = new
                {
                    lane = frame.Lane.ToString(),
                    releaseEvent = frame.ReleaseEvent,
                    exitRole = frame.ExitRole,
                },
                movement = frame.Movement,
                boundary = new
                {
                    state = frame.BoundaryState.ToString(),
                    confirmationFrameCount = frame.ConfirmationFrameCount,
                },
                feet = frame.Feet,
            }),
            outcome,
            requested = profile.Destination.Origin,
            terminalOutcomes = runtime.Navigation.TerminalOutcomes,
            producerAdvanceCount,
        }, new JsonSerializerOptions { WriteIndented = true });

    private static int FindIndex(IReadOnlyList<TerminalTraceFrame> trace, Func<TerminalTraceFrame, bool> predicate)
    {
        for (int index = 0; index < trace.Count; index++)
        {
            if (predicate(trace[index]))
            {
                return index;
            }
        }
        return -1;
    }

    private static bool HasNoMotion(NPCMovementCompositionFrame movement)
        => movement.AuthoredRootWorldDelta.Length() <= NPCAnimationProgramme.NumericalConvergenceThreshold
            && movement.ProgrammeDesiredContribution.Length() <= NPCAnimationProgramme.NumericalConvergenceThreshold
            && movement.SyntheticWorldDelta.Length() <= NPCAnimationProgramme.NumericalConvergenceThreshold
            && movement.ContactWorldDelta.Length() <= NPCAnimationProgramme.NumericalConvergenceThreshold
            && movement.RequestedWorldDelta.Length() <= NPCAnimationProgramme.NumericalConvergenceThreshold
            && movement.CollisionTravel.Length() <= NPCAnimationProgramme.NumericalConvergenceThreshold
            && movement.CollisionRemainder.Length() <= NPCAnimationProgramme.NumericalConvergenceThreshold
            && movement.RealisedWorldDelta.Length() <= NPCAnimationProgramme.NumericalConvergenceThreshold;

    private static bool SameMovement(Vector3 expected, Vector3 actual)
        => expected.DistanceTo(actual) <= NPCAnimationProgramme.NumericalConvergenceThreshold;

    private static bool SameTransform(Transform3D expected, Transform3D actual)
        => SameMovement(expected.Origin, actual.Origin)
            && expected.Basis.GetRotationQuaternion().AngleTo(actual.Basis.GetRotationQuaternion())
                <= NPCAnimationProgramme.NumericalConvergenceThreshold;

    private static object LedgerBasis(Basis basis) => new
    {
        x = LedgerVector(basis.X),
        y = LedgerVector(basis.Y),
        z = LedgerVector(basis.Z),
    };

    private static object LedgerVector(Vector3 vector) => new { x = vector.X, y = vector.Y, z = vector.Z };

    private static object LedgerVector(Vector2 vector) => new { x = vector.X, y = vector.Y };

    private static object LedgerFoot(LiveFootSample foot) => new
    {
        worldPose = LedgerVector(foot.WorldPose.Origin),
        skeletonLocalPose = LedgerVector(foot.SkeletonLocalPose.Origin),
        worldVelocity = LedgerVector(foot.WorldVelocity),
        lifecycle = foot.Lifecycle.ToString(),
        lifecycleTransition = foot.LifecycleTransition,
        hasAnchor = foot.HasAnchor,
        resolutionPublished = foot.ResolutionPublished,
        contactPublished = foot.ContactPublished,
    };

    private readonly record struct ExpectedFiniteExitMapping(
        SyntheticNavigationClipDeclaration Declaration,
        NPCAnimationPhaseLane Lane,
        string ReleaseEvent,
        StringName State);

    private readonly record struct TerminalTraceFrame(
        Transform3D Actor,
        NPCMovementCompositionFrame Movement,
        NPCTerminalBoundaryState BoundaryState,
        int ConfirmationFrameCount,
        NPCNavigationPlaybackObservation Playback,
        NPCAnimationFrame Published,
        NPCTerminalCandidateDecisionDiagnostics Candidate,
        StringName State,
        string ExitRole,
        NPCAnimationPhaseLane Lane,
        string ReleaseEvent,
        int ConsumedProducerAdvance,
        LiveFootTrace? Feet = null)
    {
        public bool IsFinite => Actor.Origin.IsFinite()
            && Actor.Basis.Column0.IsFinite()
            && Actor.Basis.Column1.IsFinite()
            && Actor.Basis.Column2.IsFinite()
            && Movement.AuthoredRootWorldDelta.IsFinite()
            && Movement.ProgrammeDesiredWorldDelta.IsFinite()
            && Movement.ProgrammeDesiredContribution.IsFinite()
            && Movement.SyntheticWorldDelta.IsFinite()
            && Movement.ContactWorldDelta.IsFinite()
            && Movement.RequestedVelocity.IsFinite()
            && Movement.RequestedWorldDelta.IsFinite()
            && Movement.CollisionTravel.IsFinite()
            && Movement.CollisionRemainder.IsFinite()
            && Movement.RealisedWorldDelta.IsFinite();

        public bool IsFiniteExit => State == "NPCF2IL" || State == "NPCF2IR";

        public bool IsFinalMovingBoundary => BoundaryState == NPCTerminalBoundaryState.Confirmation
            && ConfirmationFrameCount == 0;

        public bool IsStationaryConfirmation => BoundaryState is NPCTerminalBoundaryState.Confirmation or NPCTerminalBoundaryState.Completed
            && ConfirmationFrameCount is >= 1 and <= 4;

        /// <summary>The completed terminal tick is the fourth observation even after the mutable counter resets.</summary>
        public int ConfirmationOrdinal => BoundaryState == NPCTerminalBoundaryState.Completed
            ? NPCAnimationProgramme.StableConvergenceFrameCount
            : ConfirmationFrameCount;

        public NPCTerminalCandidateEvaluationDiagnostics SelectedCandidate => Candidate.SelectedCandidateIndex switch
        {
            0 => Candidate.SameLaneCandidate,
            1 => Candidate.OppositeLaneCandidate,
            _ => default,
        };

        public static TerminalTraceFrame Capture(
            NPCNavigation navigation,
            int consumedProducerAdvance,
            LiveFootTraceSampler? footSampler = null) => new(
            navigation.Actor!.GlobalTransform,
            navigation.LastMovementCompositionFrame,
            navigation.TerminalBoundaryState,
            navigation.TerminalConfirmationFrameCount,
            navigation.PlaybackObservation,
            navigation.LastPublishedAnimationFrame,
            navigation.LastTerminalCandidateDecision,
            navigation.LastEvaluatedState,
            navigation.LastSelectedExitRole,
            navigation.LastSelectedReleaseLane,
            navigation.LastSelectedReleaseEvent,
            consumedProducerAdvance,
            footSampler?.Capture(navigation, consumedProducerAdvance));

        public string BuildReport() => $"state={State}; boundary={BoundaryState}; confirmation={ConfirmationFrameCount}; actor={Actor.Origin}; movement={Movement}";
    }

    private readonly record struct LiveFootMapping(string State, string Role, string Identity, float Phase, float PlaybackRate);

    private readonly record struct LiveFootSample(
        Transform3D SkeletonLocalPose,
        Transform3D WorldPose,
        Vector3 WorldVelocity,
        ContactLockLifecycle Lifecycle,
        string LifecycleTransition,
        bool HasAnchor,
        bool ResolutionPublished,
        bool ContactPublished);

    private sealed record LiveFootTrace(
        NPCFootMotionContinuitySample Motion,
        LiveFootMapping Mapping,
        LiveFootSample LeftFoot,
        LiveFootSample RightFoot,
        ContactLockCoordinator Coordinator,
        ContactLockLiveState CoordinatorLiveState)
    {
        public bool IsFinite => LeftFoot.WorldPose.Origin.IsFinite()
            && RightFoot.WorldPose.Origin.IsFinite()
            && LeftFoot.WorldVelocity.IsFinite()
            && RightFoot.WorldVelocity.IsFinite();

        public bool HasLiveLifecycle(LimbSide side)
        {
            LiveFootSample foot = side == LimbSide.Left ? LeftFoot : RightFoot;
            return foot.Lifecycle != ContactLockLifecycle.None || foot.ContactPublished || foot.ResolutionPublished;
        }
    }

    private sealed class LiveFootTraceSampler
    {
        private readonly SyntheticNavigationRuntime _runtime;
        private readonly ContactLockCoordinator _contacts;
        private readonly FootPosePointDefinition _leftDefinition;
        private readonly FootPosePointDefinition _rightDefinition;
        private readonly int _root;
        private readonly int _hips;
        private readonly int _leftFoot;
        private readonly int _leftToes;
        private readonly int _rightFoot;
        private readonly int _rightToes;
        private FootWorldSample? _previousLeftWorld;
        private FootWorldSample? _previousRightWorld;

        private LiveFootTraceSampler(
            SyntheticNavigationRuntime runtime,
            ContactLockCoordinator contacts,
            FootPosePointDefinition leftDefinition,
            FootPosePointDefinition rightDefinition)
        {
            _runtime = runtime;
            _contacts = contacts;
            _leftDefinition = leftDefinition;
            _rightDefinition = rightDefinition;
            _root = RequireBone("Root");
            _hips = RequireBone("Hips");
            _leftFoot = RequireBone("LeftFoot");
            _leftToes = RequireBone("LeftToes");
            _rightFoot = RequireBone("RightFoot");
            _rightToes = RequireBone("RightToes");
        }

        public static LiveFootTraceSampler Create(SyntheticNavigationRuntime runtime, ContactLockCoordinator contacts)
        {
            Assert.True(FootPosePointDefinition.TryCreate(runtime.Skeleton, LimbSide.Left, out FootPosePointDefinition left));
            Assert.True(FootPosePointDefinition.TryCreate(runtime.Skeleton, LimbSide.Right, out FootPosePointDefinition right));
            return new(runtime, contacts, left, right);
        }

        public LiveFootTrace Capture(NPCNavigation navigation, int producerAdvance)
        {
            Transform3D actorBefore = _runtime.Actor.GlobalTransform;
            StringName stateBefore = navigation.LastEvaluatedState;
            NPCNavigationPlaybackObservation playbackBefore = navigation.PlaybackObservation;
            NPCMovementCompositionFrame movementBefore = navigation.LastMovementCompositionFrame;
            NavigationTerminalOutcomeRecord[] outcomesBefore = [.. navigation.TerminalOutcomes];
            ContactLockCoordinatorSnapshot coordinatorBefore = _contacts.StateSnapshot;
            ContactLockLiveState liveBefore = _contacts.LiveState;
            ContactLockResolverTrace leftResolverBefore = _contacts.GetResolverTrace(LimbSide.Left);
            ContactLockResolverTrace rightResolverBefore = _contacts.GetResolverTrace(LimbSide.Right);
            ulong tick = navigation.LastMovementCompositionFrame.PhysicsFrame;
            Transform3D rootLocal = EvaluatedSkeletonPose(_root);
            Transform3D hipsLocal = EvaluatedSkeletonPose(_hips);
            Transform3D leftLocal = EvaluatedSkeletonPose(_leftFoot);
            Transform3D rightLocal = EvaluatedSkeletonPose(_rightFoot);
            Transform3D leftToeLocal = EvaluatedSkeletonPose(_leftToes);
            Transform3D rightToeLocal = EvaluatedSkeletonPose(_rightToes);
            Transform3D skeletonWorld = _runtime.Skeleton.GlobalTransform;
            Transform3D leftWorld = skeletonWorld * leftLocal;
            Transform3D rightWorld = skeletonWorld * rightLocal;
            Vector3 leftVelocity = CalculateVelocity(_previousLeftWorld, leftWorld.Origin, tick, liveBefore.Delta);
            Vector3 rightVelocity = CalculateVelocity(_previousRightWorld, rightWorld.Origin, tick, liveBefore.Delta);
            _previousLeftWorld = new(tick, leftWorld.Origin);
            _previousRightWorld = new(tick, rightWorld.Origin);

            ContactLockResolverTrace leftContact = _contacts.GetResolverTrace(LimbSide.Left);
            ContactLockResolverTrace rightContact = _contacts.GetResolverTrace(LimbSide.Right);
            Assert.Equal(actorBefore, _runtime.Actor.GlobalTransform);
            Assert.Equal(stateBefore, navigation.LastEvaluatedState);
            Assert.Equal(playbackBefore, navigation.PlaybackObservation);
            Assert.Equal(movementBefore, navigation.LastMovementCompositionFrame);
            Assert.Equal(outcomesBefore, navigation.TerminalOutcomes);
            Assert.Equal(coordinatorBefore, _contacts.StateSnapshot);
            Assert.Equal(liveBefore, _contacts.LiveState);
            Assert.Equal(leftResolverBefore, leftContact);
            Assert.Equal(rightResolverBefore, rightContact);

            FootPosePointSet leftPoints = WorldPoints(_leftDefinition.Evaluate(leftLocal, leftToeLocal), skeletonWorld);
            FootPosePointSet rightPoints = WorldPoints(_rightDefinition.Evaluate(rightLocal, rightToeLocal), skeletonWorld);
            NPCFootMotionPose pose = new(skeletonWorld * rootLocal, skeletonWorld * hipsLocal, leftWorld, rightWorld, leftPoints, rightPoints);
            LiveFootMapping mapping = new(
                stateBefore,
                liveBefore.Role,
                liveBefore.Identity,
                liveBefore.Phase,
                navigation.LastPublishedAnimationFrame.PlaybackRate);
            NPCFootGraphEvidence graph = new(
                stateBefore, string.Empty, playbackBefore.EvaluatedPlayPosition, 0f, liveBefore.Phase,
                navigation.LastPublishedAnimationFrame.PlaybackRate, liveBefore.Role, liveBefore.Clip,
                playbackBefore.ProgrammeStage.ToString(), string.Empty, navigation.LastSelectedReleaseLane.ToString(),
                navigation.LastSelectedReleaseEvent, navigation.LastSelectedExitRole);
            return new(
                new NPCFootMotionContinuitySample(tick, tick * liveBefore.Delta, actorBefore, pose, pose, graph,
                    ToEvidence(leftContact), ToEvidence(rightContact), (ulong)producerAdvance),
                mapping,
                ToFoot(leftLocal, leftWorld, leftVelocity, leftContact),
                ToFoot(rightLocal, rightWorld, rightVelocity, rightContact),
                _contacts,
                liveBefore);
        }

        private int RequireBone(StringName name)
        {
            int index = _runtime.Skeleton.FindBone(name);
            return index >= 0 ? index : throw new InvalidOperationException($"Live foot sampler requires bone '{name}'.");
        }

        private Transform3D EvaluatedSkeletonPose(int bone)
            => _runtime.Skeleton.GetBoneGlobalPose(bone);

        private static Vector3 CalculateVelocity(
            FootWorldSample? previous,
            Vector3 current,
            ulong currentTick,
            float currentDeltaSeconds)
        {
            if (previous is not FootWorldSample sample
                || currentTick <= sample.PhysicsTick
                || !float.IsFinite(currentDeltaSeconds)
                || currentDeltaSeconds <= 0f)
            {
                return Vector3.Zero;
            }

            float elapsedSeconds = (currentTick - sample.PhysicsTick) * currentDeltaSeconds;
            return elapsedSeconds > 0f ? (current - sample.WorldPosition) / elapsedSeconds : Vector3.Zero;
        }

        private static FootPosePointSet WorldPoints(FootPosePointSet local, Transform3D skeletonWorld)
            => new(
                skeletonWorld * local.Ankle,
                skeletonWorld * local.HeelSole,
                skeletonWorld * local.Midsole,
                skeletonWorld * local.Ball,
                skeletonWorld * local.ToeTip);

        private static NPCFootContactEvidence ToEvidence(ContactLockResolverTrace trace) => new(
            ObservedLifecycle(trace),
            $"{trace.DerivedSample.RuntimeValidation}/{trace.Snapshot.LifecycleTransition}",
            trace.Snapshot.HasAnchor,
            trace.Snapshot.Anchor,
            trace.ResolutionPublished,
            trace.ContactPublished);

        private static LiveFootSample ToFoot(
            Transform3D local,
            Transform3D world,
            Vector3 velocity,
            ContactLockResolverTrace contact) => new(
            local, world, velocity, ObservedLifecycle(contact),
            $"{contact.DerivedSample.RuntimeValidation}/{contact.Snapshot.LifecycleTransition}",
            contact.Snapshot.HasAnchor, contact.ResolutionPublished, contact.ContactPublished);

        private static ContactLockLifecycle ObservedLifecycle(ContactLockResolverTrace trace)
            => Enum.TryParse(trace.DerivedSample.ObservedLifecycle, ignoreCase: true, out ContactLockLifecycle lifecycle)
                ? lifecycle
                : trace.Snapshot.Lifecycle;

        private readonly record struct FootWorldSample(ulong PhysicsTick, Vector3 WorldPosition);
    }

    private sealed class SyntheticNavigationContactLockBinding : IDisposable
    {
        private readonly SyntheticNavigationRuntime _runtime;
        private readonly StandingLocomotionCatalogue _catalogue;
        private bool _disposed;

        private SyntheticNavigationContactLockBinding(
            SyntheticNavigationRuntime runtime,
            StandingLocomotionCatalogue catalogue,
            ContactLockCoordinator coordinator)
        {
            _runtime = runtime;
            _catalogue = catalogue;
            Coordinator = coordinator;
            runtime.Navigation.ContactLockCoordinator = coordinator;
        }

        public ContactLockCoordinator Coordinator
        {
            get;
        }

        public static SyntheticNavigationContactLockBinding Create(
            SyntheticLocomotionRuntimeFixture fixture,
            SyntheticNavigationRuntime runtime)
        {
            var catalogue = StandingLocomotionCatalogue.Parse(
                File.ReadAllText(ProjectSettings.GlobalizePath(fixture.NavigationCataloguePath)),
                fixture.NavigationCataloguePath);
            ContactLockCoordinator coordinator = ContactLockProductionComposition.LoadCoordinator(
                catalogue,
                runtime.Navigation.CataloguePath);
            return new(runtime, catalogue, coordinator);
        }

        /// <summary>
        /// Mirrors the production NPC composition's live-state publication before navigation consumes contact motion.
        /// This fixture deliberately does not install CharacterIK providers or claim native IK actuation.
        /// </summary>
        public void PublishLiveState(NPCNavigation navigation, double delta)
        {
            bool useFadingSource = string.IsNullOrWhiteSpace(navigation.LastEvaluatedRole)
                && !string.IsNullOrWhiteSpace(navigation.LastFadingFromRole);
            string role = useFadingSource ? navigation.LastFadingFromRole : navigation.LastEvaluatedRole;
            string clip = useFadingSource ? navigation.LastFadingFromClip : navigation.LastEvaluatedClip;
            float phase = useFadingSource
                ? navigation.LastFadingFromNormalisedPhase
                : navigation.LastEvaluatedNormalisedPhase;
            bool hasClip = _catalogue.Clips.TryGetValue(role, out StandingLocomotionClip? evaluatedClip)
                && evaluatedClip.Identity == clip;
            Coordinator.ObserveLiveState(new ContactLockLiveState(
                hasClip,
                role,
                clip,
                phase,
                hasClip ? _catalogue.PackageVariant : string.Empty,
                hasClip ? evaluatedClip!.Identity : string.Empty,
                hasClip ? _catalogue.ContactEvidence!.Generation : string.Empty,
                Engine.GetPhysicsFrames(),
                (float)delta)
            {
                ClockGeneration = navigation.LastSustainedClockGeneration,
                ActorTransform = _runtime.Actor.GlobalTransform,
            });
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (ReferenceEquals(_runtime.Navigation.ContactLockCoordinator, Coordinator))
            {
                _runtime.Navigation.ContactLockCoordinator = null;
            }
            Coordinator.Teardown();
        }

    }

    private static Transform3D GetBoneWorldPose(Skeleton3D skeleton, int bone)
        => skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone);
}
