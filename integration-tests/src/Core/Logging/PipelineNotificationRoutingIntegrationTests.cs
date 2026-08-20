using AlleyCat.Core.Logging;
using AlleyCat.IntegrationTests.Support;
using Godot;
using Microsoft.Extensions.Logging;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Core.Logging;

/// <summary>
/// End-to-end integration coverage for routing pipeline diagnostics and error logs into the notification UI
/// through the real AlleyCat logging infrastructure.
/// </summary>
public sealed class PipelineNotificationRoutingIntegrationTests
{
    private const int MaxWaitFrames = 30;

    /// <summary>
    /// With the pipeline category opted into trace logging, latency and marker entries display their notification
    /// text while log-only diagnostics stay filtered from the notification UI.
    /// </summary>
    [Fact]
    public async Task PipelineNotifications_WithTraceOptIn_DisplayLatencyAndMarkerTexts()
    {
        SceneTree sceneTree = GetSceneTree();
        NotificationLoggingFixture fixture = await NotificationLoggingFixture.CreateAsync(sceneTree);

        try
        {
            PipelineDebugLog.Latency("TTS audio generated in", TimeSpan.FromMilliseconds(1400), "44100 bytes");
            PipelineDebugLog.Marker("Speak tool invoked", "12 chars");
            PipelineDebugLog.Stage("STT recording started");
            PipelineDebugLog.LogOnlyLatency("TTS failed after", TimeSpan.FromMilliseconds(12.34));

            await WaitUntilAsync(
                sceneTree,
                () => fixture.GetNotificationTexts().Count >= 2,
                MaxWaitFrames);

            // The widget queues notifications newest-first.
            Assert.Equal(
                ["Speak tool invoked (12 chars)", "TTS audio generated in 1.4 seconds (44100 bytes)"],
                fixture.GetNotificationTexts());
        }
        finally
        {
            await fixture.DestroyAsync(sceneTree);
        }
    }

    /// <summary>
    /// Entry-carrying trace logs posted from a worker thread still reach the overlay because the notification
    /// sink marshals its post through the production main-thread dispatcher.
    /// </summary>
    [Fact]
    public async Task PipelineNotifications_FromWorkerThread_ArriveThroughTheMainThreadDispatcher()
    {
        SceneTree sceneTree = GetSceneTree();
        NotificationLoggingFixture fixture = await NotificationLoggingFixture.CreateAsync(sceneTree);

        try
        {
            Task workerLog = Task.Factory.StartNew(
                () => PipelineDebugLog.Latency(
                    "STT transcription completed in",
                    TimeSpan.FromSeconds(2.5),
                    "cross-thread"),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
            await workerLog;

            await WaitUntilAsync(
                sceneTree,
                () => fixture.GetNotificationTexts().Contains("STT transcription completed in 2.5 seconds (cross-thread)"),
                MaxWaitFrames);

            _ = Assert.Single(fixture.GetNotificationTexts());
        }
        finally
        {
            await fixture.DestroyAsync(sceneTree);
        }
    }

    /// <summary>
    /// The pipeline category's configured level is the single universal switch: without a trace override the
    /// default information floor keeps latency toasts off, while a trace override — the same user configuration
    /// override a player would write — makes them appear.
    /// </summary>
    [Fact]
    public async Task PipelineNotifications_TraceOverrideInConfiguration_AloneDeterminesToastVisibility()
    {
        SceneTree sceneTree = GetSceneTree();

        NotificationLoggingFixture silentFixture =
            await NotificationLoggingFixture.CreateAsync(sceneTree, enablePipelineTraceLogging: false);
        try
        {
            PipelineDebugLog.Latency("TTS audio generated in", TimeSpan.FromMilliseconds(1400), "44100 bytes");
            await WaitForFramesAsync(sceneTree, 5);

            Assert.Empty(silentFixture.GetNotificationTexts());
        }
        finally
        {
            await silentFixture.DestroyAsync(sceneTree);
        }

        NotificationLoggingFixture optedInFixture = await NotificationLoggingFixture.CreateAsync(sceneTree);
        try
        {
            PipelineDebugLog.Latency("TTS audio generated in", TimeSpan.FromMilliseconds(1400), "44100 bytes");

            await WaitUntilAsync(
                sceneTree,
                () => optedInFixture.GetNotificationTexts().Count >= 1,
                MaxWaitFrames);

            Assert.Equal(
                "TTS audio generated in 1.4 seconds (44100 bytes)",
                Assert.Single(optedInFixture.GetNotificationTexts()));
        }
        finally
        {
            await optedInFixture.DestroyAsync(sceneTree);
        }
    }

    /// <summary>
    /// Error-level logs through the real provider still post their formatted text while the pipeline category
    /// stays at its default level, because the error floor is independent of pipeline diagnostics.
    /// </summary>
    [Fact]
    public async Task ErrorLog_ThroughRealProvider_PostsFormattedTextWithoutTraceOptIn()
    {
        SceneTree sceneTree = GetSceneTree();
        NotificationLoggingFixture fixture =
            await NotificationLoggingFixture.CreateAsync(sceneTree, enablePipelineTraceLogging: false);

        try
        {
            ILogger logger = fixture.CreateLogger("AlleyCat.Speech.Transcription.Transcriber");

            logger.LogError("Voice transcription failed while processing recorded microphone audio.");

            await WaitUntilAsync(sceneTree, () => fixture.GetNotificationTexts().Count >= 1, MaxWaitFrames);

            Assert.Equal(
                "[Error] AlleyCat.Speech.Transcription.Transcriber: "
                    + "Voice transcription failed while processing recorded microphone audio.",
                Assert.Single(fixture.GetNotificationTexts()));
        }
        finally
        {
            await fixture.DestroyAsync(sceneTree);
        }
    }

    /// <summary>
    /// After the eligibility refinement exactly four stage kinds post toasts — STT backend return, TTS audio
    /// generation, TTS lip-sync preparation, and the speak-tool marker — while the excluded stages keep full
    /// console coverage without ever reaching the notification UI. Verbose details are shortened for toasts only:
    /// the STT toast omits the model suffix, the lip-sync toast keeps only the frame count, and the console lines
    /// stay byte-identical.
    /// </summary>
    [Fact]
    public async Task PipelineNotifications_KeptStaysToastEligibleWhileExcludedStagesStayLogOnly()
    {
        SceneTree sceneTree = GetSceneTree();
        NotificationLoggingFixture fixture = await NotificationLoggingFixture.CreateAsync(sceneTree);

        try
        {
            PipelineDebugLog.Latency(
                "STT backend returned in",
                TimeSpan.FromMilliseconds(1200),
                "model whisper-1",
                notificationDetail: string.Empty);
            PipelineDebugLog.Latency("TTS audio generated in", TimeSpan.FromMilliseconds(1400), "44100 bytes");
            PipelineDebugLog.Latency(
                "TTS lip-sync prepared in",
                TimeSpan.FromMilliseconds(2100),
                "404 frames, 2 mesh(es)",
                notificationDetail: "404 frames");
            PipelineDebugLog.Marker("Speak tool invoked", "142 chars");

            PipelineDebugLog.LogOnlyLatency("STT completed in", TimeSpan.FromMilliseconds(1200), "42 chars");
            PipelineDebugLog.LogOnlyLatency("TTS backend returned in", TimeSpan.FromMilliseconds(900), "model tts-1");
            PipelineDebugLog.LogOnlyLatency("TTS audio parsed in", TimeSpan.FromMilliseconds(12), "44100 PCM bytes");
            PipelineDebugLog.LogOnlyLatency("TTS playback started after", TimeSpan.FromMilliseconds(2056));

            await WaitUntilAsync(
                sceneTree,
                () => fixture.GetNotificationTexts().Count >= 4,
                MaxWaitFrames);

            // The widget queues notifications newest-first, mirroring the emission order above. The STT toast drops
            // the model suffix and the lip-sync toast keeps only the frame count.
            Assert.Equal(
                [
                    "Speak tool invoked (142 chars)",
                    "TTS lip-sync prepared in 2.1 seconds (404 frames)",
                    "TTS audio generated in 1.4 seconds (44100 bytes)",
                    "STT backend returned in 1.2 seconds",
                ],
                fixture.GetNotificationTexts());

            IReadOnlyList<string> excludedStages =
            [
                "STT completed in",
                "TTS backend returned in",
                "TTS audio parsed in",
                "TTS playback started after",
            ];
            foreach (string excludedStage in excludedStages)
            {
                Assert.DoesNotContain(
                    fixture.GetNotificationTexts(),
                    text => text.StartsWith(excludedStage, StringComparison.Ordinal));
            }

            // Console coverage is unchanged: both kept and excluded stages log their full messages.
            IReadOnlyList<string> pipelineMessages = fixture.GetPipelineLogMessages();
            Assert.Contains(pipelineMessages, message => message.Contains(
                "AI pipeline latency STT completed in 1200 ms (42 chars)", StringComparison.Ordinal));
            Assert.Contains(pipelineMessages, message => message.Contains(
                "AI pipeline latency TTS backend returned in 900 ms (model tts-1)", StringComparison.Ordinal));
            Assert.Contains(pipelineMessages, message => message.Contains(
                "AI pipeline latency TTS audio parsed in 12 ms (44100 PCM bytes)", StringComparison.Ordinal));
            Assert.Contains(pipelineMessages, message => message.Contains(
                "AI pipeline latency TTS playback started after 2056 ms", StringComparison.Ordinal));
            Assert.Contains(pipelineMessages, message => message.Contains(
                "AI pipeline latency STT backend returned in 1200 ms (model whisper-1)", StringComparison.Ordinal));
            Assert.Contains(pipelineMessages, message => message.Contains(
                "AI pipeline latency TTS audio generated in 1400 ms (44100 bytes)", StringComparison.Ordinal));
            Assert.Contains(pipelineMessages, message => message.Contains(
                "AI pipeline latency TTS lip-sync prepared in 2100 ms (404 frames, 2 mesh(es))", StringComparison.Ordinal));
            Assert.Contains(pipelineMessages, message => message.Contains(
                "AI pipeline stage Speak tool invoked (142 chars)", StringComparison.Ordinal));
        }
        finally
        {
            await fixture.DestroyAsync(sceneTree);
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
}
