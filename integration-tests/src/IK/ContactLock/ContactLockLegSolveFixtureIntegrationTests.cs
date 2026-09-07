using AlleyCat.IK;
using AlleyCat.Rigging.Physics;
using AlleyCat.Tests.IK;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK.ContactLock;

/// <summary>
/// Stage 5 Gate A/B coverage using reusable, neutral player and NPC lower-leg fixture roles.
/// </summary>
public sealed class ContactLockLegSolveFixtureIntegrationTests
{
    private const string FixtureScenePath = "res://tests/ik/contact_lock_leg_solve_fixture.tscn";
    private const float FootAnchorToleranceMetres = 0.006f;
    private const float AnchorToTargetDiagnosticToleranceMetres = 0.0001f;

    /// <summary>
    /// Gate A proves an admitted synthetic anchor is resolved before native leg consumers without moving the root, hips, or opposite foot.
    /// </summary>
    [Fact]
    public async Task GateA_AdmittedAnchorResolvesWithoutMovingRootHipsOrOppositeFoot()
    {
        SceneTree sceneTree = GetSceneTree();
        foreach (FixtureFoot foot in Enum.GetValues<FixtureFoot>())
        {
            await using ContactLockLegSolveFixture fixture = await ContactLockLegSolveFixture.CreateAsync(sceneTree);
            FixtureFoot oppositeFoot = foot == FixtureFoot.Left ? FixtureFoot.Right : FixtureFoot.Left;
            Transform3D footBefore = fixture.GetFootWorldPose(foot);
            Transform3D oppositeBefore = fixture.GetFootWorldPose(oppositeFoot);
            Transform3D rootBefore = fixture.Root.GlobalTransform;
            Transform3D hipsBefore = fixture.GetBonePose("Hips");
            Transform3D anchor = fixture.CreateAnchorInsideReach(foot);
            Transform3D providerTarget = new(footBefore.Basis, footBefore.Origin + new Vector3(0.03f, 0.0f, 0.0f));

            fixture.SetProviderIntent(foot, providerTarget);
            fixture.SetProviderIntent(oppositeFoot, oppositeBefore);
            fixture.SetEvidence(FixtureContactEvidence.Admitted(foot, anchor));
            await fixture.ProcessSkeletonCyclesAsync(1);

            AssertStageOrder(fixture, foot);
            Assert.True(fixture.GetContactLockSnapshot(foot).TargetPublished);
            Assert.Equal(1.0f, fixture.GetPoleStage(foot).Influence);
            AssertTransformApproximately(anchor, fixture.GetCharacterIKFootTarget(foot).GlobalTransform);
            AssertTransformApproximately(oppositeBefore, fixture.GetCharacterIKFootTarget(oppositeFoot).GlobalTransform);
            AssertNativeSolveReachesTarget(fixture, foot, anchor);
            AssertTransformApproximately(rootBefore, fixture.Root.GlobalTransform);
            AssertTransformApproximately(hipsBefore, fixture.GetBonePose("Hips"));
            AssertTransformApproximately(oppositeBefore, fixture.GetFootWorldPose(oppositeFoot));
        }
    }

    /// <summary>
    /// Gate A independently records whether the native lower-leg solvers consume an admitted anchor.
    /// This remains strict even when target actuation fails, so the solve residual is not hidden by an earlier assertion.
    /// </summary>
    [Fact]
    public async Task GateA_AdmittedAnchorNativeLegSolveReachesAnchor()
    {
        SceneTree sceneTree = GetSceneTree();
        foreach (FixtureFoot foot in Enum.GetValues<FixtureFoot>())
        {
            await using ContactLockLegSolveFixture fixture = await ContactLockLegSolveFixture.CreateAsync(sceneTree);
            Transform3D anchor = fixture.CreateAnchorInsideReach(foot);

            fixture.SetProviderIntent(foot, fixture.GetFootWorldPose(foot));
            fixture.SetEvidence(FixtureContactEvidence.Admitted(foot, anchor));
            await fixture.ProcessSkeletonCyclesAsync(1);

            AssertNativeSolveReachesTarget(fixture, foot, anchor);
            Assert.True(
                fixture.GetContactLockSnapshot(foot).TargetPublished,
                $"Expected {foot} provider anchor publication. "
                + fixture.GetNativeTwoBonePipelineEvidence(foot).Format(
                    fixture.Role,
                    foot,
                    anchor,
                    fixture.GetCharacterIKFootTarget(foot).GlobalTransform,
                    fixture.GetConfiguredEndBoneWorldPose(foot)));
        }
    }

    /// <summary>
    /// Proves that separately instantiated player and NPC roles each run their own provider-to-native-leg-solve chain.
    /// The player admits its left anchor while the NPC admits its right anchor, leaving the opposite sides to their
    /// ordinary providers. This makes role and side state leakage observable without production wiring.
    /// </summary>
    [Fact]
    public async Task GateA_PlayerAndNPC_IndependentlyResolveAnchorsAndOrdinaryFallbacksThroughNativeLegSolvers()
    {
        SceneTree sceneTree = GetSceneTree();
        await using ContactLockTwoRoleFixture roles = await ContactLockTwoRoleFixture.CreateAsync(sceneTree);

        ContactLockLegSolveFixture player = roles.Player;
        ContactLockLegSolveFixture npc = roles.NPC;
        Transform3D playerLeftAnchor = player.CreateAnchorInsideReach(FixtureFoot.Left);
        Transform3D playerRightOrdinaryTarget = OffsetInsideReach(player, FixtureFoot.Right, new Vector3(0.008f, 0.0f, 0.0f));
        Transform3D npcRightAnchor = npc.CreateAnchorInsideReach(FixtureFoot.Right);
        Transform3D npcLeftOrdinaryTarget = OffsetInsideReach(npc, FixtureFoot.Left, new Vector3(-0.008f, 0.0f, 0.0f));

        player.SetProviderIntent(FixtureFoot.Left, player.CreateAnchorInsideReach(FixtureFoot.Left));
        player.SetProviderIntent(FixtureFoot.Right, playerRightOrdinaryTarget);
        player.SetEvidence(FixtureContactEvidence.Admitted(FixtureFoot.Left, playerLeftAnchor));
        npc.SetProviderIntent(FixtureFoot.Left, npcLeftOrdinaryTarget);
        npc.SetProviderIntent(FixtureFoot.Right, npc.CreateAnchorInsideReach(FixtureFoot.Right));
        npc.SetEvidence(FixtureContactEvidence.Admitted(FixtureFoot.Right, npcRightAnchor));

        await roles.ProcessSkeletonCyclesAsync(1);

        AssertRoleSideEvidence(player, FixtureFoot.Left, playerLeftAnchor, targetPublished: true);
        AssertRoleSideEvidence(player, FixtureFoot.Right, playerRightOrdinaryTarget, targetPublished: false);
        AssertRoleSideEvidence(npc, FixtureFoot.Left, npcLeftOrdinaryTarget, targetPublished: false);
        AssertRoleSideEvidence(npc, FixtureFoot.Right, npcRightAnchor, targetPublished: true);

        Assert.NotEqual(player.Root.GetInstanceId(), npc.Root.GetInstanceId());
        Assert.NotEqual(player.Skeleton.GetInstanceId(), npc.Skeleton.GetInstanceId());
        Assert.NotEqual(player.GetCharacterIKFootTarget(FixtureFoot.Left).GetInstanceId(), npc.GetCharacterIKFootTarget(FixtureFoot.Left).GetInstanceId());
    }

    /// <summary>
    /// Gate A proves that CharacterIK explicit-provider precedence occurs before test-only contact authority. With no
    /// anchor, this fixture explicitly passes through its ordinary provider target rather than publishing a contact target.
    /// </summary>
    [Fact]
    public async Task GateA_ProviderPrecedenceThenContactAuthorityAndNoAnchorPassThrough()
    {
        SceneTree sceneTree = GetSceneTree();
        await using ContactLockLegSolveFixture fixture = await ContactLockLegSolveFixture.CreateAsync(sceneTree);
        Transform3D providerTarget = fixture.CreateAnchorInsideReach(FixtureFoot.Left);
        providerTarget = new(providerTarget.Basis, providerTarget.Origin + new Vector3(0.012f, 0.0f, 0.0f));
        fixture.SetProviderIntent(FixtureFoot.Left, providerTarget);
        fixture.SetEvidence(FixtureContactEvidence.NoAnchor());
        await fixture.ProcessSkeletonCyclesAsync(3);

        Assert.False(fixture.GetContactLockSnapshot(FixtureFoot.Left).TargetPublished);
        AssertTransformApproximately(providerTarget, fixture.GetCharacterIKFootTarget(FixtureFoot.Left).GlobalTransform);
        AssertNativeSolveReachesTarget(fixture, FixtureFoot.Left, providerTarget);

        Transform3D contactAnchor = fixture.CreateAnchorInsideReach(FixtureFoot.Left);
        fixture.SetEvidence(FixtureContactEvidence.Admitted(FixtureFoot.Left, contactAnchor));
        await fixture.ProcessSkeletonCyclesAsync(3);

        Assert.True(fixture.GetContactLockSnapshot(FixtureFoot.Left).TargetPublished);
    }

    /// <summary>
    /// Verifies each foot resolves an independent ordered provider chain without replaying one foot's contact intent.
    /// </summary>
    [Fact]
    public async Task GateA_PerFootProviderChains_IsolateContactAuthority()
    {
        SceneTree sceneTree = GetSceneTree();
        await using ContactLockLegSolveFixture fixture = await ContactLockLegSolveFixture.CreateAsync(sceneTree);
        Transform3D leftAnchor = fixture.CreateAnchorInsideReach(FixtureFoot.Left);
        Transform3D rightProviderTarget = fixture.CreateAnchorInsideReach(FixtureFoot.Right);
        rightProviderTarget = new(rightProviderTarget.Basis, rightProviderTarget.Origin + new Vector3(0.008f, 0.0f, 0.0f));

        fixture.SetProviderIntent(FixtureFoot.Left, fixture.CreateAnchorInsideReach(FixtureFoot.Left));
        fixture.SetProviderIntent(FixtureFoot.Right, rightProviderTarget);
        fixture.SetEvidence(FixtureContactEvidence.Admitted(FixtureFoot.Left, leftAnchor));
        await fixture.ProcessSkeletonCyclesAsync(1);

        Assert.True(fixture.GetContactLockSnapshot(FixtureFoot.Left).TargetPublished);
        Assert.False(fixture.GetContactLockSnapshot(FixtureFoot.Right).TargetPublished);
        AssertTransformApproximately(leftAnchor, fixture.GetCharacterIKFootTarget(FixtureFoot.Left).GlobalTransform);
        AssertTransformApproximately(rightProviderTarget, fixture.GetCharacterIKFootTarget(FixtureFoot.Right).GlobalTransform);
        AssertNativeSolveReachesTarget(fixture, FixtureFoot.Left, leftAnchor);
        AssertNativeSolveReachesTarget(fixture, FixtureFoot.Right, rightProviderTarget);
    }

    /// <summary>
    /// Gate B fails closed for evidence that cannot safely authorise a target override.
    /// </summary>
    [Fact]
    public async Task GateB_MissingEmptyStaleInvalidAndContradictoryEvidenceFailsClosed()
    {
        SceneTree sceneTree = GetSceneTree();
        foreach (FixtureContactEvidence evidence in FixtureContactEvidence.InvalidCases())
        {
            await using ContactLockLegSolveFixture fixture = await ContactLockLegSolveFixture.CreateAsync(sceneTree);
            Transform3D providerTarget = fixture.CreateAnchorInsideReach(FixtureFoot.Left);
            fixture.SetProviderIntent(FixtureFoot.Left, providerTarget);
            fixture.SetEvidence(evidence);
            await fixture.ProcessSkeletonCyclesAsync(3);

            Assert.False(fixture.GetContactLockSnapshot(FixtureFoot.Left).TargetPublished, evidence.State.ToString());
        }
    }

    /// <summary>
    /// Gate B retains the historical invalid-evidence control. The matrix below expands this across both fixture roles
    /// and feet with auditable records and signal-time native snapshots.
    /// </summary>
    [Fact]
    public async Task GateB_InvalidPreIKBlocksPublishAndPostIKRequiresAnchorCoherence()
    {
        SceneTree sceneTree = GetSceneTree();
        await using ContactLockLegSolveFixture fixture = await ContactLockLegSolveFixture.CreateAsync(sceneTree);
        Transform3D plausibleProviderTarget = fixture.CreateAnchorInsideReach(FixtureFoot.Left);
        fixture.SetProviderIntent(FixtureFoot.Left, plausibleProviderTarget);
        fixture.SetEvidence(FixtureContactEvidence.Missing());
        await fixture.ProcessSkeletonCyclesAsync(3);

        ContactLockStageSnapshot blocked = fixture.GetContactLockSnapshot(FixtureFoot.Left);
        Assert.False(blocked.TargetPublished);
        Assert.False(FixturePostIKGate.IsCoherent(blocked, fixture.GetFootWorldPose(FixtureFoot.Left)));

        Vector3 residual = new(0.014f, 0.0f, -0.007f);
        fixture.SetEvidence(FixtureContactEvidence.Unproven(FixtureFoot.Left, plausibleProviderTarget, residual));
        await fixture.ProcessSkeletonCyclesAsync(3);

        ContactLockStageSnapshot deferred = fixture.GetContactLockSnapshot(FixtureFoot.Left);
        Assert.Equal(FixtureDisposition.DeferredRecoveryRequired, deferred.Disposition);
        Assert.Equal(residual, deferred.Residual);
        Assert.False(deferred.TargetPublished);
        Assert.False(FixturePostIKGate.IsCoherent(deferred, fixture.GetFootWorldPose(FixtureFoot.Left)));
    }

    /// <summary>
    /// Gate B proves that every invalid contact-evidence class remains refused for each role and foot, even when the
    /// ordinary provider supplies a reachable target that the native solver successfully reaches.
    /// </summary>
    [Fact]
    public async Task GateB_PreIKEvidenceMatrix_RejectsInvalidContactDespitePlausibleOrdinaryFallbackSolve()
    {
        SceneTree sceneTree = GetSceneTree();
        await using ContactLockTwoRoleFixture roles = await ContactLockTwoRoleFixture.CreateAsync(sceneTree);

        foreach (ContactLockLegSolveFixture fixture in roles.Fixtures)
        {
            foreach (FixtureFoot foot in Enum.GetValues<FixtureFoot>())
            {
                Transform3D decisionProbeFallbackTarget = OffsetInsideReach(
                    fixture,
                    foot,
                    foot == FixtureFoot.Left ? new Vector3(0.006f, 0.0f, 0.0f) : new Vector3(-0.006f, 0.0f, 0.0f));
                await AssertPreIKDecisionTracksInjectedEvidenceData(
                    roles,
                    fixture,
                    foot,
                    decisionProbeFallbackTarget);

                foreach (FixtureGateBPreIKClass evidenceClass in GetInvalidPreIKEvidenceClasses())
                {
                    Transform3D ordinaryFallbackTarget = OffsetInsideReach(
                        fixture,
                        foot,
                        foot == FixtureFoot.Left ? new Vector3(0.009f, 0.0f, 0.0f) : new Vector3(-0.009f, 0.0f, 0.0f));
                    FixtureEvidencePath source = CreateGateBEvidencePath(
                        fixture,
                        foot,
                        evidenceClass,
                        ordinaryFallbackTarget);
                    fixture.SetProviderIntent(foot, ordinaryFallbackTarget);
                    fixture.SetGateBEvidence(source);

                    await roles.ProcessSkeletonCyclesAsync(1);

                    ContactLockStageSnapshot stage = fixture.GetContactLockSnapshot(foot);
                    FixturePreIKDecision preIKDecision = stage.TargetPublished
                        ? FixturePreIKDecision.Accepted
                        : FixturePreIKDecision.Rejected;
                    FixtureSelectedProvider selectedProvider = stage.TargetPublished
                        ? FixtureSelectedProvider.ContactEvidence
                        : FixtureSelectedProvider.OrdinaryFallback;
                    FixtureNativeSolverSnapshot nativeSnapshot = fixture.CaptureNativeSolverSnapshot(foot);
                    AssertFreshNativeSnapshot(source, nativeSnapshot);
                    FixturePostIKDecision postIKDecision = FixturePostIKValidator.Validate(source, preIKDecision, nativeSnapshot);
                    FixtureGateBEvidenceRecord record = new(
                        source,
                        preIKDecision,
                        selectedProvider,
                        nativeSnapshot,
                        null,
                        null,
                        postIKDecision,
                        FixtureBlockingOutcome.PreIKFailureRetainedDespiteFallbackSolve);
                    fixture.RetainGateBEvidence(record);

                    Assert.False(stage.TargetPublished, record.Format());
                    Assert.Equal(FixtureSelectedProvider.OrdinaryFallback, selectedProvider);
                    Assert.Equal(FixturePostIKDecision.PreIKRejected, postIKDecision);
                    if (evidenceClass == FixtureGateBPreIKClass.Contradictory)
                    {
                        Assert.Equal(2, source.Contacts.Count);
                        Assert.All(source.Contacts, contact =>
                        {
                            Assert.Equal(source.Role, contact.Role);
                            Assert.Equal(source.Foot, contact.Foot);
                            Assert.Equal(source.Generation, contact.Generation);
                        });
                        Assert.True(
                            source.Contacts[0].Anchor.Origin.DistanceTo(source.Contacts[1].Anchor.Origin) > AnchorToTargetDiagnosticToleranceMetres,
                            "Contradictory evidence must contain mutually incompatible anchors for the same role, foot, and generation.");
                    }

                    AssertTransformApproximately(ordinaryFallbackTarget, fixture.GetCharacterIKFootTarget(foot).GlobalTransform);
                    AssertNativeSolveReachesTarget(fixture, foot, ordinaryFallbackTarget);
                    Assert.Equal(FixtureBlockingOutcome.PreIKFailureRetainedDespiteFallbackSolve, record.BlockingOutcome);
                    Assert.True(record.HasRequiredAuditFields, record.Format());
                }
            }
        }

        foreach (ContactLockLegSolveFixture fixture in roles.Fixtures)
        {
            Assert.Equal(10, fixture.GateBEvidenceRecords.Count);
        }
    }

    /// <summary>
    /// Gate B independently rejects every specified incoherent post-IK output after a valid contact publication.
    /// Each negative case is injected at the fixture's <c>ModificationProcessed</c> boundary; the native snapshot is
    /// retained separately so a downstream native success cannot promote the injected post-IK result.
    /// </summary>
    [Fact]
    public async Task GateB_PostIKOutputMatrix_InjectsDistinctSignalBoundaryOutputsWithNativeProvenance()
    {
        SceneTree sceneTree = GetSceneTree();
        await using ContactLockTwoRoleFixture roles = await ContactLockTwoRoleFixture.CreateAsync(sceneTree);

        foreach (ContactLockLegSolveFixture fixture in roles.Fixtures)
        {
            foreach (FixtureFoot foot in Enum.GetValues<FixtureFoot>())
            {
                Transform3D ordinaryFallbackTarget = OffsetInsideReach(
                    fixture,
                    foot,
                    foot == FixtureFoot.Left ? new Vector3(0.007f, 0.0f, 0.0f) : new Vector3(-0.007f, 0.0f, 0.0f));
                fixture.SetProviderIntent(foot, ordinaryFallbackTarget);

                foreach (FixtureGateBPostIKClass outputClass in GetInvalidPostIKOutputClasses())
                {
                    FixtureEvidencePath source = CreateGateBEvidencePath(
                        fixture,
                        foot,
                        FixtureGateBPreIKClass.Admitted,
                        ordinaryFallbackTarget);
                    fixture.SetGateBEvidence(source);
                    fixture.InjectPostIKOutputAtNextModificationProcessed(source, outputClass);
                    await roles.ProcessSkeletonCyclesAsync(1);

                    ContactLockStageSnapshot stage = fixture.GetContactLockSnapshot(foot);
                    Assert.True(stage.TargetPublished);
                    FixturePostIKSignalBoundary boundary = fixture.RequirePostIKSignalBoundary(foot);
                    AssertFreshNativeSnapshot(source, boundary.NativeSnapshot);
                    FixturePostIKDecision postIKDecision = FixturePostIKValidator.Validate(
                        source,
                        FixturePreIKDecision.Accepted,
                        boundary);
                    FixtureGateBEvidenceRecord record = new(
                        source,
                        FixturePreIKDecision.Accepted,
                        FixtureSelectedProvider.ContactEvidence,
                        boundary.NativeSnapshot,
                        outputClass,
                        boundary,
                        postIKDecision,
                        FixtureBlockingOutcome.PostIKFailureBlocksFutureIntegration);
                    fixture.RetainGateBEvidence(record);

                    Assert.Equal(ToPostIKDecision(outputClass), postIKDecision);
                    Assert.Equal(FixtureBlockingOutcome.PostIKFailureBlocksFutureIntegration, record.BlockingOutcome);
                    AssertPostIKInjectionAuditRecord(record, outputClass);
                    Assert.True(record.HasRequiredAuditFields, record.Format());
                    Console.WriteLine($"GATE_B_POST_IK {record.Format()}");
                }
            }
        }

        foreach (ContactLockLegSolveFixture fixture in roles.Fixtures)
        {
            Assert.Equal(10, fixture.GateBEvidenceRecords.Count);
        }
    }

    private static void AssertStageOrder(ContactLockLegSolveFixture fixture, FixtureFoot foot)
    {
        SkeletonModifier3D pole = fixture.GetPoleStage(foot);
        TwoBoneIK3D twoBone = fixture.GetTwoBoneStage(foot);
        string order = $"sync={fixture.FootSyncController.GetIndex()}, begin={fixture.CharacterIKBeginStage.GetIndex()}, provider={fixture.CharacterIKProviderStage.GetIndex()}, pole={pole.GetIndex()}, twoBone={twoBone.GetIndex()}";
        Assert.True(fixture.FootSyncController.GetIndex() < fixture.CharacterIKBeginStage.GetIndex(), order);
        Assert.True(fixture.CharacterIKBeginStage.GetIndex() < fixture.CharacterIKProviderStage.GetIndex(), order);
        Assert.True(fixture.CharacterIKProviderStage.GetIndex() < pole.GetIndex(), order);
        Assert.True(pole.GetIndex() < twoBone.GetIndex(), order);
        Assert.True(
            fixture.GetContactIntentProviderInvocationCount(foot) > 0,
            $"Expected {fixture.Role} {foot} CharacterIK provider stage to resolve the fixture contact provider before the pole solve.");
    }

    private static void AssertTransformApproximately(Transform3D expected, Transform3D actual)
    {
        float positionDistance = expected.Origin.DistanceTo(actual.Origin);
        Assert.True(
            positionDistance <= 0.0001f,
            $"Expected target position {expected.Origin}, actual {actual.Origin}, distance {positionDistance:F4} m.");
        Assert.True(
            expected.Basis.Orthonormalized().IsEqualApprox(actual.Basis.Orthonormalized()),
            $"Expected target basis {expected.Basis}, actual {actual.Basis}.");
    }

    private static void AssertNativeSolveReachesTarget(
        ContactLockLegSolveFixture fixture,
        FixtureFoot foot,
        Transform3D anchor)
    {
        NativeTwoBonePipelineEvidence evidence = fixture.GetNativeTwoBonePipelineEvidence(foot);
        Transform3D postPipelineTarget = fixture.GetCharacterIKFootTarget(foot).GlobalTransform;
        Transform3D postPipelineConfiguredEndBone = fixture.GetConfiguredEndBoneWorldPose(foot);
        float anchorToTargetError = evidence.TargetAtProcessing.Origin.DistanceTo(anchor.Origin);
        float endpointToTargetError = evidence.EndBoneAtProcessing.Origin.DistanceTo(evidence.TargetAtProcessing.Origin);

        Assert.True(
            evidence.ProcessCount > 0,
            $"Native {fixture.Role} {foot} TwoBoneIK3D did not emit ModificationProcessed through Skeleton3D's modifier pipeline. "
            + evidence.Format(fixture.Role, foot, anchor, postPipelineTarget, postPipelineConfiguredEndBone));
        Assert.True(
            evidence.RootBoneIndex >= 0 && evidence.MiddleBoneIndex >= 0 && evidence.EndBoneIndex >= 0,
            $"Native {fixture.Role} {foot} TwoBoneIK3D reported an unresolved bone index at ModificationProcessed; "
            + "this guards the engine p_bone = -1 / bone_idx=-1 failure at pipeline execution time. "
            + evidence.Format(fixture.Role, foot, anchor, postPipelineTarget, postPipelineConfiguredEndBone));
        Assert.True(
            anchorToTargetError <= AnchorToTargetDiagnosticToleranceMetres,
            $"Native {fixture.Role} {foot} TwoBoneIK3D processed before the provider anchor reached its target. "
            + $"Anchor-to-target diagnostic error: {anchorToTargetError:F4} m, tolerance: {AnchorToTargetDiagnosticToleranceMetres:F4} m. "
            + evidence.Format(fixture.Role, foot, anchor, postPipelineTarget, postPipelineConfiguredEndBone));
        Assert.True(
            evidence.EndBoneAtProcessing.Origin.DistanceTo(evidence.TargetAtProcessing.Origin) <= FootAnchorToleranceMetres,
            $"Native {fixture.Role} {foot} leg solve did not place configured end-bone '{fixture.GetConfiguredEndBoneName(foot)}' origin at CharacterIK's signal-time target when its ModificationProcessed signal completed. "
            + $"Native endpoint-to-target error: {endpointToTargetError:F4} m, tolerance: {FootAnchorToleranceMetres:F4} m. "
            + evidence.Format(fixture.Role, foot, anchor, postPipelineTarget, postPipelineConfiguredEndBone));
    }

    private static Transform3D OffsetInsideReach(ContactLockLegSolveFixture fixture, FixtureFoot foot, Vector3 offset)
    {
        Transform3D target = fixture.CreateAnchorInsideReach(foot);
        return new Transform3D(target.Basis, target.Origin + offset);
    }

    private static FixtureEvidencePath CreateGateBEvidencePath(
        ContactLockLegSolveFixture fixture,
        FixtureFoot foot,
        FixtureGateBPreIKClass evidenceClass,
        Transform3D ordinaryFallbackTarget)
    {
        long generation = fixture.NextNativeSnapshotGeneration;
        Transform3D anchor = fixture.CreateAnchorInsideReach(foot);
        var sharedEvidence = ContactLockGateBEvidence.Create(
            fixture.Role.ToString(),
            foot.ToString(),
            generation,
            ToSharedInjectedEvidence(evidenceClass),
            anchor,
            ordinaryFallbackTarget);
        IReadOnlyList<FixtureGateBContactRecord> contacts = [.. sharedEvidence.Contacts
            .Select(contact => new FixtureGateBContactRecord(
                Enum.Parse<FixtureRole>(contact.Role),
                Enum.Parse<FixtureFoot>(contact.Foot),
                contact.Generation,
                contact.Anchor,
                contact.IsStale,
                contact.IsValid))];

        return new FixtureEvidencePath(
            fixture.Role,
            foot,
            generation,
            evidenceClass,
            sharedEvidence.ExpectedAnchor,
            ordinaryFallbackTarget,
            contacts);
    }

    private static ContactLockGateBInjectedEvidence ToSharedInjectedEvidence(FixtureGateBPreIKClass evidenceClass)
        => evidenceClass switch
        {
            FixtureGateBPreIKClass.Admitted => ContactLockGateBInjectedEvidence.Admitted,
            FixtureGateBPreIKClass.Missing => ContactLockGateBInjectedEvidence.Missing,
            FixtureGateBPreIKClass.Stale => ContactLockGateBInjectedEvidence.Stale,
            FixtureGateBPreIKClass.Invalid => ContactLockGateBInjectedEvidence.Invalid,
            FixtureGateBPreIKClass.Contradictory => ContactLockGateBInjectedEvidence.Contradictory,
            FixtureGateBPreIKClass.FallbackMasking => ContactLockGateBInjectedEvidence.FallbackMasking,
            _ => throw new ArgumentOutOfRangeException(nameof(evidenceClass), evidenceClass, null),
        };

    /// <summary>
    /// Proves that the pre-IK provider adjudicates the injected records, not the audit-only case label. The label is
    /// deliberately held constant while a valid, finite contact becomes invalid.
    /// </summary>
    private static async Task AssertPreIKDecisionTracksInjectedEvidenceData(
        ContactLockTwoRoleFixture roles,
        ContactLockLegSolveFixture fixture,
        FixtureFoot foot,
        Transform3D ordinaryFallbackTarget)
    {
        FixtureEvidencePath admittedDataWithInvalidLabel = CreateGateBEvidencePath(
            fixture,
            foot,
            FixtureGateBPreIKClass.Admitted,
            ordinaryFallbackTarget) with
        {
            InjectedClass = FixtureGateBPreIKClass.Invalid
        };
        fixture.SetProviderIntent(foot, ordinaryFallbackTarget);
        fixture.SetGateBEvidence(admittedDataWithInvalidLabel);
        await roles.ProcessSkeletonCyclesAsync(1);

        ContactLockStageSnapshot accepted = fixture.GetContactLockSnapshot(foot);
        Assert.True(
            accepted.TargetPublished,
            "A valid finite contact must publish even when its audit-only injected-case label says Invalid.");
        Assert.Equal(FixtureEvidenceState.NoAnchor, FixtureContactEvidence.GateB(admittedDataWithInvalidLabel).State);

        FixtureEvidencePath invalidDataWithSameLabel = admittedDataWithInvalidLabel with
        {
            Contacts = [admittedDataWithInvalidLabel.Contacts[0] with { IsValid = false }]
        };
        fixture.SetGateBEvidence(invalidDataWithSameLabel);
        await roles.ProcessSkeletonCyclesAsync(1);

        ContactLockStageSnapshot rejected = fixture.GetContactLockSnapshot(foot);
        Assert.Equal(admittedDataWithInvalidLabel.InjectedClass, invalidDataWithSameLabel.InjectedClass);
        Assert.True(admittedDataWithInvalidLabel.Contacts[0].IsValid);
        Assert.False(invalidDataWithSameLabel.Contacts[0].IsValid);
        Assert.True(accepted.TargetPublished);
        Assert.False(rejected.TargetPublished);
    }

    private static FixtureInjectedPostIKOutput CreatePostIKInjectedOutput(
        FixtureNativeSolverSnapshot nativeSnapshot,
        FixtureGateBPostIKClass outputClass)
    {
        NativeTwoBonePipelineEvidence evidence = nativeSnapshot.SignalTimeEvidence
            ?? throw new Xunit.Sdk.XunitException("Expected a fresh native snapshot before post-IK injection.");

        FixtureNativeSolverSnapshot injectedSnapshot = outputClass switch
        {
            FixtureGateBPostIKClass.Missing => nativeSnapshot with { SignalTimeEvidence = null },
            FixtureGateBPostIKClass.Stale => nativeSnapshot with { Generation = nativeSnapshot.Generation - 1 },
            FixtureGateBPostIKClass.WrongFoot => nativeSnapshot with
            {
                Foot = nativeSnapshot.Foot == FixtureFoot.Left ? FixtureFoot.Right : FixtureFoot.Left
            },
            FixtureGateBPostIKClass.NonFinite => nativeSnapshot with
            {
                SignalTimeEvidence = evidence with
                {
                    TargetAtProcessing = new Transform3D(evidence.TargetAtProcessing.Basis, new Vector3(float.NaN, 0.0f, 0.0f))
                }
            },
            FixtureGateBPostIKClass.AnchorIncoherent => nativeSnapshot with
            {
                SignalTimeEvidence = evidence with
                {
                    TargetAtProcessing = new Transform3D(
                        evidence.TargetAtProcessing.Basis,
                        evidence.TargetAtProcessing.Origin + new Vector3(0.02f, 0.0f, 0.0f))
                }
            },
            FixtureGateBPostIKClass.Accepted => nativeSnapshot,
            _ => throw new ArgumentOutOfRangeException(nameof(outputClass), outputClass, null)
        };

        return new FixtureInjectedPostIKOutput(
            outputClass,
            injectedSnapshot.Role,
            injectedSnapshot.Foot,
            injectedSnapshot.Generation,
            injectedSnapshot.SignalTimeEvidence);
    }

    private static FixturePostIKDecision ToPostIKDecision(FixtureGateBPostIKClass outputClass)
        => outputClass switch
        {
            FixtureGateBPostIKClass.Accepted => FixturePostIKDecision.Accepted,
            FixtureGateBPostIKClass.Missing => FixturePostIKDecision.Missing,
            FixtureGateBPostIKClass.Stale => FixturePostIKDecision.Stale,
            FixtureGateBPostIKClass.WrongFoot => FixturePostIKDecision.WrongFoot,
            FixtureGateBPostIKClass.NonFinite => FixturePostIKDecision.NonFinite,
            FixtureGateBPostIKClass.AnchorIncoherent => FixturePostIKDecision.AnchorIncoherent,
            _ => throw new ArgumentOutOfRangeException(nameof(outputClass), outputClass, null)
        };

    private static void AssertFreshNativeSnapshot(FixtureEvidencePath source, FixtureNativeSolverSnapshot snapshot)
    {
        Assert.Equal(source.Role, snapshot.Role);
        Assert.Equal(source.Foot, snapshot.Foot);
        Assert.Equal(source.Generation, snapshot.Generation);
        NativeTwoBonePipelineEvidence evidence = snapshot.SignalTimeEvidence
            ?? throw new Xunit.Sdk.XunitException("Expected fresh ModificationProcessed signal-time evidence.");
        Assert.True(evidence.ProcessCount > 0, "Expected the native signal-time snapshot to contain at least one modification pass.");
    }

    private static void AssertPostIKInjectionAuditRecord(
        FixtureGateBEvidenceRecord record,
        FixtureGateBPostIKClass expectedInjectedClass)
    {
        Assert.Equal(expectedInjectedClass, record.InjectedPostIKClass);
        FixturePostIKSignalBoundary boundary = record.PostIKSignalBoundary
            ?? throw new Xunit.Sdk.XunitException("Post-IK matrix records must retain their ModificationProcessed injection boundary.");
        Assert.Equal(expectedInjectedClass, boundary.InjectedOutput.InjectedClass);
        Assert.Equal(record.NativeSolverSnapshot, boundary.NativeSnapshot);
        Assert.Equal(record.SourceEvidence.Role, record.NativeSolverSnapshot.Role);
        Assert.Equal(record.SourceEvidence.Foot, record.NativeSolverSnapshot.Foot);
        Assert.Equal(record.SourceEvidence.Generation, record.NativeSolverSnapshot.Generation);

        FixtureInjectedPostIKOutput injectedOutput = boundary.InjectedOutput;
        switch (expectedInjectedClass)
        {
            case FixtureGateBPostIKClass.Missing:
                Assert.Null(injectedOutput.SignalTimeEvidence);
                break;
            case FixtureGateBPostIKClass.Stale:
                Assert.Equal(record.SourceEvidence.Generation - 1, injectedOutput.Generation);
                break;
            case FixtureGateBPostIKClass.WrongFoot:
                Assert.Equal(record.SourceEvidence.Role, injectedOutput.Role);
                Assert.NotEqual(record.SourceEvidence.Foot, injectedOutput.Foot);
                break;
            case FixtureGateBPostIKClass.NonFinite:
                NativeTwoBonePipelineEvidence nonFiniteEvidence = injectedOutput.SignalTimeEvidence
                    ?? throw new Xunit.Sdk.XunitException("Non-finite injection must retain signal-time evidence.");
                Assert.True(float.IsNaN(nonFiniteEvidence.TargetAtProcessing.Origin.X));
                break;
            case FixtureGateBPostIKClass.AnchorIncoherent:
                NativeTwoBonePipelineEvidence incoherentEvidence = injectedOutput.SignalTimeEvidence
                    ?? throw new Xunit.Sdk.XunitException("Anchor-incoherent injection must retain signal-time evidence.");
                Transform3D expectedAnchor = record.SourceEvidence.ExpectedAnchor
                    ?? throw new Xunit.Sdk.XunitException("Accepted source evidence must retain an anchor.");
                Assert.True(
                    incoherentEvidence.TargetAtProcessing.Origin.DistanceTo(expectedAnchor.Origin) > AnchorToTargetDiagnosticToleranceMetres);
                break;
            case FixtureGateBPostIKClass.Accepted:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(expectedInjectedClass), expectedInjectedClass, null);
        }
    }

    private static void AssertRoleSideEvidence(
        ContactLockLegSolveFixture fixture,
        FixtureFoot foot,
        Transform3D expectedTarget,
        bool targetPublished)
    {
        AssertStageOrder(fixture, foot);
        ContactLockStageSnapshot snapshot = fixture.GetContactLockSnapshot(foot);
        NativeTwoBonePipelineEvidence evidence = fixture.GetNativeTwoBonePipelineEvidence(foot);
        Transform3D actualTarget = fixture.GetCharacterIKFootTarget(foot).GlobalTransform;
        Transform3D configuredEndBone = fixture.GetConfiguredEndBoneWorldPose(foot);
        string diagnostic = evidence.Format(fixture.Role, foot, expectedTarget, actualTarget, configuredEndBone);

        Assert.Equal(targetPublished, snapshot.TargetPublished);
        Assert.True(
            actualTarget.Origin.DistanceTo(expectedTarget.Origin) <= AnchorToTargetDiagnosticToleranceMetres,
            $"{fixture.Role} {foot} target did not retain its independently configured {(targetPublished ? "anchor" : "ordinary fallback")}. {diagnostic}");
        AssertNativeSolveReachesTarget(fixture, foot, expectedTarget);
    }

    private enum FixtureFoot
    {
        Left, Right
    }

    private enum FixtureRole
    {
        Player,
        NPC,
    }

    private enum FixtureEvidenceState
    {
        NoAnchor, Admitted, Missing, Empty, Stale, Invalid, Contradictory, Released, Unproven
    }

    private enum FixtureDisposition
    {
        Applied, DeferredRecoveryRequired, FailedClosed
    }

    private readonly record struct FixtureContactRecord(FixtureFoot Foot, Transform3D Anchor, bool IsStale, bool IsValid);

    private readonly record struct FixtureContactEvidence(
        FixtureEvidenceState State,
        IReadOnlyList<FixtureContactRecord> Contacts,
        Vector3 Residual,
        FixtureEvidencePath? GateBPath = null)
    {
        public static FixtureContactEvidence NoAnchor() => new(FixtureEvidenceState.NoAnchor, [], Vector3.Zero);
        public static FixtureContactEvidence Missing() => new(FixtureEvidenceState.Missing, [], Vector3.Zero);
        public static FixtureContactEvidence Released() => new(FixtureEvidenceState.Released, [], Vector3.Zero);
        public static FixtureContactEvidence Admitted(FixtureFoot foot, Transform3D anchor)
            => new(FixtureEvidenceState.Admitted, [new FixtureContactRecord(foot, anchor, false, true)], Vector3.Zero);
        public static FixtureContactEvidence Unproven(FixtureFoot foot, Transform3D anchor, Vector3 residual)
            => new(FixtureEvidenceState.Unproven, [new FixtureContactRecord(foot, anchor, false, true)], residual);
        public static FixtureContactEvidence GateB(FixtureEvidencePath path)
            => new(
                FixtureEvidenceState.NoAnchor,
                [],
                Vector3.Zero,
                path);
        public static IEnumerable<FixtureContactEvidence> InvalidCases()
        {
            yield return Missing();
            yield return new(FixtureEvidenceState.Empty, [], Vector3.Zero);
            yield return new(FixtureEvidenceState.Stale, [new(FixtureFoot.Left, Transform3D.Identity, true, true)], Vector3.Zero);
            yield return new(FixtureEvidenceState.Invalid, [new(FixtureFoot.Left, Transform3D.Identity, false, false)], Vector3.Zero);
            Transform3D incompatibleAnchor = new(Transform3D.Identity.Basis, new Vector3(0.012f, 0.0f, 0.0f));
            yield return new(FixtureEvidenceState.Contradictory, [new(FixtureFoot.Left, Transform3D.Identity, false, true), new(FixtureFoot.Left, incompatibleAnchor, false, true)], Vector3.Zero);
            yield return Released();
        }
    }

    private readonly record struct ContactLockStageSnapshot(
        bool TargetPublished,
        FixtureDisposition Disposition,
        Transform3D? Anchor,
        Vector3 Residual);

    private readonly record struct NativeTwoBonePipelineEvidence(
        int ProcessCount,
        Transform3D TargetAtProcessing,
        Transform3D EndBoneAtProcessing,
        int RootBoneIndex,
        int MiddleBoneIndex,
        int EndBoneIndex)
    {
        public static NativeTwoBonePipelineEvidence Empty => new(0, Transform3D.Identity, Transform3D.Identity, -1, -1, -1);

        public string Format(FixtureRole role, FixtureFoot foot, Transform3D anchor, Transform3D postPipelineTarget, Transform3D postPipelineEndBone)
            => $"role={role}; foot={foot}; ModificationProcessed count: {ProcessCount}; anchor: {anchor.Origin}; "
                 + $"target at signal: {TargetAtProcessing.Origin}; "
                 + $"configured end-bone at signal: {EndBoneAtProcessing.Origin}; "
                 + $"signal-time native bone indices: root={RootBoneIndex}, middle={MiddleBoneIndex}, end={EndBoneIndex}; "
                + $"anchor-to-target error: {anchor.Origin.DistanceTo(TargetAtProcessing.Origin):F6} m; "
                + $"native endpoint-to-target error: {EndBoneAtProcessing.Origin.DistanceTo(TargetAtProcessing.Origin):F6} m; "
                + $"post-pipeline target: {postPipelineTarget.Origin}; "
                  + $"post-pipeline configured end-bone: {postPipelineEndBone.Origin}.";
    }

    private enum FixtureGateBPreIKClass
    {
        Admitted,
        Missing,
        Stale,
        Invalid,
        Contradictory,
        FallbackMasking
    }

    private enum FixturePreIKDecision
    {
        Accepted,
        Rejected
    }

    private enum FixtureSelectedProvider
    {
        ContactEvidence,
        OrdinaryFallback
    }

    private enum FixtureGateBPostIKClass
    {
        Accepted,
        Missing,
        Stale,
        WrongFoot,
        NonFinite,
        AnchorIncoherent
    }

    private enum FixturePostIKDecision
    {
        Accepted,
        PreIKRejected,
        Missing,
        Stale,
        WrongFoot,
        NonFinite,
        AnchorIncoherent
    }

    private enum FixtureBlockingOutcome
    {
        None,
        PreIKFailureRetainedDespiteFallbackSolve,
        PostIKFailureBlocksFutureIntegration
    }

    /// <summary>
    /// Shared, test-only source path for a contact-evidence attempt. It deliberately describes the injected evidence
    /// rather than defining production contact policy.
    /// </summary>
    private readonly record struct FixtureEvidencePath(
        FixtureRole Role,
        FixtureFoot Foot,
        long Generation,
        // Audit-only grouping metadata. Pre-IK admission must be derived from Contacts.
        FixtureGateBPreIKClass InjectedClass,
        Transform3D? ExpectedAnchor,
        Transform3D OrdinaryFallbackTarget,
        IReadOnlyList<FixtureGateBContactRecord> Contacts);

    private readonly record struct FixtureGateBContactRecord(
        FixtureRole Role,
        FixtureFoot Foot,
        long Generation,
        Transform3D Anchor,
        bool IsStale,
        bool IsValid);

    /// <summary>
    /// Wraps the existing <see cref="NativeTwoBonePipelineEvidence"/> captured at <c>ModificationProcessed</c> with
    /// fixture identity and generation. A null signal-time value is deliberate evidence of a missing output.
    /// </summary>
    private readonly record struct FixtureNativeSolverSnapshot(
        FixtureRole Role,
        FixtureFoot Foot,
        long Generation,
        NativeTwoBonePipelineEvidence? SignalTimeEvidence);

    /// <summary>
    /// A deliberately constructed post-IK output. It is independent from the native signal-time snapshot so each
    /// negative class remains traceable without rewriting the observed native evidence.
    /// </summary>
    private readonly record struct FixtureInjectedPostIKOutput(
        FixtureGateBPostIKClass InjectedClass,
        FixtureRole Role,
        FixtureFoot Foot,
        long Generation,
        NativeTwoBonePipelineEvidence? SignalTimeEvidence);

    /// <summary>
    /// Captures the test-only boundary immediately after native <c>ModificationProcessed</c> evidence and before the
    /// post-IK validator consumes its explicitly injected output.
    /// </summary>
    private readonly record struct FixturePostIKSignalBoundary(
        FixtureNativeSolverSnapshot NativeSnapshot,
        FixtureInjectedPostIKOutput InjectedOutput);

    private readonly record struct FixturePostIKInjectionRequest(
        FixtureEvidencePath SourceEvidence,
        FixtureGateBPostIKClass InjectedClass);

    /// <summary>
    /// Retained per-case Gate B audit record. This is test evidence only and is not a production release decision.
    /// </summary>
    private readonly record struct FixtureGateBEvidenceRecord(
        FixtureEvidencePath SourceEvidence,
        FixturePreIKDecision PreIKDecision,
        FixtureSelectedProvider SelectedProvider,
        FixtureNativeSolverSnapshot NativeSolverSnapshot,
        FixtureGateBPostIKClass? InjectedPostIKClass,
        FixturePostIKSignalBoundary? PostIKSignalBoundary,
        FixturePostIKDecision PostIKDecision,
        FixtureBlockingOutcome BlockingOutcome)
    {
        public bool HasRequiredAuditFields
            => SourceEvidence.Generation > 0
                && NativeSolverSnapshot.Role == SourceEvidence.Role
                && NativeSolverSnapshot.Foot == SourceEvidence.Foot
                && NativeSolverSnapshot.Generation == SourceEvidence.Generation
                && (PostIKSignalBoundary is not FixturePostIKSignalBoundary boundary
                    || (InjectedPostIKClass.HasValue && boundary.InjectedOutput.InjectedClass == InjectedPostIKClass.Value))
                && SelectedProvider is FixtureSelectedProvider.ContactEvidence or FixtureSelectedProvider.OrdinaryFallback
                && BlockingOutcome != FixtureBlockingOutcome.None;

        public string Format()
            => $"source role={SourceEvidence.Role}; foot={SourceEvidence.Foot}; generation={SourceEvidence.Generation}; "
                + $"injected={SourceEvidence.InjectedClass}; contacts={SourceEvidence.Contacts.Count}; "
                + $"anchor={SourceEvidence.ExpectedAnchor?.Origin.ToString() ?? "missing"}; ordinary fallback={SourceEvidence.OrdinaryFallbackTarget.Origin}; "
                + $"pre-IK={PreIKDecision}; selected={SelectedProvider}; native role={NativeSolverSnapshot.Role}; "
                + $"native foot={NativeSolverSnapshot.Foot}; native generation={NativeSolverSnapshot.Generation}; "
                + $"native={FormatNativeSnapshot(NativeSolverSnapshot.SignalTimeEvidence)}; "
                + $"post-IK injected={InjectedPostIKClass?.ToString() ?? "not injected"}; "
                + $"boundary={FormatPostIKBoundary(PostIKSignalBoundary)}; "
                + $"post-IK={PostIKDecision}; "
                + $"blocking={BlockingOutcome}.";

        private static string FormatNativeSnapshot(NativeTwoBonePipelineEvidence? snapshot)
            => snapshot is NativeTwoBonePipelineEvidence evidence
                ? $"processes={evidence.ProcessCount}, target={evidence.TargetAtProcessing.Origin}, end={evidence.EndBoneAtProcessing.Origin}"
                : "missing";

        private static string FormatPostIKBoundary(FixturePostIKSignalBoundary? boundary)
            => boundary is FixturePostIKSignalBoundary value
                ? $"ModificationProcessed; post-IK role={value.InjectedOutput.Role}; foot={value.InjectedOutput.Foot}; generation={value.InjectedOutput.Generation}"
                : "not injected";
    }

    /// <summary>
    /// Independent test-only validator for final output. It consumes source evidence plus the signal-boundary native
    /// snapshot and injected output; it neither calls modifiers nor alters a target.
    /// </summary>
    private static class FixturePostIKValidator
    {
        public static FixturePostIKDecision Validate(
            FixtureEvidencePath source,
            FixturePreIKDecision preIKDecision,
            FixtureNativeSolverSnapshot nativeSnapshot)
        {
            _ = source;
            _ = nativeSnapshot;
            return preIKDecision != FixturePreIKDecision.Accepted
                ? FixturePostIKDecision.PreIKRejected
                : throw new Xunit.Sdk.XunitException(
                "Accepted post-IK validation requires an output injected at ModificationProcessed.");
        }

        public static FixturePostIKDecision Validate(
            FixtureEvidencePath source,
            FixturePreIKDecision preIKDecision,
            FixturePostIKSignalBoundary boundary)
        {
            if (preIKDecision != FixturePreIKDecision.Accepted)
            {
                return FixturePostIKDecision.PreIKRejected;
            }

            FixtureNativeSolverSnapshot nativeSnapshot = boundary.NativeSnapshot;
            if (nativeSnapshot.Role != source.Role
                || nativeSnapshot.Foot != source.Foot
                || nativeSnapshot.Generation != source.Generation
                || nativeSnapshot.SignalTimeEvidence is not NativeTwoBonePipelineEvidence { ProcessCount: > 0 })
            {
                return FixturePostIKDecision.Missing;
            }

            FixtureInjectedPostIKOutput injectedOutput = boundary.InjectedOutput;
            if (injectedOutput.SignalTimeEvidence is not NativeTwoBonePipelineEvidence signalTimeEvidence
                || signalTimeEvidence.ProcessCount <= 0)
            {
                return FixturePostIKDecision.Missing;
            }

            if (injectedOutput.Generation != source.Generation)
            {
                return FixturePostIKDecision.Stale;
            }

            if (injectedOutput.Role != source.Role || injectedOutput.Foot != source.Foot)
            {
                return FixturePostIKDecision.WrongFoot;
            }

            if (!IsFinite(signalTimeEvidence.TargetAtProcessing) || !IsFinite(signalTimeEvidence.EndBoneAtProcessing))
            {
                return FixturePostIKDecision.NonFinite;
            }

            if (source.ExpectedAnchor is not Transform3D expectedAnchor)
            {
                return FixturePostIKDecision.AnchorIncoherent;
            }

            float anchorToTargetError = signalTimeEvidence.TargetAtProcessing.Origin.DistanceTo(expectedAnchor.Origin);
            float endBoneToTargetError = signalTimeEvidence.EndBoneAtProcessing.Origin.DistanceTo(signalTimeEvidence.TargetAtProcessing.Origin);
            return anchorToTargetError <= AnchorToTargetDiagnosticToleranceMetres
                   && endBoneToTargetError <= FootAnchorToleranceMetres
                ? FixturePostIKDecision.Accepted
                : FixturePostIKDecision.AnchorIncoherent;
        }

        private static bool IsFinite(Transform3D transform)
            => IsFinite(transform.Origin)
                && IsFinite(transform.Basis.Column0)
                && IsFinite(transform.Basis.Column1)
                && IsFinite(transform.Basis.Column2);

        private static bool IsFinite(Vector3 vector)
            => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
    }

    private static IEnumerable<FixtureGateBPreIKClass> GetInvalidPreIKEvidenceClasses()
    {
        yield return FixtureGateBPreIKClass.Missing;
        yield return FixtureGateBPreIKClass.Stale;
        yield return FixtureGateBPreIKClass.Invalid;
        yield return FixtureGateBPreIKClass.Contradictory;
        yield return FixtureGateBPreIKClass.FallbackMasking;
    }

    private static IEnumerable<FixtureGateBPostIKClass> GetInvalidPostIKOutputClasses()
    {
        yield return FixtureGateBPostIKClass.Missing;
        yield return FixtureGateBPostIKClass.Stale;
        yield return FixtureGateBPostIKClass.WrongFoot;
        yield return FixtureGateBPostIKClass.NonFinite;
        yield return FixtureGateBPostIKClass.AnchorIncoherent;
    }

    private static class FixturePostIKGate
    {
        public static bool IsCoherent(ContactLockStageSnapshot snapshot, Transform3D finalFootWorldPose)
            => snapshot.TargetPublished
                && snapshot.Anchor is Transform3D anchor
                && finalFootWorldPose.Origin.DistanceTo(anchor.Origin) <= FootAnchorToleranceMetres;
    }

    private sealed partial class FixtureIntentProvider : IKTargetIntentProvider
    {
        public IKTargetIntent Intent
        {
            get; set;
        }

        public override IKTargetIntent GetTargetIntent() => Intent;
    }

    /// <summary>
    /// Test-only, per-foot future-intent provider. It only returns values; CharacterIK remains the actuator.
    /// </summary>
    private sealed partial class FixtureContactIntentProvider(FixtureRole role, FixtureFoot foot) : IKTargetIntentProvider
    {
        public FixtureContactEvidence Evidence
        {
            get; set;
        } = FixtureContactEvidence.NoAnchor();

        public ContactLockStageSnapshot Snapshot
        {
            get; private set;
        } = new(false, FixtureDisposition.FailedClosed, null, Vector3.Zero);

        public int InvocationCount
        {
            get; private set;
        }

        public override IKTargetIntent GetTargetIntent()
            => new(Transform3D.Identity, 0.0f);

        public override bool TryGetTargetIntent(out IKTargetIntentResolution resolution)
        {
            InvocationCount += 1;
            FixtureContactEvidence evidence = Evidence;
            if (evidence.GateBPath is FixtureEvidencePath gateBPath)
            {
                return TryResolveGateBEvidence(gateBPath, out resolution);
            }

            if (evidence.State == FixtureEvidenceState.Admitted && evidence.Contacts.Count == 1)
            {
                FixtureContactRecord contact = evidence.Contacts[0];
                if (contact.Foot == foot && !contact.IsStale && contact.IsValid)
                {
                    Snapshot = new(true, FixtureDisposition.Applied, contact.Anchor, evidence.Residual);
                    resolution = new IKTargetIntentResolution(new IKTargetIntent(contact.Anchor, 1.0f), true);
                    return true;
                }
            }

            FixtureDisposition disposition = evidence.State == FixtureEvidenceState.Unproven
                ? FixtureDisposition.DeferredRecoveryRequired
                : FixtureDisposition.FailedClosed;
            Snapshot = new(false, disposition, null, evidence.Residual);
            resolution = default;
            return false;
        }

        private bool TryResolveGateBEvidence(FixtureEvidencePath path, out IKTargetIntentResolution resolution)
        {
            if (FixturePreIKEvidenceValidator.TryResolve(path, role, foot, out Transform3D anchor))
            {
                Snapshot = new(true, FixtureDisposition.Applied, anchor, Vector3.Zero);
                resolution = new IKTargetIntentResolution(new IKTargetIntent(anchor, 1.0f), true);
                return true;
            }

            Snapshot = new(false, FixtureDisposition.FailedClosed, null, Vector3.Zero);
            resolution = default;
            return false;
        }
    }

    /// <summary>
    /// Test-only pre-IK evidence adjudication. It is intentionally data-driven so the audit label cannot authorise
    /// an override: records must be current, valid, finite, and mutually coherent for this role and foot.
    /// </summary>
    private static class FixturePreIKEvidenceValidator
    {
        public static bool TryResolve(
            FixtureEvidencePath path,
            FixtureRole role,
            FixtureFoot foot,
            out Transform3D anchor)
        {
            anchor = Transform3D.Identity;
            if (path.Role != role || path.Foot != foot || path.Generation <= 0 || path.Contacts.Count == 0)
            {
                return false;
            }

            Transform3D? resolvedAnchor = null;
            foreach (FixtureGateBContactRecord contact in path.Contacts)
            {
                if (contact.Role != role
                    || contact.Foot != foot
                    || contact.Generation != path.Generation
                    || contact.IsStale
                    || !contact.IsValid
                    || !IsFinite(contact.Anchor))
                {
                    return false;
                }

                if (resolvedAnchor is Transform3D existingAnchor
                    && existingAnchor.Origin.DistanceTo(contact.Anchor.Origin) > AnchorToTargetDiagnosticToleranceMetres)
                {
                    return false;
                }

                resolvedAnchor ??= contact.Anchor;
            }

            if (resolvedAnchor is not Transform3D finiteAnchor)
            {
                return false;
            }

            anchor = finiteAnchor;
            return true;
        }

        private static bool IsFinite(Transform3D transform)
            => IsFinite(transform.Origin)
                && IsFinite(transform.Basis.Column0)
                && IsFinite(transform.Basis.Column1)
                && IsFinite(transform.Basis.Column2);

        private static bool IsFinite(Vector3 vector)
            => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
    }

    private sealed class ContactLockLegSolveFixture : IAsyncDisposable
    {
        private ContactLockLegSolveFixture(
            FixtureRole role,
            Node3D root,
            Skeleton3D skeleton,
            FootTargetSyncController footTargetSyncController,
            CharacterIK characterIK,
            SkeletonModifier3D characterIKBeginStage,
            SkeletonModifier3D characterIKProviderStage,
            FixtureContactIntentProvider leftContactProvider,
            FixtureContactIntentProvider rightContactProvider,
            Node3D leftFootTarget,
            Node3D rightFootTarget,
            Node3D leftPoleTarget,
            Node3D rightPoleTarget,
            LegIKController leftPoleStage,
            LegIKController rightPoleStage,
            TwoBoneIK3D leftTwoBoneStage,
            TwoBoneIK3D rightTwoBoneStage,
            FixtureIntentProvider leftProvider,
            FixtureIntentProvider rightProvider,
            SceneTree sceneTree)
        {
            Role = role;
            Root = root;
            Skeleton = skeleton;
            FootSyncController = footTargetSyncController;
            CharacterIK = characterIK;
            CharacterIKBeginStage = characterIKBeginStage;
            CharacterIKProviderStage = characterIKProviderStage;
            _leftContactProvider = leftContactProvider;
            _rightContactProvider = rightContactProvider;
            _leftFootTarget = leftFootTarget;
            _rightFootTarget = rightFootTarget;
            _leftPoleTarget = leftPoleTarget;
            _rightPoleTarget = rightPoleTarget;
            _leftPoleStage = leftPoleStage;
            _rightPoleStage = rightPoleStage;
            _leftTwoBoneStage = leftTwoBoneStage;
            _rightTwoBoneStage = rightTwoBoneStage;
            _leftProvider = leftProvider;
            _rightProvider = rightProvider;
            _sceneTree = sceneTree;
        }

        private readonly Node3D _leftFootTarget;
        private readonly Node3D _rightFootTarget;
        private readonly Node3D _leftPoleTarget;
        private readonly Node3D _rightPoleTarget;
        private readonly LegIKController _leftPoleStage;
        private readonly LegIKController _rightPoleStage;
        private readonly TwoBoneIK3D _leftTwoBoneStage;
        private readonly TwoBoneIK3D _rightTwoBoneStage;
        private readonly FixtureIntentProvider _leftProvider;
        private readonly FixtureIntentProvider _rightProvider;
        private readonly FixtureContactIntentProvider _leftContactProvider;
        private readonly FixtureContactIntentProvider _rightContactProvider;
        private readonly SceneTree _sceneTree;
        private Node3D? _owningFixtureRoot;
        private NativeTwoBonePipelineEvidence _leftNativeTwoBonePipelineEvidence = NativeTwoBonePipelineEvidence.Empty;
        private NativeTwoBonePipelineEvidence _rightNativeTwoBonePipelineEvidence = NativeTwoBonePipelineEvidence.Empty;
        private FixturePostIKSignalBoundary? _leftPostIKSignalBoundary;
        private FixturePostIKSignalBoundary? _rightPostIKSignalBoundary;
        private FixturePostIKInjectionRequest? _pendingPostIKInjection;
        private readonly List<FixtureGateBEvidenceRecord> _gateBEvidenceRecords = [];
        private long _nativeSnapshotGeneration;

        public Node3D Root
        {
            get;
        }
        public FixtureRole Role
        {
            get;
        }
        public Skeleton3D Skeleton
        {
            get;
        }
        public FootTargetSyncController FootSyncController
        {
            get;
        }
        public CharacterIK CharacterIK
        {
            get;
        }
        public SkeletonModifier3D CharacterIKBeginStage
        {
            get;
        }
        public SkeletonModifier3D CharacterIKProviderStage
        {
            get;
        }
        public IReadOnlyList<FixtureGateBEvidenceRecord> GateBEvidenceRecords => _gateBEvidenceRecords;
        public long NextNativeSnapshotGeneration => _nativeSnapshotGeneration + 1;
        public static async Task<ContactLockLegSolveFixture> CreateAsync(SceneTree sceneTree)
        {
            ContactLockTwoRoleFixture roles = await ContactLockTwoRoleFixture.CreateAsync(sceneTree);
            roles.Player._owningFixtureRoot = roles.Root;
            return roles.Player;
        }

        internal static async Task<ContactLockLegSolveFixture> CreateRoleAsync(
            FixtureRole role,
            Node3D root,
            SceneTree sceneTree)
        {
            Skeleton3D skeleton = RequireDescendant<Skeleton3D>(root, "GeneralSkeleton");
            skeleton.Set("modifier_callback_mode_process", 0);
            skeleton.ResetBonePoses();

            Node3D leftFootTarget = RequireNode3D(root, "Markers/LeftFootTarget");
            Node3D rightFootTarget = RequireNode3D(root, "Markers/RightFootTarget");
            Node3D leftPoleTarget = RequireNode3D(root, "Markers/LeftKneePoleTarget");
            Node3D rightPoleTarget = RequireNode3D(root, "Markers/RightKneePoleTarget");
            LegIKController leftPole = Assert.IsType<LegIKController>(skeleton.GetNodeOrNull("LeftLegIKController"), exactMatch: false);
            LegIKController rightPole = Assert.IsType<LegIKController>(skeleton.GetNodeOrNull("RightLegIKController"), exactMatch: false);
            TwoBoneIK3D leftTwoBone = Assert.IsType<TwoBoneIK3D>(skeleton.GetNodeOrNull("LeftLegTwoBoneIKController"), exactMatch: false);
            TwoBoneIK3D rightTwoBone = Assert.IsType<TwoBoneIK3D>(skeleton.GetNodeOrNull("RightLegTwoBoneIKController"), exactMatch: false);
            leftTwoBone.Active = true;
            leftTwoBone.Influence = 1.0f;
            rightTwoBone.Active = true;
            rightTwoBone.Influence = 1.0f;

            FootTargetSyncController sync = Assert.IsType<FootTargetSyncController>(
                skeleton.GetNodeOrNull("FootTargetSyncController"),
                exactMatch: false);
            sync.LeftFootTarget = leftFootTarget;
            sync.RightFootTarget = rightFootTarget;
            sync.Active = true;
            sync.Influence = 1.0f;
            Assert.Equal(1, CountFootTargetSyncControllers(skeleton));
            Assert.Same(leftFootTarget, sync.LeftFootTarget);
            Assert.Same(rightFootTarget, sync.RightFootTarget);
            DynamicPhysicalRig physicalRig = new()
            {
                Name = "ContactLockFixturePhysicalRig",
                Enabled = false,
                TargetSkeleton = skeleton
            };
            skeleton.AddChild(physicalRig);

            Node3D targetRoot = new()
            {
                Name = "ContactLockFixtureTargets"
            };
            root.AddChild(targetRoot);
            Marker3D viewpoint = new()
            {
                Name = "Viewpoint"
            };
            CharacterBody3D head = new()
            {
                Name = "Head"
            };
            Node3D headSolve = new()
            {
                Name = "HeadSolve"
            };
            AnimatableBody3D rightHand = new()
            {
                Name = "RightHand"
            };
            AnimatableBody3D leftHand = new()
            {
                Name = "LeftHand"
            };
            targetRoot.AddChild(viewpoint);
            targetRoot.AddChild(head);
            targetRoot.AddChild(headSolve);
            targetRoot.AddChild(rightHand);
            targetRoot.AddChild(leftHand);
            FixtureIntentProvider leftProvider = new()
            {
                Name = "LeftFixtureProvider"
            };
            FixtureIntentProvider rightProvider = new()
            {
                Name = "RightFixtureProvider"
            };
            FixtureContactIntentProvider leftContactProvider = new(role, FixtureFoot.Left)
            {
                Name = "LeftFixtureContactProvider"
            };
            FixtureContactIntentProvider rightContactProvider = new(role, FixtureFoot.Right)
            {
                Name = "RightFixtureContactProvider"
            };
            PriorityIKTargetIntentProvider leftProviderChain = new()
            {
                Name = "LeftFixtureProviderChain",
                Providers = [leftContactProvider, leftProvider],
            };
            PriorityIKTargetIntentProvider rightProviderChain = new()
            {
                Name = "RightFixtureProviderChain",
                Providers = [rightContactProvider, rightProvider],
            };
            AnimationSynchronizedFootTargetProvider leftFallback = new()
            {
                Name = "LeftAnimationFallback",
                FootTarget = leftFootTarget
            };
            AnimationSynchronizedFootTargetProvider rightFallback = new()
            {
                Name = "RightAnimationFallback",
                FootTarget = rightFootTarget
            };
            targetRoot.AddChild(leftProvider);
            targetRoot.AddChild(rightProvider);
            targetRoot.AddChild(leftContactProvider);
            targetRoot.AddChild(rightContactProvider);
            targetRoot.AddChild(leftProviderChain);
            targetRoot.AddChild(rightProviderChain);
            targetRoot.AddChild(leftFallback);
            targetRoot.AddChild(rightFallback);

            CharacterIK characterIK = new()
            {
                Name = "CharacterIK",
                Active = true,
                Viewpoint = viewpoint,
                HeadIKTarget = head,
                HeadIKSolveTarget = headSolve,
                RightHandIKTarget = rightHand,
                LeftHandIKTarget = leftHand,
                LeftFootIKTarget = leftFootTarget,
                RightFootIKTarget = rightFootTarget,
                LeftFootTargetIntentProvider = leftProviderChain,
                RightFootTargetIntentProvider = rightProviderChain,
                LeftFootFallbackIntentProvider = leftFallback,
                RightFootFallbackIntentProvider = rightFallback,
                LeftFootModifierGroup = [leftPole, leftTwoBone],
                RightFootModifierGroup = [rightPole, rightTwoBone],
                PhysicalRig = physicalRig,
            };
            root.AddChild(characterIK);
            await WaitForFramesAsync(sceneTree, 2);
            characterIK._PhysicsProcess(1.0d / 60.0d);

            SkeletonModifier3D begin = Assert.IsType<SkeletonModifier3D>(skeleton.GetNodeOrNull("CharacterIKBeginStage"), exactMatch: false);
            SkeletonModifier3D provider = Assert.IsType<SkeletonModifier3D>(skeleton.GetNodeOrNull("CharacterIKFootProviderStage"), exactMatch: false);
            await WaitForFramesAsync(sceneTree, 2);

            ContactLockLegSolveFixture fixture = new(role, root, skeleton, sync, characterIK, begin, provider, leftContactProvider, rightContactProvider, leftFootTarget, rightFootTarget, leftPoleTarget, rightPoleTarget, leftPole, rightPole, leftTwoBone, rightTwoBone, leftProvider, rightProvider, sceneTree);
            leftTwoBone.ModificationProcessed += fixture.RecordLeftNativeTwoBonePipelineProcessing;
            rightTwoBone.ModificationProcessed += fixture.RecordRightNativeTwoBonePipelineProcessing;
            fixture.SetProviderIntent(FixtureFoot.Left, fixture.GetFootWorldPose(FixtureFoot.Left));
            fixture.SetProviderIntent(FixtureFoot.Right, fixture.GetFootWorldPose(FixtureFoot.Right));
            await fixture.ProcessSkeletonCyclesAsync(2);
            fixture.ResetNativeTwoBonePipelineEvidence();
            return fixture;
        }

        public Transform3D GetFootWorldPose(FixtureFoot foot) => GetBoneWorldPose(foot == FixtureFoot.Left ? "LeftFoot" : "RightFoot");
        public Transform3D GetConfiguredEndBoneWorldPose(FixtureFoot foot) => GetBoneWorldPose(GetConfiguredEndBoneName(foot));
        public string GetConfiguredEndBoneName(FixtureFoot foot)
        {
            TwoBoneIK3D twoBone = GetTwoBoneStage(foot);
            string configuredEndBoneName = twoBone.Get("settings/0/end_bone_name").AsString();
            string expectedEndBoneName = foot == FixtureFoot.Left ? "LeftFoot" : "RightFoot";

            Assert.Equal(expectedEndBoneName, configuredEndBoneName);
            return configuredEndBoneName;
        }
        public Transform3D GetBonePose(string name) => Skeleton.GetBoneGlobalPose(RequireBone(name));
        public Transform3D GetBoneWorldPose(string name) => Skeleton.GlobalTransform * GetBonePose(name);
        public Node3D GetFootTarget(FixtureFoot foot) => foot == FixtureFoot.Left ? _leftFootTarget : _rightFootTarget;
        public Node3D GetCharacterIKFootTarget(FixtureFoot foot)
            => foot == FixtureFoot.Left
                ? CharacterIK.LeftFootIKTarget ?? throw new Xunit.Sdk.XunitException("CharacterIK has no left-foot target.")
                : CharacterIK.RightFootIKTarget ?? throw new Xunit.Sdk.XunitException("CharacterIK has no right-foot target.");
        public Node3D GetPoleTarget(FixtureFoot foot) => foot == FixtureFoot.Left ? _leftPoleTarget : _rightPoleTarget;
        public SkeletonModifier3D GetPoleStage(FixtureFoot foot) => foot == FixtureFoot.Left ? _leftPoleStage : _rightPoleStage;
        public TwoBoneIK3D GetTwoBoneStage(FixtureFoot foot) => foot == FixtureFoot.Left ? _leftTwoBoneStage : _rightTwoBoneStage;
        public void SetProviderIntent(FixtureFoot foot, Transform3D target) => (foot == FixtureFoot.Left ? _leftProvider : _rightProvider).Intent = new IKTargetIntent(target, 1f);
        public void SetEvidence(FixtureContactEvidence evidence)
        {
            _leftContactProvider.Evidence = evidence;
            _rightContactProvider.Evidence = evidence;
        }
        public void SetGateBEvidence(FixtureEvidencePath path) => SetEvidence(FixtureContactEvidence.GateB(path));
        public int GetContactIntentProviderInvocationCount(FixtureFoot foot)
            => (foot == FixtureFoot.Left ? _leftContactProvider : _rightContactProvider).InvocationCount;
        public ContactLockStageSnapshot GetContactLockSnapshot(FixtureFoot foot)
            => (foot == FixtureFoot.Left ? _leftContactProvider : _rightContactProvider).Snapshot;
        public NativeTwoBonePipelineEvidence GetNativeTwoBonePipelineEvidence(FixtureFoot foot)
            => foot == FixtureFoot.Left ? _leftNativeTwoBonePipelineEvidence : _rightNativeTwoBonePipelineEvidence;
        public FixtureNativeSolverSnapshot CaptureNativeSolverSnapshot(FixtureFoot foot)
            => new(Role, foot, _nativeSnapshotGeneration, GetNativeTwoBonePipelineEvidence(foot));
        public void InjectPostIKOutputAtNextModificationProcessed(
            FixtureEvidencePath source,
            FixtureGateBPostIKClass injectedClass)
        {
            if (source.Role != Role || source.Generation != NextNativeSnapshotGeneration)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Post-IK injection must target {Role} and generation {NextNativeSnapshotGeneration}; "
                    + $"received role={source.Role}, generation={source.Generation}.");
            }

            if (_pendingPostIKInjection is not null)
            {
                throw new Xunit.Sdk.XunitException("Only one post-IK injection may await the next ModificationProcessed boundary.");
            }

            SetPostIKSignalBoundary(source.Foot, null);
            _pendingPostIKInjection = new FixturePostIKInjectionRequest(source, injectedClass);
        }
        public FixturePostIKSignalBoundary RequirePostIKSignalBoundary(FixtureFoot foot)
            => GetPostIKSignalBoundary(foot)
                ?? throw new Xunit.Sdk.XunitException(
                    $"Expected {Role} {foot} post-IK output injection at ModificationProcessed.");
        public void RetainGateBEvidence(FixtureGateBEvidenceRecord record) => _gateBEvidenceRecords.Add(record);
        internal bool HasNativeTwoBonePipelineEvidence
            => _leftNativeTwoBonePipelineEvidence.ProcessCount > 0
               && _rightNativeTwoBonePipelineEvidence.ProcessCount > 0;

        public async Task ProcessSkeletonCyclesAsync(int cycleCount)
        {
            for (int cycle = 0; cycle < cycleCount; cycle++)
            {
                ResetNativeTwoBonePipelineEvidence();

                // Godot 4.7.2 only runs native TwoBoneIK3D through Skeleton3D's modifier pipeline.
                // Calling its C# virtual method directly is a no-op for the native solve and is deliberately not used.
                // Wait for modifier physics frames until both native stages prove their pipeline execution.
                for (int frame = 0;
                     frame < 8
                     && (_leftNativeTwoBonePipelineEvidence.ProcessCount == 0 || _rightNativeTwoBonePipelineEvidence.ProcessCount == 0);
                     frame++)
                {
                    await WaitForNextPhysicsFrameAsync(_sceneTree);
                }
            }
        }
        public Transform3D CreateAnchorInsideReach(FixtureFoot foot)
        {
            Transform3D upper = GetBoneWorldPose(foot == FixtureFoot.Left ? "LeftUpperLeg" : "RightUpperLeg");
            Transform3D current = GetFootWorldPose(foot);
            Vector3 towardsUpper = (upper.Origin - current.Origin).Normalized();
            return new Transform3D(current.Basis, current.Origin + (towardsUpper * 0.018f));
        }

        public async ValueTask DisposeAsync()
        {
            Node3D nodeToFree = _owningFixtureRoot ?? Root;
            if (GodotObject.IsInstanceValid(nodeToFree) && nodeToFree.IsInsideTree())
            {
                nodeToFree.QueueFree();
                await WaitForNextFrameAsync(_sceneTree);
            }
        }

        private int RequireBone(string name) => Skeleton.FindBone(name) is int index and not -1 ? index : throw new Xunit.Sdk.XunitException($"Fixture skeleton is missing '{name}'.");
        private static Node3D RequireNode3D(Node root, string path) => Assert.IsType<Node3D>(root.GetNodeOrNull(path), exactMatch: false);
        private static T RequireDescendant<T>(Node root, string name)
            where T : Node
            => Assert.IsType<T>(root.FindChild(name, owned: true, recursive: true), exactMatch: false);

        internal void ResetNativeTwoBonePipelineEvidence()
        {
            _nativeSnapshotGeneration += 1;
            _leftNativeTwoBonePipelineEvidence = NativeTwoBonePipelineEvidence.Empty;
            _rightNativeTwoBonePipelineEvidence = NativeTwoBonePipelineEvidence.Empty;
        }

        private void RecordLeftNativeTwoBonePipelineProcessing() => RecordNativeTwoBonePipelineProcessing(FixtureFoot.Left);
        private void RecordRightNativeTwoBonePipelineProcessing() => RecordNativeTwoBonePipelineProcessing(FixtureFoot.Right);

        private void RecordNativeTwoBonePipelineProcessing(FixtureFoot foot)
        {
            NativeTwoBonePipelineEvidence previousEvidence = GetNativeTwoBonePipelineEvidence(foot);
            TwoBoneIK3D twoBone = GetTwoBoneStage(foot);
            NativeTwoBonePipelineEvidence evidence = new(
                previousEvidence.ProcessCount + 1,
                GetCharacterIKFootTarget(foot).GlobalTransform,
                GetConfiguredEndBoneWorldPose(foot),
                twoBone.Get("settings/0/root_bone").AsInt32(),
                twoBone.Get("settings/0/middle_bone").AsInt32(),
                twoBone.Get("settings/0/end_bone").AsInt32());

            if (foot == FixtureFoot.Left)
            {
                _leftNativeTwoBonePipelineEvidence = evidence;
            }
            else
            {
                _rightNativeTwoBonePipelineEvidence = evidence;
            }

            ApplyPendingPostIKInjectionAtModificationProcessed(foot);
        }

        private FixturePostIKSignalBoundary? GetPostIKSignalBoundary(FixtureFoot foot)
            => foot == FixtureFoot.Left ? _leftPostIKSignalBoundary : _rightPostIKSignalBoundary;

        private void SetPostIKSignalBoundary(FixtureFoot foot, FixturePostIKSignalBoundary? boundary)
        {
            if (foot == FixtureFoot.Left)
            {
                _leftPostIKSignalBoundary = boundary;
            }
            else
            {
                _rightPostIKSignalBoundary = boundary;
            }
        }

        private void ApplyPendingPostIKInjectionAtModificationProcessed(FixtureFoot foot)
        {
            if (_pendingPostIKInjection is not FixturePostIKInjectionRequest request
                || request.SourceEvidence.Foot != foot)
            {
                return;
            }

            FixtureNativeSolverSnapshot nativeSnapshot = CaptureNativeSolverSnapshot(foot);
            if (nativeSnapshot.Role != request.SourceEvidence.Role
                || nativeSnapshot.Foot != request.SourceEvidence.Foot
                || nativeSnapshot.Generation != request.SourceEvidence.Generation
                || nativeSnapshot.SignalTimeEvidence is not NativeTwoBonePipelineEvidence { ProcessCount: > 0 })
            {
                throw new Xunit.Sdk.XunitException(
                    "Post-IK injection requires a fresh role/foot/generation-matched native ModificationProcessed snapshot.");
            }

            FixtureInjectedPostIKOutput injectedOutput = CreatePostIKInjectedOutput(nativeSnapshot, request.InjectedClass);
            SetPostIKSignalBoundary(foot, new FixturePostIKSignalBoundary(nativeSnapshot, injectedOutput));
            _pendingPostIKInjection = null;
        }

        private static int CountFootTargetSyncControllers(Skeleton3D skeleton)
        {
            int controllerCount = 0;
            foreach (Node child in skeleton.GetChildren())
            {
                if (child is FootTargetSyncController)
                {
                    controllerCount += 1;
                }
            }

            return controllerCount;
        }
    }

    private sealed class ContactLockTwoRoleFixture : IAsyncDisposable
    {
        private ContactLockTwoRoleFixture(
            Node3D root,
            ContactLockLegSolveFixture player,
            ContactLockLegSolveFixture npc,
            SceneTree sceneTree)
        {
            Root = root;
            Player = player;
            NPC = npc;
            _sceneTree = sceneTree;
        }

        private readonly SceneTree _sceneTree;

        public Node3D Root
        {
            get;
        }
        public ContactLockLegSolveFixture Player
        {
            get;
        }
        public ContactLockLegSolveFixture NPC
        {
            get;
        }
        public IReadOnlyList<ContactLockLegSolveFixture> Fixtures => [Player, NPC];

        public static async Task<ContactLockTwoRoleFixture> CreateAsync(SceneTree sceneTree)
        {
            Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(FixtureScenePath));
            Assert.Equal(Error.Ok, changeSceneError);
            await WaitForFramesAsync(sceneTree, 2);
            Node3D root = Assert.IsAssignableFrom<Node3D>(
                sceneTree.CurrentScene ?? throw new Xunit.Sdk.XunitException("Expected contact-lock fixture to become the current scene."));
            root.Name = "ContactLockTwoRoleFixture";

            foreach (Node child in root.GetChildren())
            {
                child.QueueFree();
            }

            await WaitForNextFrameAsync(sceneTree);
            PackedScene roleTopology = LoadPackedScene(FixtureScenePath);
            Node3D playerRoot = roleTopology.Instantiate<Node3D>();
            playerRoot.Name = nameof(FixtureRole.Player);
            Node3D npcRoot = roleTopology.Instantiate<Node3D>();
            npcRoot.Name = nameof(FixtureRole.NPC);
            npcRoot.Position = new Vector3(1.5f, 0.0f, 0.0f);
            root.AddChild(playerRoot);
            root.AddChild(npcRoot);
            await WaitForFramesAsync(sceneTree, 2);

            ContactLockLegSolveFixture player = await ContactLockLegSolveFixture.CreateRoleAsync(FixtureRole.Player, playerRoot, sceneTree);
            ContactLockLegSolveFixture npc = await ContactLockLegSolveFixture.CreateRoleAsync(FixtureRole.NPC, npcRoot, sceneTree);
            return new ContactLockTwoRoleFixture(root, player, npc, sceneTree);
        }

        public async Task ProcessSkeletonCyclesAsync(int cycleCount)
        {
            for (int cycle = 0; cycle < cycleCount; cycle++)
            {
                Player.ResetNativeTwoBonePipelineEvidence();
                NPC.ResetNativeTwoBonePipelineEvidence();
                for (int frame = 0;
                     frame < 8
                     && (!Player.HasNativeTwoBonePipelineEvidence || !NPC.HasNativeTwoBonePipelineEvidence);
                     frame++)
                {
                    await WaitForNextPhysicsFrameAsync(_sceneTree);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
                await WaitForNextFrameAsync(_sceneTree);
            }
        }
    }
}
