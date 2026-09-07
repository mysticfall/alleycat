using System.Reflection;
using AlleyCat.IK;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// IK-005 TR20 and CTRL-002 pause regressions for the <see cref="HandGrabTargetProvider" /> simulation clock:
/// interpolation advances once per supplied simulation tick and never on wall-clock reads or observational
/// getters, physics catch-up deltas advance it proportionally, the canonical source-intent snapshot is captured
/// only at the authoritative advance epoch, and the CharacterIK physics-actuation tick performs the advance.
/// The held-carry latency contracts are pinned here as well: the residual return completes into true
/// passthrough under sustained source motion so held tracking carries no steady-state lag, with bounded
/// adjacent steps and stepped-source absorption through the decaying residual.
/// </summary>
public sealed class HandGrabTargetProviderSimulationClockIntegrationTests
{
    private const double PhysicsTickSeconds = 1.0d / 60.0d;
    private const float PositionToleranceMetres = 0.0002f;

    /// <summary>
    /// Output-versus-source offset bound for the held passthrough contracts — the residual snap tolerance plus
    /// float-composition slack.
    /// </summary>
    private const float HeldLagToleranceMetres = 0.002f;

    /// <summary>
    /// A long wall-clock pause with repeated observations must not advance or jump the interpolation; the first
    /// simulation tick afterwards advances exactly one tick's worth.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Provider_Interpolation_IgnoresWallClockAndObservations_AdvancesPerSuppliedTick()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "GrabProviderClockRoot"
        };
        StubIntentProvider defaultProvider = new()
        {
            Name = "DefaultSource",
            TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, Vector3.Zero), 1.0f),
        };
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider",
            DefaultProvider = defaultProvider,
            Responsiveness = 18.0f,
        };
        root.AddChild(defaultProvider);
        root.AddChild(provider);
        sceneTree.Root.AddChild(root);

        try
        {
            Vector3 destination = new(0.42f, 0.18f, -0.31f);
            provider.SetGrabTarget(new Transform3D(Basis.Identity, destination));

            Transform3D atOverrideStart = provider.GetTargetIntent().WorldTransform;

            // A long wall-clock pause during which the provider is only observed: neither the elapsed wall time
            // nor repeated GetTargetIntent observations may advance the interpolation.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.Elapsed.TotalMilliseconds < 250.0d)
            {
                _ = provider.GetTargetIntent();
                await Task.Delay(10);
            }

            AssertTransformsApproximatelyEqual(atOverrideStart, provider.GetTargetIntent().WorldTransform);

            // One supplied simulation tick advances exactly one tick's alpha toward the destination.
            provider.AdvanceSimulation(PhysicsTickSeconds);
            Transform3D afterOneTick = provider.GetTargetIntent().WorldTransform;
            float expectedAlpha = 1.0f - Mathf.Exp(-provider.Responsiveness * (float)PhysicsTickSeconds);
            Vector3 expectedPosition = atOverrideStart.Origin.Lerp(destination, expectedAlpha);
            AssertTransformsApproximatelyEqual(new Transform3D(Basis.Identity, expectedPosition), afterOneTick);
            Assert.True(
                afterOneTick.Origin.DistanceTo(atOverrideStart.Origin) > 0.5f * expectedAlpha * destination.Length(),
                "A single tick must advance the interpolation materially, proving the pause did not bank time.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Physics catch-up steps advance interpolation proportionally: one double-length advance equals two
    /// ordinary advances, matching the exponential smoothing composition exactly.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Provider_Interpolation_CatchUpDelta_AdvancesProportionally()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "GrabProviderCatchUpRoot"
        };
        StubIntentProvider defaultProvider = new()
        {
            Name = "DefaultSource"
        };
        HandGrabTargetProvider stepwise = new()
        {
            Name = "StepwiseProvider",
            DefaultProvider = defaultProvider
        };
        HandGrabTargetProvider catchUp = new()
        {
            Name = "CatchUpProvider",
            DefaultProvider = defaultProvider
        };
        root.AddChild(defaultProvider);
        root.AddChild(stepwise);
        root.AddChild(catchUp);
        sceneTree.Root.AddChild(root);

        try
        {
            Vector3 destination = new(-0.35f, 0.22f, 0.41f);
            Transform3D grabTarget = new(Basis.Identity, destination);
            stepwise.SetGrabTarget(grabTarget);
            catchUp.SetGrabTarget(grabTarget);

            stepwise.AdvanceSimulation(PhysicsTickSeconds);
            stepwise.AdvanceSimulation(PhysicsTickSeconds);
            catchUp.AdvanceSimulation(2.0d * PhysicsTickSeconds);

            AssertTransformsApproximatelyEqual(
                stepwise.GetTargetIntent().WorldTransform,
                catchUp.GetTargetIntent().WorldTransform);

            // The catch-up advance is also materially less than a full snap to the destination.
            Assert.True(
                catchUp.GetTargetIntent().WorldTransform.Origin.DistanceTo(destination) > 0.01f,
                "A catch-up advance must remain an incremental interpolation, not a jump to the destination.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// The canonical source-intent snapshot is published only by the authoritative advance, never by
    /// observational reads, and unusable (zero-influence) samples are not exposed as a usable source pose.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Provider_SourceIntent_IsCapturedOnlyAtTheAuthoritativeAdvanceEpoch()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "GrabProviderEpochRoot"
        };
        StubIntentProvider defaultProvider = new()
        {
            Name = "DefaultSource"
        };
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider",
            DefaultProvider = defaultProvider
        };
        root.AddChild(defaultProvider);
        root.AddChild(provider);
        sceneTree.Root.AddChild(root);

        try
        {
            Assert.False(provider.HasSourceIntent);
            Assert.False(provider.TryGetSourceIntent(out _));

            Transform3D firstSource = new(Basis.Identity, new Vector3(0.1f, 1.4f, -0.2f));
            defaultProvider.TargetIntent = new IKTargetIntent(firstSource, 1.0f);
            provider.AdvanceSimulation(PhysicsTickSeconds);
            Assert.True(provider.TryGetSourceIntent(out IKTargetIntent captured));
            AssertTransformsApproximatelyEqual(firstSource, captured.WorldTransform);
            Assert.Equal(1.0f, captured.DesiredInfluence);

            // Changing the default source moves passthrough output immediately, but the canonical epoch
            // snapshot stays at the last advance until the next authoritative tick.
            Transform3D secondSource = new(Basis.Identity, new Vector3(0.5f, 0.9f, 0.3f));
            defaultProvider.TargetIntent = new IKTargetIntent(secondSource, 1.0f);
            AssertTransformsApproximatelyEqual(secondSource, provider.GetTargetIntent().WorldTransform);
            Assert.True(provider.TryGetSourceIntent(out _));
            AssertTransformsApproximatelyEqual(firstSource, provider.LastSourceIntent.WorldTransform);

            provider.AdvanceSimulation(PhysicsTickSeconds);
            AssertTransformsApproximatelyEqual(secondSource, provider.LastSourceIntent.WorldTransform);

            // An unavailable (zero-influence) source never clobbers the retained canonical sample: transient
            // loss keeps the last usable source intent instead of substituting assisted output for it.
            defaultProvider.TargetIntent = new IKTargetIntent(secondSource, 0.0f);
            provider.AdvanceSimulation(PhysicsTickSeconds);
            Assert.True(provider.TryGetSourceIntent(out IKTargetIntent retained));
            AssertTransformsApproximatelyEqual(secondSource, retained.WorldTransform);
            Assert.True(provider.HasSourceIntent);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// The CharacterIK physics-actuation tick — the authoritative tick — advances a configured hand grab
    /// provider exactly once per invocation at the canonical source-sampling epoch.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterIK_PhysicsActuationTick_AdvancesGrabProviderSimulationOncePerTick()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixtureReference fixture = await VrikFixtureReference.CreateAsync(sceneTree);

        try
        {
            StubIntentProvider defaultProvider = new()
            {
                Name = "ClockWiringDefaultSource",
                TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, Vector3.Zero), 1.0f),
            };
            HandGrabTargetProvider provider = new()
            {
                Name = "ClockWiringGrabProvider",
                DefaultProvider = defaultProvider,
                Responsiveness = 18.0f,
            };
            fixture.Root.AddChild(defaultProvider);
            fixture.Root.AddChild(provider);
            fixture.PlayerVRIK.RightHandIKTargetIntentProvider = provider;
            Assert.True(fixture.PlayerVRIK.BindToXRRuntime(fixture.Origin, fixture.Camera));

            Vector3 destination = new(0.31f, 0.95f, -0.28f);

            // Pause the tree so only the explicitly invoked physics-actuation ticks advance the provider.
            sceneTree.Paused = true;
            provider.SetGrabTarget(new Transform3D(Basis.Identity, destination));
            fixture.InvokeUpdatePhysicalActuators(PhysicsTickSeconds);
            Transform3D advancedOnce = provider.GetTargetIntent().WorldTransform;
            Assert.True(
                advancedOnce.Origin.DistanceTo(Vector3.Zero) > 0.005f,
                "The first authoritative CharacterIK tick must advance the grab provider off its origin.");

            // Wall-clock time passing between authoritative ticks must not advance the provider further.
            await Task.Delay(150);
            AssertTransformsApproximatelyEqual(advancedOnce, provider.GetTargetIntent().WorldTransform);

            fixture.InvokeUpdatePhysicalActuators(PhysicsTickSeconds);
            Transform3D advancedTwice = provider.GetTargetIntent().WorldTransform;
            Assert.True(
                advancedTwice.Origin.DistanceTo(advancedOnce.Origin) > 0.001f,
                "A second authoritative tick must advance the interpolation again.");
            Assert.True(
                advancedTwice.Origin.DistanceTo(destination) > 0.01f,
                "Two ticks must remain an incremental interpolation toward the destination.");

            // The same epoch captured the canonical source snapshot for acquisition consumers.
            Assert.True(provider.TryGetSourceIntent(out IKTargetIntent sourceIntent));
            Assert.Equal(1.0f, sourceIntent.DesiredInfluence);
        }
        finally
        {
            sceneTree.Paused = false;
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Held tracking must not low-pass the live source: after a Movable commit's anchor window the residual
    /// return completes into true default passthrough even while the source keeps moving, so sustained held
    /// motion carries no steady-state output-versus-source offset (INTR-002 R2/R4 — the held hand follows the
    /// selected XR hand-pose source, without a permanent velocity-dependent lag).
    /// </summary>
    [Headless]
    [Fact]
    public async Task Provider_HeldCarry_ReturnCompletesToPassthroughUnderSustainedSourceMotion()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "HeldCarryPassthroughRoot"
        };
        StubIntentProvider defaultProvider = new()
        {
            Name = "DefaultSource",
            TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, Vector3.Zero), 1.0f),
        };
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider",
            DefaultProvider = defaultProvider,
        };
        root.AddChild(defaultProvider);
        root.AddChild(provider);
        sceneTree.Root.AddChild(root);

        try
        {
            Vector3 command = new(0.09f, 0.0f, 0.0f);
            provider.SetGrabTarget(new Transform3D(Basis.Identity, command));
            for (int tick = 0; tick < 120; tick++)
            {
                provider.AdvanceSimulation(PhysicsTickSeconds);
            }

            provider.BeginHeldCarry();

            // Sustained source motion across the anchor and the return: the output must converge onto the
            // moving source and stay there, not settle into a velocity-dependent pursuit offset.
            const float sourceSpeed = 0.4f;
            Vector3 sourceOrigin = Vector3.Zero;
            int firstConvergedTick = -1;
            int sustainedConvergedTicks = 0;
            for (int tick = 0; tick < 120; tick++)
            {
                sourceOrigin += Vector3.Right * (sourceSpeed * (float)PhysicsTickSeconds);
                defaultProvider.TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, sourceOrigin), 1.0f);
                provider.AdvanceSimulation(PhysicsTickSeconds);

                float offset = sourceOrigin.DistanceTo(provider.GetTargetIntent().WorldTransform.Origin);
                if (offset <= HeldLagToleranceMetres)
                {
                    firstConvergedTick = firstConvergedTick < 0 ? tick : firstConvergedTick;
                    sustainedConvergedTicks++;
                }
                else
                {
                    sustainedConvergedTicks = 0;
                }
            }

            int anchorTicks = (int)Mathf.Ceil(provider.HeldCarryAnchorSeconds / (float)PhysicsTickSeconds);
            Assert.True(
                firstConvergedTick >= 0 && firstConvergedTick <= anchorTicks + 40,
                $"The held-carry return must complete to passthrough within the anchor plus a bounded return; "
                + $"first convergence observed at tick {firstConvergedTick} (anchor {anchorTicks} ticks).");
            Assert.True(
                sustainedConvergedTicks >= 30,
                $"Once converged, held tracking must stay on the moving source (no steady-state lag); observed "
                + $"only {sustainedConvergedTicks} consecutive converged ticks of 120.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Held-carry output motion stays within the bounded per-tick continuity budget while the source moves and
    /// the residual decays, and a stepped source — larger than the held-carry carry bound — is absorbed into
    /// the decaying residual instead of teleporting the output (INTR-002 R2/R4; IK-005 TR20).
    /// </summary>
    [Headless]
    [Fact]
    public async Task Provider_HeldCarry_ReturnStepsStayBoundedAndAbsorbSourceJumps()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "HeldCarryStepBoundRoot"
        };
        StubIntentProvider defaultProvider = new()
        {
            Name = "DefaultSource",
            TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, Vector3.Zero), 1.0f),
        };
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider",
            DefaultProvider = defaultProvider,
        };
        root.AddChild(defaultProvider);
        root.AddChild(provider);
        sceneTree.Root.AddChild(root);

        try
        {
            Vector3 command = new(0.09f, 0.0f, 0.0f);
            provider.SetGrabTarget(new Transform3D(Basis.Identity, command));
            for (int tick = 0; tick < 120; tick++)
            {
                provider.AdvanceSimulation(PhysicsTickSeconds);
            }

            // The continuity baseline is the last approach output the pipeline consumed before the commit —
            // reads between the commit and the first advance answer with the documented immediate
            // post-commit passthrough and are not pipeline observations.
            Vector3 lastApproachOutput = provider.GetTargetIntent().WorldTransform.Origin;
            provider.BeginHeldCarry();

            // Anchor plus return with sustained source motion: every adjacent output step stays within the
            // carried source motion plus the bounded residual decay (plus settle residual slack).
            const float sourceSpeed = 0.4f;
            float maximumAdjacentStep = 0.0f;
            Vector3 sourceOrigin = Vector3.Zero;
            Vector3 previousOutput = lastApproachOutput;
            for (int tick = 0; tick < 60; tick++)
            {
                sourceOrigin += Vector3.Right * (sourceSpeed * (float)PhysicsTickSeconds);
                defaultProvider.TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, sourceOrigin), 1.0f);
                provider.AdvanceSimulation(PhysicsTickSeconds);
                Vector3 output = provider.GetTargetIntent().WorldTransform.Origin;
                maximumAdjacentStep = MathF.Max(maximumAdjacentStep, output.DistanceTo(previousOutput));
                previousOutput = output;
            }

            float boundedStep = ((sourceSpeed + provider.HeldCarryMaximumSpeedMetresPerSecond) * (float)PhysicsTickSeconds)
                + HeldLagToleranceMetres;
            Assert.True(
                maximumAdjacentStep <= boundedStep,
                $"Adjacent held-carry output motion {maximumAdjacentStep:F4} m must stay within the carried "
                + $"source motion plus the bounded decay ({boundedStep:F4} m).");

            // A single-tick source step far beyond the carry bound is absorbed into the residual: the output's
            // step that tick is bounded, and the residual still converges onto the source afterwards.
            sourceOrigin += Vector3.Right * 0.15f;
            defaultProvider.TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, sourceOrigin), 1.0f);
            Vector3 beforeStep = provider.GetTargetIntent().WorldTransform.Origin;
            provider.AdvanceSimulation(PhysicsTickSeconds);
            float stepOutputMove = beforeStep.DistanceTo(provider.GetTargetIntent().WorldTransform.Origin);
            Assert.True(
                stepOutputMove <= (provider.HeldCarryMaximumSpeedMetresPerSecond * (float)PhysicsTickSeconds)
                    + HeldLagToleranceMetres,
                $"A stepped source must be absorbed into the decaying residual, not teleported; observed "
                + $"{stepOutputMove:F4} m of output motion in one tick.");

            int convergedTick = -1;
            for (int tick = 0; tick < 60; tick++)
            {
                sourceOrigin += Vector3.Right * (sourceSpeed * (float)PhysicsTickSeconds);
                defaultProvider.TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, sourceOrigin), 1.0f);
                provider.AdvanceSimulation(PhysicsTickSeconds);
                if (sourceOrigin.DistanceTo(provider.GetTargetIntent().WorldTransform.Origin) <= HeldLagToleranceMetres)
                {
                    convergedTick = tick;
                    break;
                }
            }

            Assert.True(
                convergedTick >= 0,
                "The absorbed source step must decay: the output must reconverge onto the moving source.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// A release that ends the override during the held-carry anchor also returns to true passthrough under
    /// sustained source motion — opening the fingers right after a grab leaves no tracking lag behind.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Provider_ReleaseDuringHeldCarry_CompletesToPassthroughUnderMotion()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "ReleaseDuringCarryRoot"
        };
        StubIntentProvider defaultProvider = new()
        {
            Name = "DefaultSource",
            TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, Vector3.Zero), 1.0f),
        };
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider",
            DefaultProvider = defaultProvider,
        };
        root.AddChild(defaultProvider);
        root.AddChild(provider);
        sceneTree.Root.AddChild(root);

        try
        {
            Vector3 command = new(0.09f, 0.0f, 0.0f);
            provider.SetGrabTarget(new Transform3D(Basis.Identity, command));
            for (int tick = 0; tick < 120; tick++)
            {
                provider.AdvanceSimulation(PhysicsTickSeconds);
            }

            provider.BeginHeldCarry();

            // Release mid-anchor, then keep the source moving.
            for (int tick = 0; tick < 10; tick++)
            {
                provider.AdvanceSimulation(PhysicsTickSeconds);
            }

            provider.ReleaseGrabTarget();

            const float sourceSpeed = 0.4f;
            Vector3 sourceOrigin = Vector3.Zero;
            int convergedTick = -1;
            for (int tick = 0; tick < 60; tick++)
            {
                sourceOrigin += Vector3.Right * (sourceSpeed * (float)PhysicsTickSeconds);
                defaultProvider.TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, sourceOrigin), 1.0f);
                provider.AdvanceSimulation(PhysicsTickSeconds);
                if (sourceOrigin.DistanceTo(provider.GetTargetIntent().WorldTransform.Origin) <= HeldLagToleranceMetres)
                {
                    convergedTick = tick;
                    break;
                }
            }

            Assert.True(
                convergedTick >= 0,
                "A release during the held-carry anchor must still converge to passthrough under sustained "
                + "source motion.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// The residual return must track hardware-representative sources whose basis is far from identity:
    /// a sustained translation under a 180°-yaw wrist basis must converge to passthrough with the output
    /// motion in the source's direction, and a direction reversal must never invert the output motion.
    /// This pins the frame of the carried source motion — applying the previous-sample-local motion
    /// (previous⁻¹ · current) as a world carry inverts output motion for flipped bases, which
    /// identity-basis fixtures can never catch (INTR-002 R2/R4; IK-005 TR20).
    /// </summary>
    [Headless]
    [Fact]
    public async Task Provider_HeldCarry_ReturnTracksSourceDirectionUnderRotatedBasisMotion()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "HeldCarryRotatedBasisRoot"
        };
        StubIntentProvider defaultProvider = new()
        {
            Name = "DefaultSource",
            TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, Vector3.Zero), 1.0f),
        };
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider",
            DefaultProvider = defaultProvider,
        };
        root.AddChild(defaultProvider);
        root.AddChild(provider);
        sceneTree.Root.AddChild(root);

        try
        {
            var rotatedBasis = Basis.FromEuler(new Vector3(0.0f, Mathf.Pi, 0.0f));
            Vector3 sourceOrigin = new(0.3f, 1.0f, -0.2f);
            Vector3 command = sourceOrigin + new Vector3(0.09f, 0.0f, 0.0f);
            defaultProvider.TargetIntent = new IKTargetIntent(new Transform3D(rotatedBasis, sourceOrigin), 1.0f);
            provider.SetGrabTarget(new Transform3D(rotatedBasis, command));
            for (int tick = 0; tick < 120; tick++)
            {
                provider.AdvanceSimulation(PhysicsTickSeconds);
            }

            provider.BeginHeldCarry();

            const float sourceSpeed = 0.4f;
            int anchorTicks = (int)Mathf.Ceil(provider.HeldCarryAnchorSeconds / (float)PhysicsTickSeconds);
            int firstConvergedTick = -1;
            int sustainedConvergedTicks = 0;
            float maximumConvergedOffset = 0.0f;
            float minimumDirectionDot = float.PositiveInfinity;
            Vector3 previousSource = sourceOrigin;
            Vector3 previousOutput = provider.GetTargetIntent().WorldTransform.Origin;

            // Phase 1 — sustained translation along +X under the rotated basis for 1.5 s.
            for (int tick = 0; tick < 90; tick++)
            {
                sourceOrigin += Vector3.Right * (sourceSpeed * (float)PhysicsTickSeconds);
                defaultProvider.TargetIntent = new IKTargetIntent(new Transform3D(rotatedBasis, sourceOrigin), 1.0f);
                provider.AdvanceSimulation(PhysicsTickSeconds);

                Vector3 output = provider.GetTargetIntent().WorldTransform.Origin;
                float offset = sourceOrigin.DistanceTo(output);
                if (offset <= HeldLagToleranceMetres)
                {
                    firstConvergedTick = firstConvergedTick < 0 ? tick : firstConvergedTick;
                    sustainedConvergedTicks++;
                    maximumConvergedOffset = MathF.Max(maximumConvergedOffset, offset);
                }
                else
                {
                    sustainedConvergedTicks = 0;
                }

                if (firstConvergedTick >= 0 && tick > firstConvergedTick)
                {
                    Vector3 sourceStep = sourceOrigin - previousSource;
                    Vector3 outputStep = output - previousOutput;
                    minimumDirectionDot = MathF.Min(minimumDirectionDot, sourceStep.Dot(outputStep));
                }

                previousSource = sourceOrigin;
                previousOutput = output;
            }

            Assert.True(
                firstConvergedTick >= 0 && firstConvergedTick <= anchorTicks + 40,
                $"The return must converge to passthrough under a rotated basis within the anchor plus a "
                + $"bounded return; first convergence at tick {firstConvergedTick} (anchor {anchorTicks} ticks).");
            Assert.True(
                sustainedConvergedTicks >= 30,
                $"Once converged, the return must stay on the moving rotated-basis source; observed only "
                + $"{sustainedConvergedTicks} consecutive converged ticks of 90.");
            Assert.True(
                maximumConvergedOffset <= HeldLagToleranceMetres,
                $"Converged output-versus-source offset {maximumConvergedOffset:F4} m must stay within the "
                + $"passthrough tolerance {HeldLagToleranceMetres:F4} m under a rotated basis.");
            Assert.True(
                minimumDirectionDot >= 0.0f,
                $"Output motion must track the source direction under a rotated basis after convergence; "
                + $"observed a negative source-versus-output step dot product {minimumDirectionDot:F6}.");

            // Phase 2 — reverse the translation direction for 1 s: the output must follow the reversal
            // without ever moving against the source, which is where a frame-inverted carry is most visible.
            int invertedDirectionTicks = 0;
            float worstInversionDot = 0.0f;
            for (int tick = 0; tick < 60; tick++)
            {
                sourceOrigin -= Vector3.Right * (sourceSpeed * (float)PhysicsTickSeconds);
                defaultProvider.TargetIntent = new IKTargetIntent(new Transform3D(rotatedBasis, sourceOrigin), 1.0f);
                provider.AdvanceSimulation(PhysicsTickSeconds);

                Vector3 output = provider.GetTargetIntent().WorldTransform.Origin;
                Vector3 sourceStep = sourceOrigin - previousSource;
                Vector3 outputStep = output - previousOutput;
                float directionDot = sourceStep.Dot(outputStep);
                if (directionDot < 0.0f)
                {
                    invertedDirectionTicks++;
                    worstInversionDot = MathF.Min(worstInversionDot, directionDot);
                }

                Assert.True(
                    sourceOrigin.DistanceTo(output) <= HeldLagToleranceMetres,
                    $"The converged return must stay on the source through a direction reversal; offset grew "
                    + $"to {sourceOrigin.DistanceTo(output):F4} m at reversal tick {tick}.");

                previousSource = sourceOrigin;
                previousOutput = output;
            }

            Assert.True(
                invertedDirectionTicks == 0,
                $"A direction reversal under a rotated basis must never invert output motion; observed "
                + $"{invertedDirectionTicks} inverted ticks with worst step dot product {worstInversionDot:F6}.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// A source rotating in place must be carried about its own origin: the converged return keeps the
    /// output on the fixed source position while the basis rotates, with no tangential swing. A carry that
    /// rotates the held pose about the world origin — or applies the source-local motion as a world
    /// transform — swings the output by a per-tick amount proportional to the source distance from the
    /// world origin, which identity-basis or world-origin-centred fixtures cannot catch.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Provider_HeldCarry_ReturnCarriesRotatingSourceAboutItsOrigin()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "HeldCarryRotatingSourceRoot"
        };
        StubIntentProvider defaultProvider = new()
        {
            Name = "DefaultSource",
            TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, Vector3.Zero), 1.0f),
        };
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider",
            DefaultProvider = defaultProvider,
        };
        root.AddChild(defaultProvider);
        root.AddChild(provider);
        sceneTree.Root.AddChild(root);

        try
        {
            Vector3 sourceOrigin = new(0.35f, 1.0f, -0.25f);
            var initialBasis = Basis.FromEuler(new Vector3(0.0f, 0.4f, 0.0f));
            defaultProvider.TargetIntent = new IKTargetIntent(new Transform3D(initialBasis, sourceOrigin), 1.0f);
            provider.SetGrabTarget(new Transform3D(initialBasis, sourceOrigin + new Vector3(0.09f, 0.0f, 0.0f)));
            for (int tick = 0; tick < 120; tick++)
            {
                provider.AdvanceSimulation(PhysicsTickSeconds);
            }

            provider.BeginHeldCarry();

            // Sustained rotation in place at 60°/s about the world up axis, 2 s total.
            const float sourceAngularSpeedDegrees = 60.0f;
            float sourceAngle = 0.0f;
            int anchorTicks = (int)Mathf.Ceil(provider.HeldCarryAnchorSeconds / (float)PhysicsTickSeconds);
            int firstConvergedTick = -1;
            int sustainedConvergedTicks = 0;
            float maximumConvergedOffset = 0.0f;
            float maximumConvergedBasisAngle = 0.0f;
            for (int tick = 0; tick < 120; tick++)
            {
                sourceAngle += Mathf.DegToRad(sourceAngularSpeedDegrees) * (float)PhysicsTickSeconds;
                var sourceBasis = Basis.FromEuler(new Vector3(0.0f, 0.4f + sourceAngle, 0.0f));
                defaultProvider.TargetIntent = new IKTargetIntent(new Transform3D(sourceBasis, sourceOrigin), 1.0f);
                provider.AdvanceSimulation(PhysicsTickSeconds);

                Transform3D output = provider.GetTargetIntent().WorldTransform;
                float offset = sourceOrigin.DistanceTo(output.Origin);
                float basisAngle = output.Basis.GetRotationQuaternion().AngleTo(sourceBasis.GetRotationQuaternion());
                if (offset <= HeldLagToleranceMetres && basisAngle <= 0.02f)
                {
                    firstConvergedTick = firstConvergedTick < 0 ? tick : firstConvergedTick;
                    sustainedConvergedTicks++;
                    maximumConvergedOffset = MathF.Max(maximumConvergedOffset, offset);
                    maximumConvergedBasisAngle = MathF.Max(maximumConvergedBasisAngle, basisAngle);
                }
                else
                {
                    sustainedConvergedTicks = 0;
                }
            }

            Assert.True(
                firstConvergedTick >= 0 && firstConvergedTick <= anchorTicks + 40,
                $"The return must converge onto a rotating in-place source within the anchor plus a bounded "
                + $"return; first convergence at tick {firstConvergedTick} (anchor {anchorTicks} ticks).");
            Assert.True(
                sustainedConvergedTicks >= 30,
                $"Once converged, the return must stay on the rotating source's fixed origin; observed only "
                + $"{sustainedConvergedTicks} consecutive converged ticks of 120.");
            Assert.True(
                maximumConvergedOffset <= HeldLagToleranceMetres,
                $"Converged output origin must stay on the rotating source's origin; observed "
                + $"{maximumConvergedOffset:F4} m of tangential swing.");
            Assert.True(
                maximumConvergedBasisAngle <= 0.02f,
                $"Converged output basis must track the rotating source basis; observed "
                + $"{maximumConvergedBasisAngle:F4} rad of basis deviation.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Sustained source motion beyond the held-carry carry bound (0.8 m/s against the 0.6 m/s clamp) must
    /// leave a bounded, non-growing residual offset in the source's motion direction — never a growing or
    /// direction-inverted offset — and the output must track the source direction throughout, including
    /// under a rotated basis.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Provider_HeldCarry_ReturnUnderSupraClampSourceMotion_StaysBoundedAndDirectionTrue()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "HeldCarrySupraClampRoot"
        };
        StubIntentProvider defaultProvider = new()
        {
            Name = "DefaultSource",
            TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, Vector3.Zero), 1.0f),
        };
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider",
            DefaultProvider = defaultProvider,
        };
        root.AddChild(defaultProvider);
        root.AddChild(provider);
        sceneTree.Root.AddChild(root);

        try
        {
            var rotatedBasis = Basis.FromEuler(new Vector3(0.0f, Mathf.Pi, 0.0f));
            Vector3 sourceOrigin = new(0.3f, 1.0f, -0.2f);
            defaultProvider.TargetIntent = new IKTargetIntent(new Transform3D(rotatedBasis, sourceOrigin), 1.0f);
            provider.SetGrabTarget(new Transform3D(rotatedBasis, sourceOrigin + new Vector3(0.09f, 0.0f, 0.0f)));
            for (int tick = 0; tick < 120; tick++)
            {
                provider.AdvanceSimulation(PhysicsTickSeconds);
            }

            provider.BeginHeldCarry();

            // Sustained 0.8 m/s translation — beyond the 0.6 m/s carry clamp — for 2 s under a rotated basis.
            const float sourceSpeed = 0.8f;
            const int convergenceAllowanceTicks = 90;
            float earlyOffsetSum = 0.0f;
            int earlyOffsetTicks = 0;
            float lateOffsetSum = 0.0f;
            int lateOffsetTicks = 0;
            float minimumDirectionDot = float.PositiveInfinity;
            Vector3 previousSource = sourceOrigin;
            Vector3 previousOutput = provider.GetTargetIntent().WorldTransform.Origin;
            for (int tick = 0; tick < 120; tick++)
            {
                sourceOrigin += Vector3.Right * (sourceSpeed * (float)PhysicsTickSeconds);
                defaultProvider.TargetIntent = new IKTargetIntent(new Transform3D(rotatedBasis, sourceOrigin), 1.0f);
                provider.AdvanceSimulation(PhysicsTickSeconds);

                Vector3 output = provider.GetTargetIntent().WorldTransform.Origin;
                if (tick >= convergenceAllowanceTicks)
                {
                    float offset = sourceOrigin.DistanceTo(output);
                    if (tick < convergenceAllowanceTicks + 15)
                    {
                        earlyOffsetSum += offset;
                        earlyOffsetTicks++;
                    }
                    else if (tick >= 105)
                    {
                        lateOffsetSum += offset;
                        lateOffsetTicks++;
                    }

                    Vector3 sourceStep = sourceOrigin - previousSource;
                    Vector3 outputStep = output - previousOutput;
                    minimumDirectionDot = MathF.Min(minimumDirectionDot, sourceStep.Dot(outputStep));
                }

                previousSource = sourceOrigin;
                previousOutput = output;
            }

            float earlyOffset = earlyOffsetSum / MathF.Max(1, earlyOffsetTicks);
            float lateOffset = lateOffsetSum / MathF.Max(1, lateOffsetTicks);
            Assert.True(
                earlyOffset <= 0.025f && lateOffset <= 0.025f,
                $"Supra-clamp source motion must leave a bounded residual offset; observed early mean "
                + $"{earlyOffset:F4} m and late mean {lateOffset:F4} m.");
            Assert.True(
                MathF.Abs(lateOffset - earlyOffset) <= 0.006f,
                $"The supra-clamp residual offset must be stable, not growing; moved from {earlyOffset:F4} m "
                + $"to {lateOffset:F4} m across the window.");
            Assert.True(
                minimumDirectionDot >= 0.0f,
                $"Output motion must track the supra-clamp source direction under a rotated basis; observed "
                + $"step dot product {minimumDirectionDot:F6}.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// The return must stay stable and direction-true when the source is re-expressed every tick through a
    /// stepping XR-origin cycle — the PlayerVRIK reset-then-compensate pattern — modelled as a per-tick
    /// changing origin snap folded into the source pose on top of true hand translation that reverses
    /// mid-window. The delta-based return must carry the snap without divergence and follow the reversal
    /// without inverting the output direction.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Provider_HeldCarry_ReturnTracksSourceThroughPerTickOriginReExpression()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "HeldCarryOriginResetRoot"
        };
        StubIntentProvider defaultProvider = new()
        {
            Name = "DefaultSource",
            TargetIntent = new IKTargetIntent(new Transform3D(Basis.Identity, Vector3.Zero), 1.0f),
        };
        HandGrabTargetProvider provider = new()
        {
            Name = "GrabProvider",
            DefaultProvider = defaultProvider,
        };
        root.AddChild(defaultProvider);
        root.AddChild(provider);
        sceneTree.Root.AddChild(root);

        try
        {
            var rotatedBasis = Basis.FromEuler(new Vector3(0.0f, Mathf.Pi, 0.0f));
            Vector3 walkOrigin = new(0.3f, 1.0f, -0.2f);
            defaultProvider.TargetIntent = new IKTargetIntent(new Transform3D(rotatedBasis, walkOrigin), 1.0f);
            provider.SetGrabTarget(new Transform3D(rotatedBasis, walkOrigin + new Vector3(0.09f, 0.0f, 0.0f)));
            for (int tick = 0; tick < 120; tick++)
            {
                provider.AdvanceSimulation(PhysicsTickSeconds);
            }

            provider.BeginHeldCarry();

            // True hand translation at 0.3 m/s that reverses mid-window, with a per-tick changing origin
            // snap (compensation-cycle re-expression) of a few millimetres folded into the source pose.
            const float walkSpeed = 0.3f;
            const int walkTicksPerPhase = 60;
            const int convergenceAllowanceTicks = 60;
            int anchorTicks = (int)Mathf.Ceil(provider.HeldCarryAnchorSeconds / (float)PhysicsTickSeconds);
            Assert.True(
                convergenceAllowanceTicks >= anchorTicks + 20,
                "The convergence allowance must exceed the anchor window.");
            Vector3 blockStartSourceOrigin = walkOrigin;
            Vector3 blockStartOutput = provider.GetTargetIntent().WorldTransform.Origin;
            float maximumPostConvergenceOffset = 0.0f;
            var offendingBlocks = new List<string>();
            for (int tick = 0; tick < 2 * walkTicksPerPhase; tick++)
            {
                float walkDirection = tick < walkTicksPerPhase ? 1.0f : -1.0f;
                walkOrigin += Vector3.Right * (walkDirection * walkSpeed * (float)PhysicsTickSeconds);
                Vector3 originSnap = new(
                    0.004f * Mathf.Sin(2.0f * Mathf.Pi * tick / 13.0f),
                    0.0f,
                    0.002f * Mathf.Cos(2.0f * Mathf.Pi * tick / 7.0f));
                Vector3 sourceOrigin = walkOrigin + originSnap;
                defaultProvider.TargetIntent = new IKTargetIntent(new Transform3D(rotatedBasis, sourceOrigin), 1.0f);
                provider.AdvanceSimulation(PhysicsTickSeconds);

                Vector3 output = provider.GetTargetIntent().WorldTransform.Origin;
                if (tick >= convergenceAllowanceTicks)
                {
                    maximumPostConvergenceOffset = MathF.Max(
                        maximumPostConvergenceOffset,
                        sourceOrigin.DistanceTo(output));
                }

                // Every aligned 8-tick block after the convergence allowance must displace the output in
                // the source's direction with a sane magnitude ratio; the first block starts at the
                // allowance boundary so the commit and return transients stay out of the measurement.
                if (tick == convergenceAllowanceTicks)
                {
                    blockStartSourceOrigin = sourceOrigin;
                    blockStartOutput = output;
                }
                else if (tick > convergenceAllowanceTicks && (tick - convergenceAllowanceTicks) % 8 == 7)
                {
                    Vector3 blockSourceStep = sourceOrigin - blockStartSourceOrigin;
                    Vector3 blockOutputStep = output - blockStartOutput;
                    float blockSourceX = blockSourceStep.X;
                    float blockOutputX = blockOutputStep.X;
                    bool directionMatches = MathF.Sign(blockOutputX) == MathF.Sign(blockSourceX);
                    float ratio = MathF.Abs(blockSourceX) <= 0.00001f
                        ? 1.0f
                        : MathF.Abs(blockOutputX / blockSourceX);
                    if (!directionMatches || ratio < 0.4f || ratio > 1.6f)
                    {
                        offendingBlocks.Add(
                            $"tick {tick}: source block X {blockSourceX:F4} m, output block X {blockOutputX:F4} m");
                    }

                    blockStartSourceOrigin = sourceOrigin;
                    blockStartOutput = output;
                }
            }

            Assert.True(
                maximumPostConvergenceOffset <= 0.015f,
                $"The return must stay on a re-expressed source within a bounded offset; observed "
                + $"{maximumPostConvergenceOffset:F4} m.");
            Assert.True(
                offendingBlocks.Count == 0,
                $"Output block motion must match the source direction through origin re-expression and the "
                + $"mid-window walk reversal; offending blocks: {string.Join("; ", offendingBlocks)}.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static void AssertTransformsApproximatelyEqual(Transform3D expected, Transform3D actual)
    {
        float positionResidual = expected.Origin.DistanceTo(actual.Origin);
        Assert.True(
            positionResidual <= PositionToleranceMetres,
            $"Expected matching origins; observed {positionResidual:F5} m between {expected.Origin} and {actual.Origin}.");
    }

    private sealed partial class StubIntentProvider : IKTargetIntentProvider
    {
        public IKTargetIntent TargetIntent { get; set; } = new(Transform3D.Identity, 1.0f);

        public override IKTargetIntent GetTargetIntent() => TargetIntent;
    }

    /// <summary>
    /// Reflection wrapper over the private VrikFixture in PlayerVRIKBridgeIntegrationTests, mirroring the
    /// established reflection-reuse pattern: the wiring target is CharacterIK itself, not fixture internals.
    /// </summary>
    private sealed class VrikFixtureReference(object fixture)
    {
        private static readonly Type _owner = typeof(PlayerVRIKBridgeIntegrationTests);

        public Node Root => Get<Node>("Root");

        public PlayerVRIK PlayerVRIK => Get<PlayerVRIK>("PlayerVRIK");

        public IXROrigin Origin => Get<IXROrigin>("Origin");

        public IXRCamera Camera => Get<IXRCamera>("Camera");

        private T Get<T>(string name) => (T)fixture.GetType().GetProperty(name)!.GetValue(fixture)!;

        public void InvokeUpdatePhysicalActuators(double delta)
            => _ = typeof(CharacterIK)
                .GetMethod("UpdatePhysicalActuators", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(PlayerVRIK, [delta]);

        public Task DisposeAsync(SceneTree sceneTree)
            => (Task)fixture.GetType().GetMethod("DisposeAsync")!.Invoke(fixture, [sceneTree])!;

        public static async Task<VrikFixtureReference> CreateAsync(SceneTree sceneTree)
        {
            var task = (Task)_owner
                .GetMethod("CreateVrikFixtureAsync", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [sceneTree, 1.6f, 1.0f])!;
            await task;
            return new VrikFixtureReference(task.GetType().GetProperty("Result")!.GetValue(task)!);
        }
    }
}
