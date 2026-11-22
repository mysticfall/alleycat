using System.Net;
using System.Text.Json;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Decision;
using AlleyCat.Mind.AI.Lore;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Sense;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.Attention;

/// <summary>
/// Focused runtime coverage for the decision-driven gaze selector: composition and adaptation, cadence and
/// coalescing, the zero-candidate short-circuit, failure containment with backoff, and the speak-intention nudge.
/// </summary>
[Headless]
public sealed class AttentionGazeTargetSelectorIntegrationTests
{
    /// <summary>Two parked Minds sharing a client retain private histories and independent flight ownership.</summary>
    [Fact]
    public async Task SharedClient_SecondMindDecidesWhileFirstMindIsInFlight()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        MutableSceneContext scene = new();
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CombinedClient client = new()
        {
            Hold = held,
            Sent = firstSent
        };

        TestMind CreateMind(string id)
        {
            VoicedCharacter character = new(id, new FakeVoice(id));
            scene.Add(character);
            TestMind mind = new(character)
            {
                WaitEvaluationIntervalSeconds = 0.02f
            };
            mind.SetSceneDecisionClientForTesting(client);
            mind.SetSceneContextLoaderForTesting(() => scene);
            (tree.CurrentScene ?? tree.Root).AddChild(mind);
            return mind;
        }

        TestMind alice = CreateMind("alice");
        TestMind bob = CreateMind("bob");
        try
        {
            alice.IngestToolObservations([new ObservedSpeech(null, "red parcel")]);
            bob.IngestToolObservations([new ObservedSpeech(null, "blue envelope")]);
            Task<MindBase.WaitOutcome> a = alice.WaitForNextTurnAsync(CancellationToken.None);
            await firstSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.Hold = null;
            Assert.Equal(MindBase.ObservationWaitWake.DecisionAct,
                (await bob.WaitForNextTurnAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Wake);
            Assert.False(a.IsCompleted);
            Assert.Equal(2, client.Requests.Count);
            JsonElement first = ((JsonElement)client.Requests[0].Evidence).GetProperty("recent_history");
            JsonElement second = ((JsonElement)client.Requests[1].Evidence).GetProperty("recent_history");
            Assert.Equal("char:alice", first.GetProperty("perspective").GetString());
            Assert.Contains("red parcel", first.GetRawText());
            Assert.DoesNotContain("blue envelope", first.GetRawText());
            Assert.Equal("char:bob", second.GetProperty("perspective").GetString());
            Assert.Contains("blue envelope", second.GetRawText());
            Assert.DoesNotContain("red parcel", second.GetRawText());
            held.SetResult();
            Assert.Equal(MindBase.ObservationWaitWake.DecisionAct, (await a.WaitAsync(TimeSpan.FromSeconds(5))).Wake);
        }
        finally
        {
            _ = held.TrySetResult();
            alice.QueueFree();
            bob.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
        }
    }

    /// <summary>A frame gathers a parked wait and gaze together; the lower-prominence model choice reaches Vision.</summary>
    [Fact]
    public async Task SharedScenePass_WakesParkedMindAndAppliesModelSelectedCue()
    {
        await ExerciseSharedPassAsync(false);
        await ExerciseSharedPassAsync(true);
    }

    private static async Task ExerciseSharedPassAsync(bool dump)
    {
        SceneTree tree = TestUtils.GetSceneTree();
        string root = ProjectSettings.GlobalizePath($"res://temp/pluggable-scene-decisions/transcripts/{Guid.NewGuid():N}-{dump}");
        MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
        MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
        DecisionPayloadDumpRecorder.SetRootPathOverrideForTesting(root);
        TestVision vision = new();
        VoicedProjectionCharacter owner = new(new FakeVoice("owner-voice"), vision);
        TestMind mind = new(owner)
        {
            Diagnostics = new(false, true, true, dump),
            WaitEvaluationIntervalSeconds = 0.02f
        };
        TestVisualCue face = new()
        {
            ID = "face",
            Prominence = 3f
        };
        TestVisualCue body = new()
        {
            ID = "body",
            Prominence = 1f
        };
        MutableSceneContext scene = new();
        scene.Add(owner);
        scene.Add(new TestVisualSubject("target", [face, body]));
        CombinedClient client = new();
        mind.SetSceneContextLoaderForTesting(() => scene);
        mind.SetSceneDecisionClientForTesting(client);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        selector.EvaluationIntervalSeconds = 120;
        (tree.CurrentScene ?? tree.Root).AddChild(mind);
        try
        {
            // Hold the frame boundary across the first wait cadence so the selector's initial poll and wait
            // poll coincide, then release ordinary scene processing; neither gate is invoked by the fixture.
            mind.SetProcess(false);
            mind.ReinforceAttention("char:target", 1f);
            Task<MindBase.WaitOutcome> wait = mind.WaitForNextTurnAsync(CancellationToken.None, 30);
            await Task.Delay(TimeSpan.FromSeconds(5 * mind.WaitEvaluationIntervalSeconds));
            mind.SetProcess(true);
            Assert.Equal(MindBase.ObservationWaitWake.DecisionAct, (await wait.WaitAsync(TimeSpan.FromSeconds(5))).Wake);
            DecisionRequest request = Assert.Single(client.Requests);
            Assert.Equal(["gaze_target", "response", "initiative"], request.Questions.Select(q => q.Name));
            JsonElement evidence = Assert.IsType<JsonElement>(request.Evidence);
            Assert.Equal("waiting", evidence.GetProperty("own_action").GetString());
            Assert.Equal("char:owner", evidence.GetProperty("recent_history").GetProperty("perspective").GetString());
            Assert.False(evidence.TryGetProperty("trigger", out _));
            Assert.Equal([body], vision.SetTargets);

            // An unchanged wait-only answer is still a second actual wake, not a repeated change entry.
            Assert.Equal(MindBase.ObservationWaitWake.DecisionAct,
                (await mind.WaitForNextTurnAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Wake);
            Assert.Equal(["response", "initiative"], client.Requests[1].Questions.Select(q => q.Name));
            await MindSessionTranscriptRecorder.FlushDecisionWritesForTesting();
            string index = File.ReadAllText(Assert.Single(Directory.GetFiles(root, "index.md", SearchOption.AllDirectories)));
            Assert.Equal(3, index.Split("**Decision:**").Length - 1);
            Assert.Equal(2, index.Split("**Wake:** immediate decision").Length - 1);
            Assert.Equal(1, index.Split("pre-empted the armed deadline").Length - 1);
            Assert.Equal(dump, index.Contains("[payload]", StringComparison.Ordinal));
            Assert.Equal(dump ? 3 : 0, Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length);
            // The prior immediate verdict cannot explain a later timed wake. Only a changed selected value
            // gets a new decision entry; the reason for each actual wake must always be visible.
            mind.SetWaitTimingForTesting(new(1, 1, 0.18, 0));
            client.Response = "not_expecting";
            Assert.Equal(MindBase.ObservationWaitWake.PauseElapsed,
                (await mind.WaitForNextTurnAsync(CancellationToken.None, 3).WaitAsync(TimeSpan.FromSeconds(5))).Wake);
            client.Fail = true;
            Assert.Equal(MindBase.ObservationWaitWake.PauseElapsed,
                (await mind.WaitForNextTurnAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Wake);
            mind.WaitEvaluationIntervalSeconds = 120;
            Assert.Equal(MindBase.ObservationWaitWake.TimerElapsed,
                (await mind.WaitForNextTurnAsync(CancellationToken.None, 0.03f).WaitAsync(TimeSpan.FromSeconds(5))).Wake);
            await MindSessionTranscriptRecorder.FlushDecisionWritesForTesting();
            string indexPath = Assert.Single(Directory.GetFiles(root, "index.md", SearchOption.AllDirectories));
            index = File.ReadAllText(indexPath);
            Assert.Equal(4, index.Split("**Decision:**").Length - 1);
            Assert.Equal(5, index.Split("**Wake:**").Length - 1);
            Assert.Contains("shortened deadline elapsed (no_reason)", index);
            Assert.Contains("armed deadline elapsed (evaluation failure; no-reason fallback)", index);
            Assert.Contains("explicit wait deadline elapsed", index);
            foreach (string line in index.Split('\n').Where(l => l.Contains("[payload]", StringComparison.Ordinal)))
            {
                string link = line.Split("[payload](<")[1].Split(">)")[0];
                Assert.DoesNotContain("#", link);
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(indexPath)!, Uri.UnescapeDataString(link))));
            }

            if (dump)
            {
                // The offered face is valid and different from the assigned body. Invalidate it only
                // after transport observes the combined request, not during candidate preparation.
                client.Fail = false;
                client.Response = "expecting_arrived";
                client.Cue = "char:target#face";
                TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Hold = held;
                client.Sent = sent;
                mind.SetProcess(false);
                mind.WaitEvaluationIntervalSeconds = 0.02f;
                selector.RequestEvaluation();
                Task<MindBase.WaitOutcome> parked = mind.WaitForNextTurnAsync(CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(5 * mind.WaitEvaluationIntervalSeconds));
                mind.SetProcess(true);
                await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
                DecisionRequest combined = client.Requests[^1];
                Assert.Equal(["gaze_target", "response", "initiative"], combined.Questions.Select(q => q.Name));
                Assert.Contains(((ChoiceQuestion)combined.Questions[0]).Options, o => o.Value == client.Cue);
                face.Prominence = 0;
                held.SetResult();
                Assert.Equal(MindBase.ObservationWaitWake.DecisionAct, (await parked.WaitAsync(TimeSpan.FromSeconds(5))).Wake);
                Assert.Same(body, vision.LookTarget);
                Assert.Equal([body], vision.SetTargets);
                Assert.Equal(0, vision.ClearCount);
                await MindSessionTranscriptRecorder.FlushDecisionWritesForTesting();
                Assert.Equal(6, File.ReadAllText(indexPath).Split("**Wake:**").Length - 1);
            }

            if (!dump)
            {
                // Hold a shared request past the old wait's explicit expiry. The re-parked Mind must await
                // its own verdict; the original still-valid gaze must nevertheless apply to Vision.
                client.Fail = false;
                client.Response = "expecting_arrived";
                client.Cue = "char:target#face";
                TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Hold = held;
                int before = client.Requests.Count;
                mind.SetProcess(false);
                mind.WaitEvaluationIntervalSeconds = 0.02f;
                selector.RequestEvaluation();
                Task<MindBase.WaitOutcome> expiring = mind.WaitForNextTurnAsync(CancellationToken.None, 0.4f);
                await Task.Delay(TimeSpan.FromSeconds(5 * mind.WaitEvaluationIntervalSeconds));
                mind.SetProcess(true);
                Assert.Equal(MindBase.ObservationWaitWake.TimerElapsed, (await expiring.WaitAsync(TimeSpan.FromSeconds(5))).Wake);
                Assert.Equal(before + 1, client.Requests.Count);
                Assert.Equal(3, client.Requests[^1].Questions.Count);
                Assert.False(client.ActiveToken.IsCancellationRequested);
                Assert.Equal([body], vision.SetTargets);
                Task<MindBase.WaitOutcome> parked = mind.WaitForNextTurnAsync(CancellationToken.None);
                TaskCompletionSource next = new(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Hold = next;
                TaskCompletionSource sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Sent = sent;
                held.SetResult();
                await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(parked.IsCompleted);
                Assert.Equal([body, face], vision.SetTargets);
                Assert.Equal(["response", "initiative"], client.Requests[^1].Questions.Select(q => q.Name));
                next.SetResult();
                Assert.Equal(MindBase.ObservationWaitWake.DecisionAct, (await parked.WaitAsync(TimeSpan.FromSeconds(5))).Wake);
            }
        }
        finally
        {
            mind.QueueFree();
            await TestUtils.WaitForFramesAsync(tree, 2);
            face.Free();
            body.Free();
            vision.Dispose();
            await MindSessionTranscriptRecorder.FlushDecisionWritesForTesting();
            DecisionPayloadDumpRecorder.SetRootPathOverrideForTesting(null);
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(null);
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
        }
    }

    private sealed class CombinedClient : IDecisionClient
    {
        public string Response { get; set; } = "expecting_arrived";
        public string Cue { get; set; } = "char:target#body";
        public TaskCompletionSource? Hold
        {
            get; set;
        }
        public TaskCompletionSource? Sent
        {
            get; set;
        }
        public CancellationToken ActiveToken
        {
            get; private set;
        }
        public bool Fail
        {
            get; set;
        }

        public List<DecisionRequest> Requests { get; } = [];

        public async Task<DecisionResult> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            ActiveToken = cancellationToken;
            _ = Sent?.TrySetResult();
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }
            if (Fail)
            {
                throw new DecisionException(DecisionFailure.Timeout, "Scripted timeout");
            }

            DecisionAnswer[] answers = [.. request.Questions.Cast<ChoiceQuestion>().Select(q => new ChoiceAnswer(q,
                q.Name switch { "response" => Response, "initiative" => "no_reason", "gaze_target" => Cue, _ => throw new InvalidOperationException() },
                Requests.Count == 1 ? 0.7 : 0.9, null))];
            return new DecisionResult("script", null, null, null, null, answers, answers.ToDictionary(a => a.Name));
        }
    }

    /// <summary>Binding waits for projection, refreshes Vision safely, and never makes the selector a character component.</summary>
    [Fact]
    public void Lifecycle_WaitsForProjectionRefreshesVisionAndKeepsMindChildOutOfComponents()
    {
        ScriptedDecisionClient client = new();
        TestVision firstVision = new();
        TestCharacter character = new(hasComponentProjection: false, firstVision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue cue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject subject = new("target", [cue]);
        scene.Add(subject);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        try
        {
            mind.ReinforceAttention(subject.FullId, 1f);
            selector._Ready();
            Tick(selector, 0d);

            Assert.Empty(client.Requests);
            Assert.Empty(firstVision.SetTargets);
            Assert.Equal(1, character.ComponentsRefreshedHandlerCount);
            Assert.Same(mind, selector.GetParent());
            Assert.False(selector is IComponent);
            Assert.DoesNotContain(character.Components, component => ReferenceEquals(selector, component));

            TestVision secondVision = new();
            client.AnswerValue = $"{subject.FullId}#body";
            character.RefreshComponents(secondVision);
            Tick(selector, 0d);

            _ = Assert.Single(client.Requests);
            Assert.Equal([cue], secondVision.SetTargets);
            Assert.Equal(0, secondVision.ClearCount);
            selector._ExitTree();
            Assert.Equal(0, secondVision.ClearCount);

            Assert.Equal(0, character.ComponentsRefreshedHandlerCount);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            firstVision.Dispose();
            cue.Free();
        }
    }

    /// <summary>Current snapshot identities resolve only through scene context and assign the subject's best cue.</summary>
    [Fact]
    public void Evaluate_ResolvesVisualCuesFromSnapshotAndRebindsOnProjectionRefresh()
    {
        ScriptedDecisionClient client = new(answerValue: "char:target#face");
        TestVision firstVision = new();
        TestVision secondVision = new();
        TestCharacter character = new(hasComponentProjection: true, firstVision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue faceCue = new()
        {
            ID = "face",
            Prominence = 3f
        };
        TestVisualCue bodyCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject subject = new("target", [faceCue, bodyCue]);
        scene.Add(subject);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        try
        {
            mind.ReinforceAttention(subject.FullId, 1f);
            selector._Ready();
            Tick(selector, 0d);

            // Apply the exact cue named by the model.
            Assert.Equal([faceCue], firstVision.SetTargets);
            Assert.Equal(0, firstVision.ClearCount);

            character.RefreshComponents(secondVision);
            Tick(selector, 0d);

            Assert.Equal([faceCue], secondVision.SetTargets);
            Assert.Equal(0, secondVision.ClearCount);
            Assert.Equal(0, firstVision.ClearCount);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            faceCue.Free();
            bodyCue.Free();
        }
    }

    /// <summary>Periodic evaluation dispatches only at cadence boundaries; no catch-up happens between them.</summary>
    [Fact]
    public void Cadence_DispatchesOnlyAtIntervalBoundaries()
    {
        ScriptedDecisionClient client = new(answerValue: "char:alpha#body");
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue alphaCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualCue bravoCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject alpha = new("alpha", [alphaCue]);
        TestVisualSubject bravo = new("bravo", [bravoCue]);
        scene.Add(alpha);
        scene.Add(bravo);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        selector.EvaluationIntervalSeconds = 1f;
        try
        {
            mind.ReinforceAttention(alpha.FullId, 1f);
            mind.ReinforceAttention(bravo.FullId, 1f);
            selector._Ready();
            Tick(selector, 0d);

            Assert.Equal([alphaCue], vision.SetTargets);
            _ = Assert.Single(client.Requests);

            client.AnswerValue = "char:bravo#body";
            Tick(selector, 0.9d);
            _ = Assert.Single(client.Requests);
            Assert.Equal([alphaCue], vision.SetTargets);

            Tick(selector, 0.1d);
            Assert.Equal(2, client.Requests.Count);
            Assert.Equal([alphaCue, bravoCue], vision.SetTargets);
            Assert.Equal(0, vision.ClearCount);

            // A parked scene with no elapsed time dispatches nothing further.
            Tick(selector, 0d);
            Assert.Equal(2, client.Requests.Count);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            alphaCue.Free();
            bravoCue.Free();
        }
    }

    /// <summary>Zero candidates with no current target short-circuits without a model call; zero candidates with a
    /// current target consults the model, and its none answer clears.</summary>
    [Fact]
    public void Evaluate_ShortCircuitsWithoutACallOnlyWhenNoCandidatesAndNoCurrentTarget()
    {
        ScriptedDecisionClient client = new();
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue disabledCue = new()
        {
            ID = "disabled",
            Prominence = 0f
        };
        TestVisualCue validCue = new()
        {
            ID = "valid",
            Prominence = 1f
        };
        TestVisualSubject invalidSubject = new("invalid", [disabledCue]);
        TestVisualSubject validSubject = new("valid", [validCue]);
        scene.Add(new NonVisualIdentifiable("nonvisual"));
        scene.Add(invalidSubject);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        try
        {
            mind.ReinforceAttention("char:missing", 1f);
            mind.ReinforceAttention("char:nonvisual", 1f);
            mind.ReinforceAttention(invalidSubject.FullId, 1f);
            selector._Ready();
            Tick(selector, 0d);

            Assert.Empty(client.Requests);
            Assert.Empty(vision.SetTargets);
            Assert.Equal(0, vision.ClearCount);

            scene.Add(validSubject);
            mind.ReinforceAttention(validSubject.FullId, 1f);
            client.AnswerValue = $"{validSubject.FullId}#valid";
            selector.RequestEvaluation();
            Tick(selector, 0d);
            Assert.Equal([validCue], vision.SetTargets);
            _ = Assert.Single(client.Requests);

            scene.Remove(validSubject.FullId);
            client.AnswerValue = DecisionGazePolicy.NoneOptionValue;
            selector.RequestEvaluation();
            Tick(selector, 0d);
            Assert.Equal(2, client.Requests.Count);
            Assert.Equal(1, vision.ClearCount);

            // Nothing further to clear: repeated none stays silent.
            selector.RequestEvaluation();
            Tick(selector, 0d);
            Assert.Equal(1, vision.ClearCount);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            disabledCue.Free();
            validCue.Free();
        }
    }

    /// <summary>Every failure mode keeps the current target, applies backoff, and recovers on the next success.</summary>
    [Fact]
    public void Failure_KeepsCurrentTargetBacksOffAndRecovers()
    {
        ScriptedDecisionClient client = new(answerValue: "char:alpha#body");
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue alphaCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualCue bravoCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject alpha = new("alpha", [alphaCue]);
        TestVisualSubject bravo = new("bravo", [bravoCue]);
        scene.Add(alpha);
        scene.Add(bravo);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        selector.EvaluationIntervalSeconds = 1f;
        try
        {
            mind.ReinforceAttention(alpha.FullId, 1f);
            mind.ReinforceAttention(bravo.FullId, 0.5f);
            selector._Ready();
            Tick(selector, 0d);

            Assert.Equal([alphaCue], vision.SetTargets);

            client.DecisionFailure = DecisionFailure.Timeout;
            Tick(selector, 1d);
            Assert.Equal([alphaCue], vision.SetTargets);
            Assert.Equal(0, vision.ClearCount);
            Assert.Equal(2, client.Requests.Count);

            // The 2× interval backoff suppresses the next boundary…
            Tick(selector, 2.9d);
            Assert.Equal(2, client.Requests.Count);

            // …and elapsing it retries.
            Tick(selector, 0.1d);
            Assert.Equal(3, client.Requests.Count);
            Assert.Equal([alphaCue], vision.SetTargets);

            // A success answering for the same subject re-assigns nothing and resets the backoff…
            client.DecisionFailure = null;
            Tick(selector, 5d);
            Assert.Equal(4, client.Requests.Count);
            Assert.Equal([alphaCue], vision.SetTargets);

            // …so the next boundary evaluates at the base interval again.
            client.AnswerValue = "char:bravo#body";
            Tick(selector, 1d);
            Assert.Equal(5, client.Requests.Count);
            Assert.Equal([alphaCue, bravoCue], vision.SetTargets);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            alphaCue.Free();
            bravoCue.Free();
        }
    }

    /// <summary>Refusals and unknown choice values keep the current target exactly like typed failures.</summary>
    [Fact]
    public void RefusalAndUnknownChoice_KeepTheCurrentTarget()
    {
        ScriptedDecisionClient client = new(answerValue: "char:alpha#body");
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue alphaCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject alpha = new("alpha", [alphaCue]);
        scene.Add(alpha);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        try
        {
            mind.ReinforceAttention(alpha.FullId, 1f);
            selector._Ready();
            Tick(selector, 0d);
            Assert.Equal([alphaCue], vision.SetTargets);

            client.Refuse = true;
            selector.RequestEvaluation();
            Tick(selector, 0d);
            Assert.Equal([alphaCue], vision.SetTargets);
            Assert.Equal(0, vision.ClearCount);

            client.Refuse = false;
            client.AnswerValue = "char:hallucinated#face";
            selector.RequestEvaluation();
            Tick(selector, 0d);
            Assert.Equal([alphaCue], vision.SetTargets);
            Assert.Equal(0, vision.ClearCount);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            alphaCue.Free();
        }
    }

    /// <summary>A vanished chosen subject keeps the current target as a no-op for the next cadence.</summary>
    [Fact]
    public void VanishedChosenSubject_KeepsTheCurrentTarget()
    {
        ScriptedDecisionClient client = new(answerValue: "char:alpha#body");
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue alphaCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject alpha = new("alpha", [alphaCue]);
        scene.Add(alpha);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        try
        {
            mind.ReinforceAttention(alpha.FullId, 1f);
            selector._Ready();
            Tick(selector, 0d);
            Assert.Equal([alphaCue], vision.SetTargets);

            // The subject leaves the scene between the decision and its application.
            scene.Remove(alpha.FullId);
            client.AnswerValue = "char:alpha#body";
            selector.RequestEvaluation();
            Tick(selector, 0d);

            Assert.Equal([alphaCue], vision.SetTargets);
            Assert.Equal(0, vision.ClearCount);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            alphaCue.Free();
        }
    }

    /// <summary>An overdue cadence survives slow success, but not the longer deadline set by slow failure.</summary>
    [Fact]
    public async Task SlowCompletion_RechecksCadenceWithoutDroppingRealNudges()
    {
        foreach (bool fail in new[] { false, true })
        {
            TestVision vision = new();
            TestMind mind = new(new TestCharacter(true, vision));
            MutableSceneContext scene = new();
            TestVisualCue cue = new()
            {
                ID = "body",
                Prominence = 1
            };
            scene.Add(new TestVisualSubject("target", [cue]));
            TaskCompletionSource held = new();
            CombinedClient client = new()
            {
                Hold = held,
                Fail = fail
            };
            AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
            selector.EvaluationIntervalSeconds = 1;
            try
            {
                mind.ReinforceAttention("char:target", 1f);
                selector._Ready();
                Tick(selector, 0);
                Tick(selector, 1.25);
                _ = Assert.Single(client.Requests);
                held.SetResult();
                await TestUtils.WaitForFramesAsync(TestUtils.GetSceneTree(), 2);
                Tick(selector, 0);
                if (fail)
                {
                    Tick(selector, 1.74); // t=2.99: before the 1s cadence + 2s first-failure backoff.
                    _ = Assert.Single(client.Requests);
                    selector.RequestEvaluation();
                    Tick(selector, 0);
                }

                Assert.Equal(2, client.Requests.Count);
            }
            finally
            {
                selector._ExitTree();
                selector.Free();
                mind.Free();
                cue.Free();
                vision.Dispose();
            }
        }
    }

    /// <summary>Only one evaluation is in flight; nudges during a flight coalesce into the next evaluation.</summary>
    [Fact]
    public async Task Coalescing_NudgesDuringFlightFoldIntoTheNextEvaluation()
    {
        GatedDecisionClient client = new();
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue alphaCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualCue bravoCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject alpha = new("alpha", [alphaCue]);
        TestVisualSubject bravo = new("bravo", [bravoCue]);
        scene.Add(alpha);
        scene.Add(bravo);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        try
        {
            mind.ReinforceAttention(alpha.FullId, 1f);
            mind.ReinforceAttention(bravo.FullId, 0.9f);
            selector._Ready();
            Tick(selector, 0d);

            // The first evaluation is in flight and pending; no target is applied yet.
            _ = Assert.Single(client.Requests);
            Assert.Empty(vision.SetTargets);

            // Cadence boundaries during the flight dispatch nothing…
            Tick(selector, 10d);
            _ = Assert.Single(client.Requests);

            // …and nudges coalesce.
            selector.RequestEvaluation();
            selector.RequestEvaluation();
            _ = Assert.Single(client.Requests);

            client.CompleteInFlight("char:alpha#body");
            await PumpUntilAppliedAsync(selector, vision, expectedCount: 1);
            Assert.Equal([alphaCue], vision.SetTargets);

            // The coalesced nudge starts exactly one follow-up evaluation.
            Tick(selector, 0d);
            Assert.Equal(2, client.Requests.Count);

            client.CompleteInFlight("char:bravo#body");
            await PumpUntilAppliedAsync(selector, vision, expectedCount: 2);
            Assert.Equal([alphaCue, bravoCue], vision.SetTargets);
            Assert.Equal(0, vision.ClearCount);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            alphaCue.Free();
            bravoCue.Free();
        }
    }

    /// <summary>
    /// Pumps selector process callbacks with small thread-pool yields until the expected number of targets is
    /// applied, proving the completion settles without any inline-continuation assumption.
    /// </summary>
    private static async Task PumpUntilAppliedAsync(
        AttentionGazeTargetSelector selector,
        TestVision vision,
        int expectedCount)
    {
        for (int attempt = 0; attempt < 200 && vision.SetTargets.Count < expectedCount; attempt++)
        {
            Tick(selector, 0d);
            await Task.Delay(10);
        }

        Assert.True(
            vision.SetTargets.Count >= expectedCount,
            $"The gated evaluation did not apply within the pump window; applied {vision.SetTargets.Count} of {expectedCount}.");
    }

    /// <summary>Node exit cancels and never applies a settling evaluation.</summary>
    [Fact]
    public async Task ExitTree_NeverAppliesASettlingEvaluation()
    {
        GatedDecisionClient client = new();
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue cue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject subject = new("target", [cue]);
        scene.Add(subject);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        try
        {
            mind.ReinforceAttention(subject.FullId, 1f);
            selector._Ready();
            Tick(selector, 0d);
            _ = Assert.Single(client.Requests);

            selector._ExitTree();
            client.CompleteInFlight(subject.FullId);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                Tick(selector, 0d);
                await Task.Delay(10);
            }

            Assert.Empty(vision.SetTargets);
            Assert.Equal(0, vision.ClearCount);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            cue.Free();
        }
    }

    /// <summary>The speak-intention nudge carries the about-to-speak context into the dispatched question.</summary>
    [Fact]
    public void SpeakIntentionNudge_CarriesTheAboutToSpeakContext()
    {
        ScriptedDecisionClient client = new();
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue cue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject subject = new("target", [cue]);
        scene.Add(subject);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        try
        {
            mind.ReinforceAttention(subject.FullId, 1f);
            selector._Ready();
            Tick(selector, 0d);
            _ = Assert.Single(client.Requests);

            mind.NotifySpeakIntention([subject.FullId]);
            Tick(selector, 0d);

            Assert.Equal(2, client.Requests.Count);
            DecisionRequest request = client.Requests[1];
            ChoiceQuestion question = Assert.IsType<ChoiceQuestion>(Assert.Single(request.Questions), exactMatch: false);
            Assert.Contains($"You are about to speak to `{subject.FullId}`. ", question.Instructions);

            JsonElement evidence = Assert.IsType<JsonElement>(request.Evidence);
            Assert.Equal($"speaking_to: `{subject.FullId}`", evidence.GetProperty("own_action").GetString());
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            cue.Free();
        }
    }

    /// <summary>Own speech end and attended speaker end nudge evaluation through the real Mind voice events.</summary>
    [Fact]
    public async Task SpeechLifecycleEvents_NudgeEvaluationWithTheirTriggerContext()
    {
        ScriptedDecisionClient client = new();
        TestVision vision = new();
        FakeVoice ownVoice = new("owner-voice");
        FakeVoice otherVoice = new("other-voice");
        VoicedProjectionCharacter owner = new(ownVoice, vision);
        VoicedCharacter other = new("other", otherVoice);
        TestMind mind = new(owner);
        TestVisualCue cue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject subject = new("target", [cue]);
        MutableSceneContext scene = new();
        scene.Add(subject);
        scene.Add(owner);
        scene.Add(other);
        mind.SetSceneContextLoaderForTesting(() => scene);
        SceneTree sceneTree = TestUtils.GetSceneTree();
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        // Automatic frame processing is disabled so the test drives every evaluation boundary manually.
        selector.SetProcess(false);
        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            mind.ReinforceAttention(subject.FullId, 1f);
            mind.ReinforceAttention(other.FullId, 1f);
            Tick(selector, 0d);
            _ = Assert.Single(client.Requests);

            // Own speaking window closes: re-evaluate with the just-finished context and no speaking intention.
            ownVoice.BeginSpeech();
            ownVoice.EndSpeech();
            Tick(selector, 0d);

            Assert.Equal(2, client.Requests.Count);
            DecisionRequest ownEndRequest = client.Requests[1];
            ChoiceQuestion ownEndQuestion = Assert.IsType<ChoiceQuestion>(
                Assert.Single(ownEndRequest.Questions),
                exactMatch: false);
            Assert.Contains("You have just finished speaking. ", ownEndQuestion.Instructions);
            Assert.Equal("idle", Assert.IsType<JsonElement>(ownEndRequest.Evidence)
                .GetProperty("own_action")
                .GetString());

            // An attended speaker finishes: re-evaluate with the just-heard context.
            otherVoice.BeginSpeech();
            otherVoice.EndSpeech();
            Tick(selector, 0d);

            DecisionRequest heardRequest = client.Requests[2];
            ChoiceQuestion heardQuestion = Assert.IsType<ChoiceQuestion>(
                Assert.Single(heardRequest.Questions),
                exactMatch: false);
            Assert.Contains("You have just heard `char:other` finish speaking. ", heardQuestion.Instructions);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            cue.Free();
            vision.Dispose();
        }
    }

    /// <summary>
    /// The scenario premise resolved once from the parent AgenticMind's scenario manager rides every gaze
    /// question's instructions under its framing label; the initial evaluation after binding already carries it.
    /// </summary>
    [Fact]
    public void ScenePremise_FromTheParentAgenticMindScenarioManager_RidesTheQuestionInstructions()
    {
        ScriptedDecisionClient client = new();
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        FixedDescriptionScenarioManager manager = new()
        {
            Description = "A detention interview in the basement of the Charter Office of Compliance.",
        };
        TestAgenticMind mind = new(character)
        {
            ScenarioManager = manager,
        };
        MutableSceneContext scene = new();
        scene.Add(character);
        scene.PlayerOverride = character;
        TestVisualCue cue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject subject = new("target", [cue]);
        scene.Add(subject);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        try
        {
            mind.ReinforceAttention(subject.FullId, 1f);
            selector._Ready();
            Tick(selector, 0d);

            DecisionRequest request = Assert.Single(client.Requests);
            ChoiceQuestion question = Assert.IsType<ChoiceQuestion>(Assert.Single(request.Questions), exactMatch: false);
            // The premise rides the evidence, never the question.
            Assert.DoesNotContain("detention interview", question.Instructions, StringComparison.Ordinal);
            Assert.Equal(
                "A detention interview in the basement of the Charter Office of Compliance.",
                Assert.IsType<JsonElement>(request.Evidence).GetProperty("premise").GetString());
            // The premise resolves exactly once per selector lifetime, never per evaluation.
            Assert.Equal(1, manager.Calls);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            cue.Free();
        }
    }

    /// <summary>A scenario manager that yields no scenario leaves the questions premise-free (clean omission).</summary>
    [Fact]
    public void ScenePremise_WithNoScenarioAvailable_IsOmittedCleanly()
    {
        ScriptedDecisionClient client = new();
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        FixedDescriptionScenarioManager manager = new()
        {
            Description = null
        };
        TestAgenticMind mind = new(character)
        {
            ScenarioManager = manager,
        };
        MutableSceneContext scene = new();
        scene.Add(character);
        scene.PlayerOverride = character;
        TestVisualCue cue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject subject = new("target", [cue]);
        scene.Add(subject);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        try
        {
            mind.ReinforceAttention(subject.FullId, 1f);
            selector._Ready();
            Tick(selector, 0d);

            ChoiceQuestion question = Assert.IsType<ChoiceQuestion>(
                Assert.Single(Assert.Single(client.Requests).Questions),
                exactMatch: false);
            Assert.DoesNotContain("Scene premise:", question.Instructions, StringComparison.Ordinal);
            // The manager was consulted once and honestly reported no scenario.
            Assert.Equal(1, manager.Calls);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            cue.Free();
        }
    }

    /// <summary>
    /// A premise resolution still in flight holds the first evaluation entirely (GZ2-B1): nothing dispatches
    /// until the manager settles, and the first dispatched question then carries the premise.
    /// </summary>
    [Fact]
    public async Task ScenePremise_AnInFlightResolutionHoldsTheFirstEvaluationUntilItSettles()
    {
        ScriptedDecisionClient client = new();
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        GatedScenarioManager manager = new();
        TestAgenticMind mind = new(character)
        {
            ScenarioManager = manager,
        };
        MutableSceneContext scene = new();
        scene.Add(character);
        scene.PlayerOverride = character;
        TestVisualCue cue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject subject = new("target", [cue]);
        scene.Add(subject);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        try
        {
            mind.ReinforceAttention(subject.FullId, 1f);
            selector._Ready();
            Tick(selector, 0d);
            Tick(selector, 10d);
            Assert.Empty(client.Requests);

            manager.Complete("A slowly rendered scenario premise.");
            for (int attempt = 0; attempt < 100 && client.Requests.Count == 0; attempt++)
            {
                Tick(selector, 0d);
                await Task.Delay(10);
            }

            DecisionRequest request = Assert.Single(client.Requests);
            ChoiceQuestion question = Assert.IsType<ChoiceQuestion>(Assert.Single(request.Questions), exactMatch: false);
            // The settled premise feeds the evidence's premise field, never the question.
            Assert.DoesNotContain("slowly rendered", question.Instructions, StringComparison.Ordinal);
            Assert.Equal(
                "A slowly rendered scenario premise.",
                Assert.IsType<JsonElement>(request.Evidence).GetProperty("premise").GetString());
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            cue.Free();
        }
    }

    /// <summary>
    /// A subject entering attention mid-backoff nudges an evaluation through the membership poll and resets the
    /// failure backoff (GZ2-B3): the evaluation dispatches long before the backed-off cadence boundary would.
    /// </summary>
    [Fact]
    public void MembershipChange_AMidBackoffSubjectGainNudgesEvaluationAndResetsBackoff()
    {
        ScriptedDecisionClient client = new(answerValue: "char:alpha#body");
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue alphaCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualCue bravoCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject alpha = new("alpha", [alphaCue]);
        TestVisualSubject bravo = new("bravo", [bravoCue]);
        scene.Add(alpha);
        scene.Add(bravo);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        selector.EvaluationIntervalSeconds = 1f;
        try
        {
            mind.ReinforceAttention(alpha.FullId, 1f);
            selector._Ready();
            Tick(selector, 0d);
            Assert.Equal([alphaCue], vision.SetTargets);

            client.DecisionFailure = DecisionFailure.Timeout;
            Tick(selector, 1d);
            Assert.Equal(2, client.Requests.Count);
            Assert.Equal([alphaCue], vision.SetTargets);

            // A brand-new subject enters attention mid-backoff; before a full interval elapses nothing fires…
            mind.ReinforceAttention(bravo.FullId, 1f);
            client.DecisionFailure = null;
            client.AnswerValue = "char:bravo#body";
            Tick(selector, 0.5d);
            Assert.Equal(2, client.Requests.Count);

            // …at the interval the poll detects the gain, nudges, and clears the backoff.
            Tick(selector, 0.5d);
            Tick(selector, 0d);
            Assert.Equal(3, client.Requests.Count);
            Assert.Equal([alphaCue, bravoCue], vision.SetTargets);

            // The backoff is gone: the next base-interval boundary dispatches instead of waiting out 2× more.
            Tick(selector, 1d);
            Assert.Equal(4, client.Requests.Count);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            alphaCue.Free();
            bravoCue.Free();
        }
    }

    /// <summary>
    /// The currently applied gaze subject leaving attention mid-backoff is a meaningful membership change too
    /// (GZ2-B3): the poll nudges an evaluation, the backoff resets, and the model's none answer clears the
    /// dead target.
    /// </summary>
    [Fact]
    public void MembershipChange_LosingTheCurrentGazeSubjectMidBackoffNudgesAndClears()
    {
        ScriptedDecisionClient client = new(answerValue: "char:alpha#body");
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        TestMind mind = new(character)
        {
            AttentionDecayPerSecond = 10f,
        };
        double attentionClockSeconds = 0d;
        mind.SetAttentionClockForTesting(() => attentionClockSeconds);
        MutableSceneContext scene = new();
        TestVisualCue alphaCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject alpha = new("alpha", [alphaCue]);
        scene.Add(alpha);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        selector.EvaluationIntervalSeconds = 1f;
        try
        {
            mind.ReinforceAttention(alpha.FullId, 1f);
            selector._Ready();
            Tick(selector, 0d);
            Assert.Equal([alphaCue], vision.SetTargets);

            client.DecisionFailure = DecisionFailure.Timeout;
            Tick(selector, 1d);
            Assert.Equal(2, client.Requests.Count);

            // The subject's attention decays away entirely, leaving the gaze target dead while backoff is active.
            client.DecisionFailure = null;
            client.AnswerValue = DecisionGazePolicy.NoneOptionValue;
            attentionClockSeconds += 10d;
            Tick(selector, 0.5d);
            Assert.Equal(2, client.Requests.Count);

            Tick(selector, 0.5d);
            Tick(selector, 0d);
            Assert.Equal(3, client.Requests.Count);
            Assert.Equal(1, vision.ClearCount);
            Assert.Equal([alphaCue], vision.SetTargets);
            // The backoff reset itself is isolated by the subject-gain fact above: there the re-baselined
            // membership keeps the poll silent, so only the reset cadence boundary can dispatch.
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            alphaCue.Free();
        }
    }

    /// <summary>
    /// An attended speaker's segment onset nudges exactly one evaluation — repeated onsets before a process
    /// callback coalesce — and the owner's own voice onset nudges nothing (GZ2-B3).
    /// </summary>
    [Fact]
    public async Task AttendedSpeakerStart_NudgesExactlyOneCoalescedEvaluation()
    {
        ScriptedDecisionClient client = new();
        TestVision vision = new();
        FakeVoice ownVoice = new("owner-voice");
        FakeVoice otherVoice = new("other-voice");
        VoicedProjectionCharacter owner = new(ownVoice, vision);
        VoicedCharacter other = new("other", otherVoice);
        TestMind mind = new(owner);
        TestVisualCue cue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject subject = new("target", [cue]);
        MutableSceneContext scene = new();
        scene.Add(subject);
        scene.Add(owner);
        scene.Add(other);
        mind.SetSceneContextLoaderForTesting(() => scene);
        SceneTree sceneTree = TestUtils.GetSceneTree();
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        selector.SetProcess(false);
        try
        {
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            mind.ReinforceAttention(subject.FullId, 1f);
            mind.ReinforceAttention(other.FullId, 1f);
            Tick(selector, 0d);
            _ = Assert.Single(client.Requests);

            otherVoice.BeginSegment();
            otherVoice.BeginSegment();
            Tick(selector, 0d);

            Assert.Equal(2, client.Requests.Count);

            // The owner's own voice never carries the attended-segment cue, so it nudges nothing.
            ownVoice.BeginSegment();
            Tick(selector, 0d);
            Assert.Equal(2, client.Requests.Count);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            cue.Free();
            vision.Dispose();
        }
    }

    /// <summary>Scenario manager holding its one resolution open until the test completes it.</summary>
    private sealed partial class GatedScenarioManager : ScenarioManager
    {
        private TaskCompletionSource<string> _gate = new();

        public override async ValueTask<Scenario?> GetCurrentScenario(
            IReadOnlyDictionary<string, object?> coreContext)
            => new(await _gate.Task.ConfigureAwait(false));

        /// <summary>Settles the in-flight resolution with the supplied description.</summary>
        public void Complete(string description)
        {
            _gate.SetResult(description);
            _gate = new TaskCompletionSource<string>();
        }
    }

    /// <summary>
    /// Lore query service double answering character queries with fixed descriptions; every other query shape is
    /// unused by the gaze path.
    /// </summary>
    private sealed class ScriptedLoreQueryService(IReadOnlyDictionary<string, string> descriptions)
        : ILoreQueryService
    {
        public Task<IReadOnlyList<LoreEntry>> QueryAsync(
            ContentContext content,
            LoreQuery query,
            CancellationToken cancellationToken = default)
        {
            List<LoreEntry> entries = [];
            foreach (LoreSubjectRequest subject in query.Subjects)
            {
                if (subject.SubjectID is { } subjectId
                    && descriptions.TryGetValue(subjectId, out string? description))
                {
                    entries.Add(new LoreEntry(subjectId, subjectId, string.Empty, Description: description));
                }
            }

            return Task.FromResult<IReadOnlyList<LoreEntry>>(entries);
        }

        public Task<IReadOnlyList<LoreEntry>> QueryCatalogueAsync(
            ContentContext content,
            LoreCatalogueQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The gaze path never queries the catalogue.");

        public Task<IReadOnlyList<LoreEntryLookup>> QueryEntriesAsync(
            ContentContext content,
            LoreEntryIDQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The gaze path never queries exact entries.");
    }

    /// <summary>
    /// The model picks the cue, not just the character: a chosen lower-prominence cue applies directly while the
    /// subject's more prominent cue stays unassigned (multi-cue playtest work).
    /// </summary>
    [Fact]
    public void Evaluate_AppliesTheChosenCueNotTheMostProminentOne()
    {
        ScriptedDecisionClient client = new(answerValue: "char:target#body");
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue faceCue = new()
        {
            ID = "face",
            Prominence = 3f
        };
        TestVisualCue bodyCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject subject = new("target", [faceCue, bodyCue]);
        scene.Add(subject);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        try
        {
            mind.ReinforceAttention(subject.FullId, 1f);
            selector._Ready();
            Tick(selector, 0d);

            Assert.Equal([bodyCue], vision.SetTargets);

            DecisionRequest request = Assert.Single(client.Requests);
            ChoiceQuestion question = Assert.IsType<ChoiceQuestion>(Assert.Single(request.Questions), exactMatch: false);
            Assert.Equal(
                ["char:target#face", "char:target#body", "none"],
                question.Options.Select(option => option.Value));
            Assert.Equal(
                "`char:target#face` — face: face (attention 1, prominence 3.00)",
                question.Options[0].Criteria);
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            faceCue.Free();
            bodyCue.Free();
        }
    }

    /// <summary>
    /// Evidence carries the scene-character roster: lore descriptions through the shared cached resolver, honest
    /// bare identities when absent, no candidate duplication of the options, and a cue-level current gaze.
    /// </summary>
    [Fact]
    public void Evaluate_EvidenceCarriesTheSceneCharacterRosterAndOmitsLoreHonestly()
    {
        // The resolver caches per (observer, subject) process-wide; earlier selector facts in the same session
        // resolve these subjects through the real service, so clear the cache for scripted isolation.
        CharacterLoreDescriptions.ClearCacheForTesting();
        ScriptedDecisionClient client = new(answerValue: DecisionGazePolicy.NoneOptionValue);
        TestVision vision = new();
        TestCharacter character = new(hasComponentProjection: true, vision);
        TestCharacter describedFriend = new(hasComponentProjection: false)
        {
            Id = "described"
        };
        TestCharacter bareFriend = new(hasComponentProjection: false)
        {
            Id = "bare"
        };
        TestMind mind = new(character);
        MutableSceneContext scene = new();
        TestVisualCue alphaCue = new()
        {
            ID = "body",
            Prominence = 1f
        };
        TestVisualSubject alpha = new("alpha", [alphaCue]);
        scene.Add(alpha);
        scene.Add(describedFriend);
        scene.Add(bareFriend);
        AttentionGazeTargetSelector selector = CreateSelector(mind, scene, client);
        mind.GetSceneDecisions().LoreLoader = () => new ScriptedLoreQueryService(
            descriptions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["char:described"] = "A sturdily built man in his late twenties.",
            });
        try
        {
            mind.ReinforceAttention(alpha.FullId, 0.9f);
            selector._Ready();
            Tick(selector, 0d);

            JsonElement evidence = Assert.IsType<JsonElement>(Assert.Single(client.Requests).Evidence);
            _ = Assert.Throws<KeyNotFoundException>(() => evidence.GetProperty("candidates"));

            JsonElement characters = evidence.GetProperty("characters");
            Assert.Equal(2, characters.GetArrayLength());
            // Roster order is full-ID ordinal: bare precedes described.
            Assert.Equal("char:bare", characters[0].GetProperty("id").GetString());
            _ = Assert.Throws<KeyNotFoundException>(() => characters[0].GetProperty("description"));
            Assert.Equal(
                "A sturdily built man in his late twenties.",
                characters[1].GetProperty("description").GetString());
            Assert.Equal(
                DecisionGazePolicy.NoneOptionValue,
                evidence.GetProperty("current_gaze").GetProperty("cue").GetString());
        }
        finally
        {
            selector._ExitTree();
            selector.Free();
            mind.Free();
            alphaCue.Free();
        }
    }

    /// <summary>Invalid cadence authoring is rejected before the selector can bind runtime dependencies.</summary>
    [Fact]
    public void Authoring_RejectsInvalidCadenceBeforeActivation()
    {
        AssertInvalidAuthoring(selector => selector.EvaluationIntervalSeconds = float.NaN);
        AssertInvalidAuthoring(selector => selector.EvaluationIntervalSeconds = 0f);
        AssertInvalidAuthoring(selector => selector.EvaluationIntervalSeconds = -1f);
    }

    private static void Tick(AttentionGazeTargetSelector selector, double delta)
    {
        selector._Process(delta);
        ((MindBase)selector.GetParent()).PumpSceneDecisions();
    }

    private static AttentionGazeTargetSelector CreateSelector(
        MindBase mind,
        ISceneContext scene,
        IDecisionClient client)
    {
        AttentionGazeTargetSelector selector = new()
        {
            Name = "AttentionGazeTargetSelector",
            EvaluationIntervalSeconds = 0.5f,
        };
        mind.SetSceneContextLoaderForTesting(() => scene);
        mind.SetSceneDecisionClientForTesting(client);
        mind.AddChild(selector);
        return selector;
    }

    private static void AssertInvalidAuthoring(Action<AttentionGazeTargetSelector> configure)
    {
        TestMind mind = new(new TestCharacter(hasComponentProjection: false));
        AttentionGazeTargetSelector selector = new()
        {
            EvaluationIntervalSeconds = 0.5f,
        };
        mind.AddChild(selector);
        configure(selector);
        try
        {
            _ = Assert.Throws<InvalidOperationException>(selector._Ready);
        }
        finally
        {
            selector.Free();
            mind.Free();
        }
    }

    private sealed partial class TestMind(ICharacter owner) : MindBase
    {
        internal AIDiagnosticsSettings? Diagnostics
        {
            get; init;
        }
        internal override AIDiagnosticsSettings WaitDiagnosticsSettings => Diagnostics ?? base.WaitDiagnosticsSettings;

        protected override ICharacter ResolveOwningCharacter() => owner;

        public void ReinforceAttention(string fullId, float contribution)
            => ReinforceAttention(
                fullId,
                contribution,
                AttentionSettings.Create(maximum: 1f, decayPerSecond: 0f, retentionThreshold: 0f, contextThreshold: 0f));
    }

    /// <summary>Detached AgenticMind parent binding a scripted scenario manager for the premise wiring path.</summary>
    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        protected override ICharacter ResolveOwningCharacter() => owner;

        public void ReinforceAttention(string fullId, float contribution)
            => ReinforceAttention(
                fullId,
                contribution,
                AttentionSettings.Create(maximum: 1f, decayPerSecond: 0f, retentionThreshold: 0f, contextThreshold: 0f));
    }

    /// <summary>Scenario manager standing in for the authored one with a fixed description, or none.</summary>
    private sealed partial class FixedDescriptionScenarioManager : ScenarioManager
    {
        public string? Description
        {
            get; set;
        }

        public int Calls
        {
            get; private set;
        }

        public override ValueTask<Scenario?> GetCurrentScenario(IReadOnlyDictionary<string, object?> coreContext)
        {
            Calls++;
            return ValueTask.FromResult(
                string.IsNullOrWhiteSpace(Description) ? null : new Scenario(Description));
        }
    }

    private sealed class TestCharacter(bool hasComponentProjection, params IComponent[] components) : ICharacter, IComponentProjectionNotifier
    {
        private IComponent[] _components = components;
        private Action? _componentsRefreshed;

        public string Id { get; set; } = "owner";

        public IReadOnlyList<IComponent> Components => _components;

        public bool HasComponentProjection { get; private set; } = hasComponentProjection;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;

        public int ComponentsRefreshedHandlerCount
        {
            get; private set;
        }

        public event Action? ComponentsRefreshed
        {
            add
            {
                _componentsRefreshed += value;
                ComponentsRefreshedHandlerCount++;
            }
            remove
            {
                _componentsRefreshed -= value;
                ComponentsRefreshedHandlerCount--;
            }
        }

        public void RefreshComponents(params IComponent[] components)
        {
            _components = components;
            HasComponentProjection = true;
            _componentsRefreshed?.Invoke();
        }
    }

    private sealed class MutableSceneContext : ISceneContext
    {
        public ICharacter Player => PlayerOverride ?? throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public ICharacter? PlayerOverride
        {
            get; set;
        }

        private readonly Dictionary<string, IIdentifiable> _entries = new(StringComparer.Ordinal);

        public IReadOnlyCollection<ICharacter> Characters
            => [.. _entries.Values.OfType<ICharacter>()];

        public ContentContext Content => ContentContext.Default;

        public List<string> FindRequests { get; } = [];

        public void Add(IIdentifiable identifiable) => _entries.Add(identifiable.FullId, identifiable);

        public void Remove(string fullId) => _ = _entries.Remove(fullId);

        public IIdentifiable? Find(string fullId)
        {
            FindRequests.Add(fullId);
            return _entries.GetValueOrDefault(fullId);
        }

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"No test scene entry exists for '{fullId}'.");
    }

    private sealed class TestVisualSubject(string id, IReadOnlyList<VisualCue> visualCues) : IVisualSubject
    {
        public string Id { get; set; } = id;

        public string Type => "char";

        public string FullId => $"{Type}:{Id}";

        public IReadOnlyList<VisualCue> VisualCues { get; } = visualCues;

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    private sealed class NonVisualIdentifiable(string id) : IIdentifiable
    {
        public string Id { get; set; } = id;

        public string Type => "char";

        public string FullId => $"{Type}:{Id}";
    }

    /// <summary>Plain controllable voice: speaking-window state and events are driven explicitly by the test.</summary>
    private sealed class FakeVoice(string id) : IVoice
    {
        public string Id { get; set; } = id;

        public string Type => "voice";

        public string FullId => $"voice:{Id}";

        public bool IsSpeaking
        {
            get; private set;
        }

        public event Action<IVoice>? SpeechStarted;

        public event Action<IVoice>? SpeechEnded;

        /// <summary>Raised for the attended-gated textless segment-onset notification the Mind forwards.</summary>
        public event Action<IVoice, SpeechSegmentMetadata>? SpeechSegmentStarted;

        public Vector3 Origin => Vector3.Zero;

        public void BeginSpeech()
        {
            IsSpeaking = true;
            SpeechStarted?.Invoke(this);
        }

        public void EndSpeech()
        {
            IsSpeaking = false;
            SpeechEnded?.Invoke(this);
        }

        /// <summary>Raises one attended-segment onset without opening the speaking window.</summary>
        public void BeginSegment()
            => SpeechSegmentStarted?.Invoke(this, new SpeechSegmentMetadata($"segment-{Guid.NewGuid():N}", 0));

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    /// <summary>Projection-notifying owner character composed with its voice and vision.</summary>
    private sealed class VoicedProjectionCharacter(IVoice voice, IVision vision) : ICharacter, IComponentProjectionNotifier
    {
        public string Id { get; set; } = "owner";

        public IReadOnlyList<IComponent> Components { get; } = [voice, vision];

        public bool HasComponentProjection => true;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;

        public event Action? ComponentsRefreshed
        {
            add
            {
            }
            remove
            {
            }
        }
    }

    private sealed class VoicedCharacter(string id, IVoice voice) : ICharacter
    {
        public string Id { get; set; } = id;

        public string Type => "char";

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [voice];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    private sealed partial class TestVisualCue : VisualCue
    {
        public override ValueTask<string> Describe(ISceneContext scene, IHasVision observer)
            => ValueTask.FromResult(string.Empty);
    }

    private sealed class TestVision : IVision, IDisposable
    {
        public VisualCue? LookTarget
        {
            get; set;
        }

        public Camera3D? Camera => null;

        public ValueTask<Image> CaptureImage()
            => throw new InvalidOperationException("The test vision component does not support image capture.");

        public event Action<IPercept>? Perceived
        {
            add
            {
            }
            remove
            {
            }
        }

        public IReadOnlyList<Type> PerceptTypes { get; } = [];

        public List<VisualCue> SetTargets { get; } = [];

        public int ClearCount
        {
            get; private set;
        }

        public void SetLookTarget(VisualCue? target)
        {
            LookTarget = target;
            SetTargets.Add(Assert.IsAssignableFrom<VisualCue>(target));
        }

        public void ClearLookTarget()
        {
            LookTarget = null;
            ClearCount++;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Immediately-answering scripted client whose answer can be reconfigured between evaluations.</summary>
    private sealed class ScriptedDecisionClient(string? answerValue = null) : IDecisionClient
    {
        public List<DecisionRequest> Requests { get; } = [];

        public string AnswerValue
        {
            get; set;
        } = answerValue ?? DecisionGazePolicy.NoneOptionValue;

        public DecisionFailure? DecisionFailure
        {
            get; set;
        }

        public bool Refuse
        {
            get; set;
        }

        public Task<DecisionResult> EvaluateAsync(
            DecisionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (DecisionFailure is { } failure)
            {
                throw new DecisionException(failure, "Scripted failure.", HttpStatusCode.ServiceUnavailable);
            }

            ChoiceQuestion question = request.Questions.OfType<ChoiceQuestion>().Single();
            DecisionAnswer answer = Refuse
                ? new DecisionRefusal(question, JsonDocument.Parse("{}").RootElement)
                : new ChoiceAnswer(question, AnswerValue, null, null);
            return Task.FromResult(new DecisionResult(
                "scripted",
                ID: null,
                Provider: null,
                Usage: null,
                Cost: null,
                [answer],
                new Dictionary<string, DecisionAnswer> { [question.Name] = answer }));
        }
    }

    /// <summary>Scripted client holding each evaluation open until the test completes it.</summary>
    private sealed class GatedDecisionClient : IDecisionClient
    {
        private TaskCompletionSource<string> _gate = new();

        public List<DecisionRequest> Requests { get; } = [];

        public async Task<DecisionResult> EvaluateAsync(
            DecisionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            ChoiceQuestion question = request.Questions.OfType<ChoiceQuestion>().Single();
            // The gate uses synchronous completions so CompleteInFlight settles the whole evaluation chain
            // inline and the next selector process call applies it deterministically.
            string answerValue = await _gate.Task.ConfigureAwait(false);
            return CreateResult(question, answerValue);
        }

        /// <summary>Completes the in-flight evaluation with the chosen option value.</summary>
        public void CompleteInFlight(string answerValue)
        {
            _gate.SetResult(answerValue);
            _gate = new TaskCompletionSource<string>();
        }

        private static DecisionResult CreateResult(ChoiceQuestion question, string answerValue)
        {
            ChoiceAnswer answer = new(question, answerValue, null, null);
            return new DecisionResult(
                "scripted-gated",
                ID: null,
                Provider: null,
                Usage: null,
                Cost: null,
                [answer],
                new Dictionary<string, DecisionAnswer> { [question.Name] = answer });
        }
    }
}
