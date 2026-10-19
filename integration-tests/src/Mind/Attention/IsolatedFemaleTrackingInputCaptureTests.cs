using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using AlleyCat.IK;
using AlleyCat.Mind.Attention;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.Attention;

public sealed partial class IsolatedFemaleTrackingIntegrationTests
{
    /// <summary>
    /// Opt-in final-candidate observation using the existing demand geometry, complete windows,
    /// production pipeline and stage capture. Each case is sealed and checked before the next.
    /// No tuning, solver override or historical evidence rewriting is performed.
    /// </summary>
    [Fact]
    public async Task FinalCandidate_DemandMatrix_CapturesCompleteWindowsUntilFirstRegression()
    {
        string? directory = System.Environment.GetEnvironmentVariable("ALLEYCAT_FINAL_DEMAND_MATRIX_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        bool animated = System.Environment.GetEnvironmentVariable("ALLEYCAT_FINAL_DEMAND_MATRIX_NATIVE") == "1";
        await using Fixture rig = await Fixture.CreateAsync(animated ? "final-matrix-native" : "final-matrix-frozen",
            new InputCaptureCharacterIK());
        if (animated)
        {
            rig.StartNativeIdle();
        }
        rig.ConfigureFreshInputCapture(directory);
        rig.ConfigureDownwardEvidence();
        rig.ConfigureAnimatedEntryObservation();
        await rig.ObserveAsync("framing_sanity", 0.15);
        if (System.Environment.GetEnvironmentVariable("ALLEYCAT_FINAL_DEMAND_MATRIX_FRAMING_ONLY") == "1")
        {
            rig.Finish();
            return;
        }

        foreach ((float pitch, float distance) in new[]
                 { (0f, 1f), (-20f, 1f), (-40f, 1f), (-60f, 1f), (-40f, 0.5f), (-60f, 0.5f) })
        {
            string label = distance >= 1f ? $"p{pitch}" : $"p{pitch}_d{distance:0.0#}";
            if (animated)
            {
                await rig.AnimatedWarmUpAsync();
            }
            else
            {
                await rig.WarmUpAsync();
            }
            await rig.CentreAsync(rig.Anchor(0, 0, 1.4f));
            await rig.ObserveAsync($"{label}_straight", 1);
            rig.CheckPreAssignmentAlignment();
            rig.AssignDownwardTarget(rig.Anchor(0, pitch, distance), observeFixation: true);
            await rig.ObserveAsync($"{label}_glance", 0.22);
            await rig.ObserveAsync($"{label}_settle", 2.38);
            await rig.ObserveAsync($"{label}_hold", 2.5);
            rig.CheckMatrixFixationAndRoll(label, requireEyeMotion: pitch != 0);
            rig.ActiveFixationWindow = null;
            if (animated)
            {
                rig.Eyes.ClearLookTarget();
                await rig.ObserveAsync("release", 1.6);
                rig.CheckAnimatedRelease();
            }
            else
            {
                await rig.ReleaseAsync();
            }
            rig.CheckDownwardFoldOutcome([label], animated, requireFoldWitness: pitch < 0);
            // Finish writes evidence before throwing. Never proceed past a complete failing window.
            rig.Finish();
        }
    }

    /// <summary>
    /// Opt-in conditional new-input capture, not historical recovery or a predictor/naturalness test.
    /// One unchanged original schedule distinguishes evaluated input from final solved output.
    /// </summary>
    [Fact]
    public async Task Diagnostic_FreshInputs_CapturesOriginalDemandThroughExistingMediation()
    {
        string? directory = System.Environment.GetEnvironmentVariable("ALLEYCAT_FRESH_INPUT_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory))
        {
            // This diagnostic has execution authority only when explicitly selected with an evidence directory.
            return;
        }

        Fixture rig = await Fixture.CreateAsync("fresh-conditional-inputs", new InputCaptureCharacterIK());
        Exception? primary = null;
        try
        {
            rig.ConfigureFreshInputCapture(directory);
            await rig.ObserveAsync("framing_sanity", 0.15);
            foreach (float pitch in new[] { 0f, -20f, -40f })
            {
                await rig.WarmUpAsync();
                await rig.CentreAsync(rig.Anchor(0, 0, 1.4f));
                await rig.ObserveAsync($"p{pitch}_straight", 1);
                rig.AssignDownwardTarget(rig.Anchor(0, pitch, 1));
                await rig.ObserveAsync($"p{pitch}_glance", 0.22);
                await rig.ObserveAsync($"p{pitch}_settle", 2.38);
                await rig.ObserveAsync($"p{pitch}_hold", 2.5);
                await rig.ReleaseAsync();
            }
            rig.Finish();
        }
        catch (Exception error)
        {
            primary = error;
            TryWriteCaptureError(directory, "primary-error.txt", error);
            throw;
        }
        finally
        {
            try
            {
                await rig.DisposeAsync();
            }
            catch (Exception cleanup) when (primary is not null)
            {
                TryWriteCaptureError(directory, "secondary-cleanup-error.txt", cleanup);
            }
        }
    }

    /// <summary>
    /// Opt-in steep-pose per-modifier stage diagnostic: records the SAME existing −60°/1 m steep target
    /// pose immediately after each modifier through the accepted seven-stage subscription hooks, with a
    /// p0 neutral pairing scenario. Test-only observation; no production change, naturalness claim,
    /// target/limit/order/influence change or additional scenario authorisation.
    /// </summary>
    [Fact]
    public async Task Diagnostic_SteepStages_RecordsSamePosePerModifierStage()
    {
        string? directory = System.Environment.GetEnvironmentVariable("ALLEYCAT_STEEP_STAGE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory))
        {
            // This diagnostic has execution authority only when explicitly selected with an evidence directory.
            return;
        }

        Fixture rig = await Fixture.CreateAsync("steep-stages", new InputCaptureCharacterIK());
        Exception? primary = null;
        try
        {
            rig.ConfigureFreshInputCapture(directory);
            await rig.ObserveAsync("framing_sanity", 0.15);
            // The reviewed demand schedule and slant-radius geometry are reused unchanged: neutral p0 pairing
            // reference first, then the single existing steep −60°/1 m case from the D baseline.
            foreach (float pitch in new[] { 0f, -60f })
            {
                await rig.WarmUpAsync();
                await rig.CentreAsync(rig.Anchor(0, 0, 1.4f));
                await rig.ObserveAsync($"p{pitch}_straight", 1);
                rig.AssignDownwardTarget(rig.Anchor(0, pitch, 1));
                await rig.ObserveAsync($"p{pitch}_glance", 0.22);
                await rig.ObserveAsync($"p{pitch}_settle", 2.38);
                await rig.ObserveAsync($"p{pitch}_hold", 2.5);
                await rig.ReleaseAsync();
            }
            rig.Finish();
        }
        catch (Exception error)
        {
            primary = error;
            TryWriteCaptureError(directory, "steep-stages-primary-error.txt", error);
            throw;
        }
        finally
        {
            try
            {
                await rig.DisposeAsync();
            }
            catch (Exception cleanup) when (primary is not null)
            {
                TryWriteCaptureError(directory, "steep-stages-secondary-cleanup-error.txt", cleanup);
            }
        }
    }

    /// <summary>
    /// Opt-in half-metre per-modifier stage capture for the ONLY two missing harder-scenario
    /// input packets (−40°/0.5 m, −60°/0.5 m) in the inherited neutral-viewpoint slant-radius
    /// metric. Reuses the accepted seven-stage hooks, complete original warm-ups and the
    /// unchanged glance/settle/hold/release windows. Test-only observation; no production,
    /// target, limit, order or influence change, and no neutral recapture (the sealed
    /// −60°/1 m stage packet is reused instead).
    /// </summary>
    [Fact]
    public async Task Diagnostic_HalfmetreStages_RecordsTwoMissingPackets()
    {
        string? directory = System.Environment.GetEnvironmentVariable("ALLEYCAT_HALFMETRE_STAGE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory))
        {
            // This diagnostic has execution authority only when explicitly selected with an evidence directory.
            return;
        }

        Fixture rig = await Fixture.CreateAsync("halfmetre-stages", new InputCaptureCharacterIK());
        Exception? primary = null;
        try
        {
            rig.ConfigureFreshInputCapture(directory);
            await rig.ObserveAsync("framing_sanity", 0.15);
            // The two missing packet-grade cases in the reviewed demand order, each with the complete
            // inherited schedule: original warm-up, 1.4 m re-centring, 1 s straight, assignment,
            // 0.22 s glance, 2.38 s settle, 2.5 s hold and the 1.6 s release minimum.
            foreach ((float pitch, float distance) in new[] { (-40f, 0.5f), (-60f, 0.5f) })
            {
                string label = $"p{pitch}_d{distance:0.0#}";
                await rig.WarmUpAsync();
                await rig.CentreAsync(rig.Anchor(0, 0, 1.4f));
                await rig.ObserveAsync($"{label}_straight", 1);
                rig.AssignDownwardTarget(rig.Anchor(0, pitch, distance));
                await rig.ObserveAsync($"{label}_glance", 0.22);
                await rig.ObserveAsync($"{label}_settle", 2.38);
                await rig.ObserveAsync($"{label}_hold", 2.5);
                await rig.ReleaseAsync();
            }
            rig.Finish();
        }
        catch (Exception error)
        {
            primary = error;
            TryWriteCaptureError(directory, "halfmetre-primary-error.txt", error);
            throw;
        }
        finally
        {
            try
            {
                await rig.DisposeAsync();
            }
            catch (Exception cleanup) when (primary is not null)
            {
                TryWriteCaptureError(directory, "halfmetre-secondary-cleanup-error.txt", cleanup);
            }
        }
    }

    /// <summary>
    /// Opt-in selected-candidate stage probe (user-approved single diagnostic run): records the
    /// UNCHANGED strength-12 downward-fold candidate through the installed pipeline for the existing
    /// 1 m trajectory — neutral 0°, −20°, the decisive accepted −40° reference and −60°, each with the
    /// complete original warm-up, centring, straight, glance 0.22 s, settle 2.38 s, hold 2.5 s and
    /// release 1.6 s schedule — pairing the accepted seven-stage hooks (Begin/chain_entry/CCD/copy/
    /// twist/SkeletonUpdated same-evaluation bone packets, PoseLocal + parent/child PoseGlobal, raw
    /// authored caps, actual modifier influence) with the prototype presentation finals in ONE
    /// evidence directory. Capture-then-classify only: inherited strict-red checks and every raw
    /// packet are retained; no production, target, limit, order, influence or tuning change, and no
    /// additional scenario. ALLEYCAT_DOWNWARD_FOLD_EVIDENCE must name this probe's
    /// downward-policy-prototype label so both evidence streams seal into the same directory.
    /// </summary>
    [Fact]
    public async Task Diagnostic_SelectedStages_RecordsStrength12OneMetreTrajectoryPerModifierStage()
    {
        string? directory = System.Environment.GetEnvironmentVariable("ALLEYCAT_SELECTED_STAGE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory))
        {
            // This diagnostic has execution authority only when explicitly selected with an evidence directory.
            return;
        }

        string? foldLabel = System.Environment.GetEnvironmentVariable("ALLEYCAT_DOWNWARD_FOLD_EVIDENCE");
        Assert.False(string.IsNullOrWhiteSpace(foldLabel),
            "ALLEYCAT_DOWNWARD_FOLD_EVIDENCE must name this probe's downward-policy-prototype label.");
        string expected = Path.GetFullPath(ProjectSettings.GlobalizePath(
            "res://temp/ai-009-design-review/downward-policy-prototype/" + foldLabel));
        Assert.True(string.Equals(expected, Path.GetFullPath(directory), StringComparison.Ordinal),
            $"ALLEYCAT_SELECTED_STAGE_EVIDENCE must be {expected} so stage packets and presentations seal together.");

        Fixture rig = await Fixture.CreateAsync("selected-stages", new InputCaptureCharacterIK());
        Exception? primary = null;
        try
        {
            rig.ConfigureFreshInputCapture(directory);
            rig.ConfigureDownwardEvidence();
            await rig.ObserveAsync("framing_sanity", 0.15);
            // The unchanged existing 1 m trajectory (the selected S capture schedule): other phases kept,
            // no extra matrix. The decisive window is the selected strength-12 −40° settle-through-hold.
            foreach ((float pitch, float distance) in new[] { (0f, 1f), (-20f, 1f), (-40f, 1f), (-60f, 1f) })
            {
                string label = distance >= 1f ? $"p{pitch}" : $"p{pitch}_d{distance:0.0#}";
                await rig.WarmUpAsync();
                await rig.CentreAsync(rig.Anchor(0, 0, 1.4f));
                await rig.ObserveAsync($"{label}_straight", 1);
                rig.AssignDownwardTarget(rig.Anchor(0, pitch, distance));
                await rig.ObserveAsync($"{label}_glance", 0.22);
                await rig.ObserveAsync($"{label}_settle", 2.38);
                await rig.ObserveAsync($"{label}_hold", 2.5);
                await rig.ReleaseAsync();
            }
            rig.CheckDownwardFoldOutcome(["p0", "p-20", "p-40", "p-60"]);
            rig.Finish();
        }
        catch (Exception error)
        {
            primary = error;
            TryWriteCaptureError(directory, "selected-stages-primary-error.txt", error);
            throw;
        }
        finally
        {
            try
            {
                await rig.DisposeAsync();
            }
            catch (Exception cleanup) when (primary is not null)
            {
                TryWriteCaptureError(directory, "selected-stages-secondary-cleanup-error.txt", cleanup);
            }
        }
    }

    private static void TryWriteCaptureError(string directory, string name, Exception error)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, name), error.ToString());
        }
        catch (Exception evidenceError)
        {
            error.Data["EvidenceWriteFailure"] = evidenceError.ToString();
        }
    }

    private sealed partial class InputCaptureCharacterIK : CharacterIK
    {
        private bool _observingPhysics;
        public Action<string, double>? Observe
        {
            get;
            set;
        }

        public override void _PhysicsProcess(double delta)
        {
            _observingPhysics = true;
            try
            {
                base._PhysicsProcess(delta);
            }
            finally
            {
                _observingPhysics = false;
            }
        }

        protected override void BeforeProviderTargetProcessing()
            => Observe?.Invoke(_observingPhysics ? "physics_before_provider" : "begin_before_provider", 0);

        protected override void AfterProviderTargetProcessing(Skeleton3D skeleton, double delta)
            => Observe?.Invoke("begin_after_provider", delta);
    }

    private sealed partial class Fixture
    {
        private string? _freshInputDirectory;
        private string _freshInputPhase = "configured";
        private readonly List<string> _freshInputStages = [];
        private readonly List<string> _freshInputPresentations = [];
        private readonly List<string> _freshInputEvents = [];
        private readonly List<string> _freshInputPhysics = [];
        private InputFileIdentity[] _freshInputFiles = [];
        private bool _freshInputPreflightPassed;
        private readonly List<(SkeletonModifier3D Modifier, Action Handler)> _freshInputSubscriptions = [];
        private int[] _freshInputBones = [];
        private long _freshInputEvaluation;
        private long _freshInputOrdinal;
        private long _freshInputCompletedEvaluation;
        private long _freshInputCompletedOrdinal;
        private Exception? _freshInputCallbackError;

        public void ConfigureFreshInputCapture(string directory)
        {
            _freshInputDirectory = directory;
            _ = Directory.CreateDirectory(directory);
            _freshInputFiles = BuildFreshInputProvenance(directory);
            List<int> ancestry = [];
            for (int bone = FixtureSkeleton.FindBone("Head"); bone >= 0; bone = FixtureSkeleton.GetBoneParent(bone))
            {
                ancestry.Add(bone);
            }
            ancestry.Reverse();
            _freshInputBones = [.. ancestry];
            Assert.Contains(FixtureSkeleton.FindBone("Hips"), _freshInputBones);
            // Raw authored half-caps are read once (cached) so every stage packet carries them.
            ReadAuthoredHalfCaps();

            SkeletonModifier3D[] modifiers = [.. FixtureSkeleton.GetChildren().OfType<SkeletonModifier3D>()];
            int neck = Array.FindIndex(modifiers, m => m.Name == "NeckSpineIK");
            Assert.True(neck > 0);
            Assert.Equal("CharacterIKFootProviderStage", modifiers[neck - 1].Name.ToString());
            Assert.Equal("HeadCopyRotation", modifiers[neck + 1].Name.ToString());
            Assert.Equal("NeckTwistDisperser", modifiers[neck + 2].Name.ToString());
            Subscribe(modifiers[neck - 1], "chain_entry");
            Subscribe(modifiers[neck], "ccdik_processed");
            Subscribe(modifiers[neck + 1], "copy_processed");
            Subscribe(modifiers[neck + 2], "twist_processed");
            ((InputCaptureCharacterIK)_ik).Observe = RecordFreshInputStage;
            FixtureSkeleton.SkeletonUpdated += OnFreshInputFinal;
            File.WriteAllText(Path.Combine(directory, "configuration-before.json"), FreshInputConfiguration());
            _freshInputPreflightPassed = true;
            File.WriteAllText(Path.Combine(directory, "preflight-result.json"), JsonSerializer.Serialize(new
            {
                Passed = true,
                Identity = "Conditional module metadata matches are NOT loaded-byte hashes",
                Time = Now
            }));
            FreshInputEvent("capture_start");

            void Subscribe(SkeletonModifier3D modifier, string stage)
            {
                void Handler()
                {
                    RecordFreshInputStage(stage, 0);
                }
                modifier.ModificationProcessed += Handler;
                _freshInputSubscriptions.Add((modifier, Handler));
            }
        }

        private void OnFreshInputFinal() => RecordFreshInputStage("skeleton_updated", 0);

        private object[] FreshInputBonePacket(IEnumerable<int> indices)
            => [.. indices.Select(i => (object)new
            {
                Index = i,
                Name = FixtureSkeleton.GetBoneName(i).ToString(),
                Parent = FixtureSkeleton.GetBoneParent(i),
                PoseLocal = Matrix(FixtureSkeleton.GetBonePose(i)),
                PoseGlobal = Matrix(FixtureSkeleton.GetBoneGlobalPose(i)),
                RestLocal = Matrix(FixtureSkeleton.GetBoneRest(i)),
                RestGlobal = Matrix(FixtureSkeleton.GetBoneGlobalRest(i))
            })];

        private object? FreshInputSnapshot()
        {
            OrientingUpstreamSnapshot? input = Controller.LastUpstreamSnapshot;
            return input is null ? null : new
            {
                input.Generation,
                input.EvaluationSequence,
                input.DeltaSeconds,
                input.EvaluationMode,
                input.SkeletonIdentity,
                CharacterGlobal = Matrix(input.CharacterGlobalTransform),
                SkeletonGlobal = Matrix(input.SkeletonGlobalTransform),
                HeadOriginWorld = Vector(input.HeadOriginWorld),
                PivotPositionWorld = Vector(input.PivotPositionWorld),
                FaceBasisWorld = Matrix(new Transform3D(input.UpstreamFaceBasisWorld, Vector3.Zero)),
                input.HasPivot
            };
        }

        private void RecordFreshInputStage(string stage, double delta)
        {
            try
            {
                RecordFreshInputStageCore(stage, delta);
            }
            catch (Exception error)
            {
                // Signal exceptions must not disappear into the engine's callback logger, or
                // abort the unchanged observation window. Preserve the first and fail at cleanup.
                _freshInputCallbackError ??= error;
            }
        }

        private void RecordFreshInputStageCore(string stage, double delta)
        {
            if (_freshInputDirectory is null)
            {
                return;
            }
            if (stage == "physics_before_provider")
            {
                _freshInputPhysics.Add(JsonSerializer.Serialize(new
                {
                    Stage = stage,
                    Time = Now,
                    Frame = Engine.GetProcessFrames(),
                    PhysicsFrame = Engine.GetPhysicsFrames(),
                    PhysicsTicksBefore = _ik.PhysicsActuatorTickCount
                }));
                return;
            }
            if (stage == "begin_before_provider")
            {
                ++_freshInputEvaluation;
            }
            long ordinal = ++_freshInputOrdinal;
            if (stage == "skeleton_updated")
            {
                _freshInputCompletedEvaluation = _freshInputEvaluation;
                _freshInputCompletedOrdinal = ordinal;
            }
            IKTargetIntent requested = Controller.GetTargetIntent(); // Pure accessor; never advance provider/policy.
            _freshInputStages.Add(JsonSerializer.Serialize(new
            {
                Stage = stage,
                Ordinal = ordinal,
                Evaluation = _freshInputEvaluation,
                Phase = _freshInputPhase,
                Time = Now,
                Frame = Engine.GetProcessFrames(),
                PhysicsFrame = Engine.GetPhysicsFrames(),
                Delta = stage == "begin_after_provider" ? (double?)delta : null,
                SkeletonIdentity = FixtureSkeleton.GetInstanceId(),
                Root = Matrix(_root.GlobalTransform),
                Skeleton = Matrix(FixtureSkeleton.GlobalTransform),
                // The before-provider record intentionally contains no previous controller snapshot as current input.
                Upstream = stage == "begin_before_provider" ? null : FreshInputSnapshot(),
                Feedback = stage == "begin_before_provider" ? null : new
                {
                    Controller.LastFeedbackSequence,
                    Controller.LastFeedbackIsBootstrap,
                    HeadWorld = Matrix(Controller.LastFeedbackHeadFrame),
                    EyeWorld = Vector(Controller.LastFeedbackEyeOrigin)
                },
                Bones = FreshInputBonePacket(_freshInputBones),
                FaceOffset = Matrix(FixtureViewpoint.Transform),
                PivotOffset = Matrix(Controller.NeckPivot!.Transform),
                Requested = Matrix(requested.WorldTransform),
                RequestedInfluence = requested.DesiredInfluence,
                MediatedHead = Matrix(_ik.HeadIKTarget!.GlobalTransform),
                HeadSolve = Matrix(_ik.HeadIKSolveTarget!.GlobalTransform),
                CommandRadians = new[] { Controller.LastPublishedAim.HorizontalRadians, Controller.LastPublishedAim.VerticalRadians },
                TargetIdentity = Eyes.LookTarget?.GetInstanceId(),
                Target = Eyes.LookTarget is { } target ? Vector(target.GlobalPosition) : null,
                Modifiers = _ik.HeadModifierGroup.Select(m => new { Name = m.Name.ToString(), m.Active, m.Influence }).ToArray(),
                // Raw authored half-caps read straight from the NeckSpineIK limitation resources, so every
                // stage packet is self-contained for cap judgements. Radians stay double-widened float32
                // values (the authored bits); degrees are computed in float64 to preserve the exact cap.
                AuthoredHalfCapsRadians = _authoredHalfCaps.Select(v => (double)v).ToArray(),
                AuthoredHalfCapsDegrees = _authoredHalfCaps.Select(v => v * 180d / Math.PI).ToArray()
            }));
        }

        private void RecordFreshInputPresentation(string phase)
        {
            if (_freshInputDirectory is null)
            {
                return;
            }
            _freshInputPresentations.Add(JsonSerializer.Serialize(new
            {
                Phase = phase,
                Sample = Samples[^1],
                Evaluation = _freshInputCompletedEvaluation,
                FinalOrdinal = _freshInputCompletedOrdinal,
                LastStageOrdinal = _freshInputOrdinal,
                Time = Now,
                Frame = Engine.GetProcessFrames(),
                PhysicsFrame = Engine.GetPhysicsFrames(),
                TargetIdentity = Eyes.LookTarget?.GetInstanceId(),
                Target = Eyes.LookTarget is { } target ? Vector(target.GlobalPosition) : null,
                NeutralFace = Matrix(new Transform3D(_neutralFace, _neutralOrigin)),
                // Actual output is separately cached at SkeletonUpdated; NEVER use this as upstream input.
                FinalFiveBones = _downwardBones.Select(Matrix).ToArray()
            }));
        }

        private void FreshInputEvent(string kind, string? phase = null, double? duration = null, StaticVisualCue? target = null)
        {
            if (_freshInputDirectory is null)
            {
                return;
            }
            _freshInputEvents.Add(JsonSerializer.Serialize(new
            {
                Event = kind,
                Phase = phase ?? _freshInputPhase,
                Time = Now,
                Frame = Engine.GetProcessFrames(),
                Duration = duration,
                Evaluation = _freshInputEvaluation,
                TargetIdentity = target?.GetInstanceId() ?? Eyes.LookTarget?.GetInstanceId(),
                Target = target is null ? null : Vector(target.GlobalPosition)
            }));
        }

        private sealed record InputModuleIdentity(string FullName, string ModuleName, Guid Mvid);

        private sealed record InputFileIdentity(
            string Owner, string OriginalPath, string? ResolvedPath, string Status,
            InputModuleIdentity? RuntimeMetadata = null, string? CandidatePath = null,
            InputModuleIdentity? CandidateMetadata = null, string? FileSHA256 = null,
            string? CandidateSHA256 = null, long? Bytes = null);

        private static InputFileIdentity[] BuildFreshInputProvenance(string directory)
        {
            const string model = "res://assets/characters/reference/female/reference_female.blend";
            List<InputFileIdentity> entries =
            [
                FileEntry("imported-model-source", model),
                FileEntry("import-descriptor", model + ".import"),
                new("generated-imported-scene", "<pending-remap>", null, "pending-remap"),
                FileEntry("female-template", "res://assets/characters/templates/reference_female/reference_female_base.tscn"),
                FileEntry("fixture-gdscript", "res://tests/ai009_head_orientation/isolated_female_tracking.gd"),
                FileEntry("engine-executable", OS.GetExecutablePath()),
                AssemblyEntry("runtime-game", typeof(CharacterIK).Assembly,
                    "res://.godot/mono/temp/bin/Debug/AlleyCat.dll"),
                AssemblyEntry("runtime-test", typeof(IsolatedFemaleTrackingIntegrationTests).Assembly,
                    "res://../integration-tests/bin/Debug/net10.0/AlleyCat.IntegrationTests.dll"),
                AssemblyEntry("runtime-framework", typeof(HeadlessAttribute).Assembly,
                    "res://../integration-tests/bin/Debug/net10.0/AlleyCat.TestFramework.dll")
            ];
            WriteInventory("preflight-paths-before-open.json");
            string currentOwner = "import-descriptor";
            try
            {
                var descriptor = new ConfigFile();
                Assert.Equal(Error.Ok, descriptor.Load(model + ".import"));
                entries[2] = FileEntry("generated-imported-scene", descriptor.GetValue("remap", "path").AsString());
                WriteInventory("preflight-paths-resolved.json");
                for (int i = 0; i < entries.Count; i++)
                {
                    InputFileIdentity entry = entries[i];
                    currentOwner = entry.Owner;
                    bool conditional = entry.RuntimeMetadata is not null && string.IsNullOrEmpty(entry.OriginalPath);
                    string? path = conditional ? entry.CandidatePath : entry.ResolvedPath;
                    entries[i] = entry with
                    {
                        Status = conditional ? "opening-sealed-candidate" : "opening-file"
                    };
                    WriteInventory("preflight-inventory.json"); // Ownership and paths precede every file open.
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    {
                        throw new FileNotFoundException($"Required provenance unavailable: {entry.Owner}", path);
                    }
                    using FileStream stream = File.OpenRead(path);
                    string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                    InputModuleIdentity? candidate = null;
                    if (entry.RuntimeMetadata is { } runtime)
                    {
                        stream.Position = 0;
                        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                        MetadataReader metadata = pe.GetMetadataReader();
                        ModuleDefinition module = metadata.GetModuleDefinition();
                        candidate = new InputModuleIdentity(AssemblyName.GetAssemblyName(path).FullName ?? string.Empty,
                            metadata.GetString(module.Name), metadata.GetGuid(module.Mvid));
                        if (runtime.Mvid == Guid.Empty || runtime != candidate)
                        {
                            throw new InvalidOperationException($"Runtime/candidate module metadata mismatch: {entry.Owner}; runtime={runtime}; candidate={candidate}");
                        }
                    }
                    entries[i] = entry with
                    {
                        Status = conditional ? "conditional-metadata-match-NOT-loaded-byte-hash" : "file-backed-hash",
                        CandidateMetadata = candidate,
                        FileSHA256 = conditional ? null : hash,
                        CandidateSHA256 = conditional ? hash : null,
                        Bytes = stream.Length
                    };
                    WriteInventory("preflight-inventory.json");
                }
                return [.. entries];
            }
            catch (Exception error)
            {
                try
                {
                    File.WriteAllText(Path.Combine(directory, "preflight-result.json"), JsonSerializer.Serialize(new
                    {
                        Passed = false,
                        Owner = currentOwner,
                        Error = error.ToString()
                    }));
                }
                catch (Exception)
                {
                    // Secondary evidence failure must not replace the original provenance exception.
                }
                throw;
            }

            void WriteInventory(string name)
            {
                File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(entries));
            }

            static string? Resolve(string path)
            {
                return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(
                    path.StartsWith("res://", StringComparison.Ordinal) ? ProjectSettings.GlobalizePath(path) : path);
            }

            static InputFileIdentity FileEntry(string owner, string path)
            {
                return new InputFileIdentity(owner, path, Resolve(path), string.IsNullOrEmpty(path) ? "empty-file-path" : "unhashed-file");
            }

            static InputFileIdentity AssemblyEntry(string owner, Assembly assembly, string candidate)
            {
                string location = assembly.Location;
                var runtime = new InputModuleIdentity(assembly.FullName ?? string.Empty,
                    assembly.ManifestModule.ScopeName, assembly.ManifestModule.ModuleVersionId);
                return new InputFileIdentity(owner, location, Resolve(location),
                    string.IsNullOrEmpty(location) ? "empty-runtime-Location" : "unhashed-runtime-file",
                    runtime, string.IsNullOrEmpty(location) ? Resolve(candidate) : null);
            }
        }

        private string FreshInputConfiguration()
        {
            const string modelPath = "res://assets/characters/reference/female/reference_female.blend";
            var import = new ConfigFile();
            Assert.Equal(Error.Ok, import.Load(modelPath + ".import"));
            string generated = import.GetValue("remap", "path").AsString();
            PackedScene loaded = ResourceLoader.Load<PackedScene>(modelPath);
            return JsonSerializer.Serialize(new
            {
                Evidence = "CONDITIONAL NEW-INPUT EVIDENCE; not historical imported-model identity",
                Engine = Engine.GetVersionInfo().ToString(),
                Executable = OS.GetExecutablePath(),
                ImportedResourcePath = loaded.ResourcePath,
                ImportedResourceIdentity = loaded.GetInstanceId(),
                GeneratedResourcePath = generated,
                Files = _freshInputFiles,
                FilesCapturedAtPreflightOnly = true,
                SkeletonIdentity = FixtureSkeleton.GetInstanceId(),
                SkeletonPath = FixtureSkeleton.GetPath().ToString(),
                Root = Matrix(_root.GlobalTransform),
                Skeleton = Matrix(FixtureSkeleton.GlobalTransform),
                FixtureSkeleton.MotionScale,
                FixtureSkeleton.ModifierCallbackModeProcess,
                AllInstantiatedBones = FreshInputBonePacket(Enumerable.Range(0, FixtureSkeleton.GetBoneCount())),
                AffectedBoneIndices = _freshInputBones,
                JointBoneNames = Enumerable.Range(0, 5).Select(i => FixtureSkeleton.GetNode("NeckSpineIK")
                    .Call("get_joint_bone_name", 0, i).AsString()).ToArray(),
                FaceOffset = Matrix(FixtureViewpoint.Transform),
                FaceBone = FixtureSkeleton.FindBone("Head"),
                PivotOffset = Matrix(Controller.NeckPivot!.Transform),
                PivotBone = FixtureSkeleton.FindBone("Neck"),
                HeadTargetPath = _ik.HeadIKTarget!.GetPath().ToString(),
                HeadSolvePath = _ik.HeadIKSolveTarget!.GetPath().ToString(),
                ProviderIdentity = Controller.GetInstanceId(),
                Order = FixtureSkeleton.GetChildren().OfType<SkeletonModifier3D>().Select(m => new
                {
                    Name = m.Name.ToString(),
                    Index = m.GetIndex(),
                    Class = m.GetClass().ToString(),
                    m.Active,
                    m.Influence
                }).ToArray(),
                HeadSettings = _ik.HeadModifierGroup.Select(m => new
                {
                    Name = m.Name.ToString(),
                    Properties = m.GetPropertyList().Select(p => p["name"].AsString())
                        .Where(n => n.StartsWith("settings/", StringComparison.Ordinal) || n == "setting_count")
                        .ToDictionary(n => n, n => FreshInputValue(m.Get(n)))
                }).ToArray(),
                Controller.AimSmoothingSeconds,
                Eyes.LookSmoothingTime,
                Eyes.MaxHorizontalAngleDegrees,
                Eyes.MaxVerticalAngleDegrees,
                GPU = RenderingServer.GetVideoAdapterName(),
                Renderer = RenderingServer.GetCurrentRenderingMethod()
            });
        }

        private static object? FreshInputValue(Variant value)
        {
            if (value.VariantType == Variant.Type.Nil)
            {
                return null;
            }
            if (value.VariantType == Variant.Type.Bool)
            {
                return value.AsBool();
            }
            if (value.VariantType == Variant.Type.Int)
            {
                return value.AsInt64();
            }
            if (value.VariantType == Variant.Type.Float)
            {
                return value.AsDouble();
            }
            if (value.VariantType == Variant.Type.Vector3)
            {
                return Vector(value.AsVector3());
            }
            if (value.VariantType == Variant.Type.Quaternion)
            {
                Quaternion q = value.AsQuaternion();
                return new[] { q.X, q.Y, q.Z, q.W };
            }
            return value.VariantType == Variant.Type.Object && value.AsGodotObject() is Resource resource
                ? new
                {
                    Class = resource.GetClass().ToString(),
                    resource.ResourcePath,
                    Properties = resource.GetPropertyList().Select(p => p["name"].AsString())
                        .Where(n => n == "angle").ToDictionary(n => n, n => resource.Get(n).AsDouble())
                }
                : value.ToString();
        }

        private void CompleteFreshInputCapture()
        {
            if (_freshInputDirectory is not { } directory)
            {
                return;
            }
            FreshInputEvent("capture_end");
            ((InputCaptureCharacterIK)_ik).Observe = null;
            foreach ((SkeletonModifier3D modifier, Action handler) in _freshInputSubscriptions)
            {
                modifier.ModificationProcessed -= handler;
            }
            FixtureSkeleton.SkeletonUpdated -= OnFreshInputFinal;
            try
            {
                File.WriteAllLines(Path.Combine(directory, "evaluated-stages.jsonl"), _freshInputStages);
                File.WriteAllLines(Path.Combine(directory, "presentations.jsonl"), _freshInputPresentations);
                File.WriteAllLines(Path.Combine(directory, "events.jsonl"), _freshInputEvents);
                File.WriteAllLines(Path.Combine(directory, "physics-only.jsonl"), _freshInputPhysics);
                if (_freshInputPreflightPassed)
                {
                    File.WriteAllText(Path.Combine(directory, "configuration-after.json"), FreshInputConfiguration());
                }
                if (_freshInputCallbackError is { } callbackError)
                {
                    TryWriteCaptureError(directory, "stage-callback-error.txt", callbackError);
                    throw new InvalidOperationException("Stage observation callback failed; capture is incomplete.", callbackError);
                }
            }
            finally
            {
                _freshInputDirectory = null;
            }
        }
    }
}
