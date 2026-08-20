using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Threading;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Attention;
using AlleyCat.Scene;
using AlleyCat.Speech.Voice;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Integration coverage for the speak-tool invocation marker: the marker must become observable while the
/// turn-taking wait is still blocked, so the inferred reasoning gap stays untainted by speaker waits.
/// </summary>
public sealed class SpeechToolMarkerIntegrationTests
{
    private const int MaxWaitFrames = 30;

    /// <summary>
    /// The speak-tool invocation marker reaches the notification UI while the attended-speaker wait is still
    /// blocked, and the tool only completes after the attended speaker finishes.
    /// </summary>
    [Fact]
    public async Task SpeechTool_MarkerBecomesObservableWhileAttendedSpeakerWaitStillBlocks()
    {
        SceneTree sceneTree = GetSceneTree();
        NotificationLoggingFixture loggingFixture = await NotificationLoggingFixture.CreateAsync(sceneTree);

        try
        {
            FakeVoice ownerVoice = new("speech_tool_owner_voice");
            FakeVoice speakerVoice = new("attended_speaker_voice");
            Task<object?> speakTask = Task.FromException<object?>(new InvalidOperationException("Not invoked."));
            VoiceCharacter owner = new("speech_tool_owner", ownerVoice);
            VoiceCharacter speaker = new("attended_speaker", speakerVoice);
            ToolSpeechMind mind = new(owner)
            {
                AttentionDecayPerSecond = 0f,
            };
            mind.SetSceneContextLoaderForTesting(() => new ToolSpeechSceneContext([owner, speaker]));
            mind.AttendForTest(speaker.FullId);
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
            await WaitForFramesAsync(sceneTree, 2);

            try
            {
                // The attended speaker's open window parks the turn-taking wait on its unsignalled pulse, so the
                // tool cannot proceed past the wait until EndSpeech runs.
                speakerVoice.BeginSpeech();

                IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
                SpeechTool tool = new();
                AIFunction speak = tool.CreateFunction(
                    new ScenarioContext(owner, new ToolSpeechSceneContext([owner, speaker])),
                    mind,
                    dispatcher);

                string speech = "  Marker must land before the turn-taking wait.  ";
                speakTask = speak.InvokeAsync(
                    new AIFunctionArguments { ["speech"] = speech },
                    CancellationToken.None).AsTask();

                string expectedMarkerText = $"Speak tool invoked ({speech.Trim().Length} chars)";
                await WaitUntilAsync(
                    sceneTree,
                    () => loggingFixture.GetNotificationTexts().Contains(expectedMarkerText),
                    MaxWaitFrames);

                // The toast is already displayed while the tool is still blocked in the attended-speaker wait.
                Assert.False(speakTask.IsCompleted, "The speak tool must still be blocked in the turn-taking wait.");

                speakerVoice.EndSpeech();

                object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal("Spoken through the configured voice.", result);
            }
            finally
            {
                speakerVoice.EndSpeech();
                await ObserveSpeakOutcomeAsync(speakTask);
                mind.QueueFree();
                await WaitForFramesAsync(sceneTree, 2);
            }
        }
        finally
        {
            await loggingFixture.DestroyAsync(sceneTree);
        }
    }

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames)
    {
        for (int frame = 0; frame < maxFrames; frame++)
        {
            if (predicate())
            {
                return;
            }

            await WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }

    private static async Task ObserveSpeakOutcomeAsync(Task<object?> speakTask)
    {
        try
        {
            _ = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Teardown observes an orphaned tool invocation without letting it fail the test.
        }
    }

    private sealed partial class ToolSpeechMind(VoiceCharacter owner) : MindBase
    {
        public void AttendForTest(string fullId)
            => ReinforceAttention(fullId, 1f, AttentionSettings.Create(1f, 0f, 0.05f, 0.25f));

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>
    /// Plain controllable voice: speaking-window state and events are driven explicitly by the test.
    /// </summary>
    private sealed class FakeVoice(string id) : IVoice
    {
        public string Id { get; set; } = id;

        public string Type => "voice";

        public string FullId => $"voice:{Id}";

        public bool IsSpeaking
        {
            get;
            private set;
        }

        public event Action<IVoice>? SpeechStarted;

        public event Action<IVoice>? SpeechEnded;

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

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class VoiceCharacter(string id, IVoice voice) : ICharacter
    {
        public string Id { get; set; } = id;

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [voice];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }

    private sealed record ToolSpeechSceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public ICharacter Player => throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
            => Characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException();
    }
}
