using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AlleyCat.Speech.LipSync;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using HttpClient = System.Net.Http.HttpClient;
namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// Runtime coverage for Audio2Face streaming inference playback policies against a local scripted NDJSON
/// stub server: first-frame playback gate, starvation hold, stop-driven stream cancellation, truncated
/// streams, and diffusion mode falling back to the batch endpoint.
/// </summary>
public sealed partial class A2FStreamingLipSyncIntegrationTests
{
    private const float StreamFps = 30f;

    /// <summary>
    /// Policy 1: preparation must complete (so playback can start audibly) once metadata and the startup
    /// buffer worth of frames have arrived, well before the server sends its complete record.
    /// </summary>
    [Fact]
    [Headless]
    public async Task A2FStreaming_Preparation_CompletesBeforeServerSendsCompleteRecord()
    {
        const int frameCount = 90;
        ScriptedA2FServer server = new(async session =>
        {
            await session.SendMetadataAsync(StreamFps, ["jawOpen"]);
            for (int frameIndex = 0; frameIndex < 6; frameIndex++)
            {
                await session.SendFrameAsync(frameIndex, frameIndex / 90f);
            }

            for (int frameIndex = 6; frameIndex < frameCount; frameIndex++)
            {
                await Task.Delay(10);
                await session.SendFrameAsync(frameIndex, frameIndex / 90f);
            }

            session.MarkEvent("frames-sent");
            await Task.Delay(1500);
            await session.SendCompleteAsync(frameCount);
            session.MarkEvent("complete-sent");
        });

        await using StreamingPlaybackFixture fixture = await StreamingPlaybackFixture.CreateAsync(
            GetSceneTree(),
            server.BlendshapesUrl);

        try
        {
            Assert.True(fixture.Player.IsInitialised, fixture.Player.InitialisationError);

            Task<LipSyncPlayer.PreparedPlayback> preparation = fixture.Player.PreparePlaybackAsync(
                CreateSilenceStream(seconds: 3.0));

            Task finishedFirst = await Task.WhenAny(preparation, Task.Delay(4000));
            Assert.Same(preparation, finishedFirst);

            // The complete record is still seconds away, proving playback was allowed to start early.
            Assert.False(server.HasEvent("complete-sent"), "Preparation must complete before the server sends complete.");

            LipSyncPlayer.PreparedPlayback prepared = await preparation;
            Assert.NotNull(prepared.StreamingSession);
            Assert.True(prepared.PreparedFrameCount >= 3, "The startup buffer must have filled before preparation completed.");

            int completedCount = 0;
            fixture.Player.PlaybackCompleted += () => completedCount++;

            fixture.Player.PlayPrepared(prepared);
            Assert.True(fixture.Player.IsAudioPlaying);
            Assert.True(string.IsNullOrWhiteSpace(fixture.Player.PlaybackError), fixture.Player.PlaybackError);

            await WaitUntilAsync(GetSceneTree(), () => completedCount == 1, maxFrames: 3000);

            Assert.True(string.IsNullOrWhiteSpace(fixture.Player.PlaybackError), fixture.Player.PlaybackError);
            Assert.True(server.HasEvent("complete-sent"));
            LipSyncPlayer.StreamingPlaybackSession session = prepared.StreamingSession;
            Assert.True(session.Buffer.IsCompleted, "The background download must finish after playback started.");
            Assert.Equal(frameCount, session.Buffer.FrameCount);
            Assert.Equal(frameCount, session.Buffer.DeclaredFrameCount);
            _ = Assert.Single(server.Requests);
        }
        finally
        {
            await fixture.DisposeAsync();
            server.Dispose();
        }
    }

    /// <summary>
    /// Policy 2: when the playback cursor outruns frame arrival, playback holds the last applied frame,
    /// emits a rate-limited warning, and resumes applying frames once the download catches up.
    /// </summary>
    [Fact]
    [Headless]
    public async Task A2FStreaming_WhenFramesArriveSlowerThanPlayback_HoldsLastFrameAndWarns()
    {
        const int frameCount = 90;
        ScriptedA2FServer server = new(async session =>
        {
            await session.SendMetadataAsync(StreamFps, ["jawOpen"]);
            for (int frameIndex = 0; frameIndex < 5; frameIndex++)
            {
                await session.SendFrameAsync(frameIndex, frameIndex / 90f);
            }

            // Starvation window: the audio clock consumes frames while the download stalls.
            await Task.Delay(1300);

            for (int frameIndex = 5; frameIndex < frameCount; frameIndex++)
            {
                await session.SendFrameAsync(frameIndex, frameIndex / 90f);
            }

            await session.SendCompleteAsync(frameCount);
        });

        await using StreamingPlaybackFixture fixture = await StreamingPlaybackFixture.CreateAsync(
            GetSceneTree(),
            server.BlendshapesUrl);

        try
        {
            Task<LipSyncPlayer.PreparedPlayback> preparation = fixture.Player.PreparePlaybackAsync(
                CreateSilenceStream(seconds: 3.0));
            Task finishedFirst = await Task.WhenAny(preparation, Task.Delay(4000));
            Assert.Same(preparation, finishedFirst);
            fixture.Player.PlayPrepared(await preparation);

            SceneTree sceneTree = GetSceneTree();
            await WaitUntilAsync(
                sceneTree,
                () => fixture.Player.StreamingStarvationEpisodeCount >= 1,
                maxFrames: 3000);
            Assert.True(fixture.Player.StreamingStarvationWarningCount >= 1, "Starvation must emit a warning.");
            Assert.True(fixture.Player.AppliedFrameCount > 0);

            // While starved, the last applied frame is held: neither the applied count nor the mesh
            // blendshape value may change while frames are unavailable.
            int appliedCountAtHold = fixture.Player.AppliedFrameCount;
            float meshValueAtHold = fixture.Mesh.GetBlendShapeValue(0);
            Assert.InRange(meshValueAtHold, 0f, 5f / 90f);

            await WaitForFramesAsync(sceneTree, 15);
            Assert.Equal(appliedCountAtHold, fixture.Player.AppliedFrameCount);
            Assert.Equal(meshValueAtHold, fixture.Mesh.GetBlendShapeValue(0));

            // Once the burst of frames arrives, playback resumes applying them and completes cleanly.
            await WaitUntilAsync(
                sceneTree,
                () => fixture.Player.AppliedFrameCount > appliedCountAtHold,
                maxFrames: 3000);

            int completedCount = 0;
            fixture.Player.PlaybackCompleted += () => completedCount++;
            await WaitUntilAsync(sceneTree, () => completedCount == 1, maxFrames: 3000);

            Assert.True(string.IsNullOrWhiteSpace(fixture.Player.PlaybackError), fixture.Player.PlaybackError);
            LipSyncPlayer.StreamingPlaybackSession session = (await preparation).StreamingSession!;
            Assert.True(session.Buffer.IsCompleted);
            Assert.Equal(frameCount, session.Buffer.FrameCount);
            Assert.Equal(1, fixture.Player.StreamingStarvationEpisodeCount);
        }
        finally
        {
            await fixture.DisposeAsync();
            server.Dispose();
        }
    }

    /// <summary>
    /// Policy 3: stopping mid-stream cancels the in-flight download — the background read loop settles,
    /// the server observes the client connection abort, and a fresh session can be replayed afterwards.
    /// </summary>
    [Fact]
    [Headless]
    public async Task A2FStreaming_StopDuringActiveStream_CancelsDownloadAndAllowsReplay()
    {
        LipSyncPlayer.StreamingPlaybackSession? firstSession = null;
        int secondRequestAdmittedBeforeFirstReadLoopSettled = 0;
        ScriptedA2FServer server = new(async session =>
        {
            await session.SendMetadataAsync(StreamFps, ["jawOpen"]);

            if (session.RequestIndex == 1)
            {
                int frameIndex = 0;
                while (true)
                {
                    // Endless frame trickle: only a client abort can end this loop.
                    await session.SendFrameAsync(frameIndex++, 0.5f);
                    await Task.Delay(40);
                }
            }

            if (firstSession is { } observedFirstSession && !observedFirstSession.ReadLoop.IsCompleted)
            {
                _ = Interlocked.Exchange(ref secondRequestAdmittedBeforeFirstReadLoopSettled, 1);
            }

            for (int frameIndex = 0; frameIndex < 45; frameIndex++)
            {
                await session.SendFrameAsync(frameIndex, frameIndex / 45f);
            }

            await session.SendCompleteAsync(45);
        });

        await using StreamingPlaybackFixture fixture = await StreamingPlaybackFixture.CreateAsync(
            GetSceneTree(),
            server.BlendshapesUrl);

        try
        {
            SceneTree sceneTree = GetSceneTree();

            Task<LipSyncPlayer.PreparedPlayback> firstPreparation = fixture.Player.PreparePlaybackAsync(
                CreateSilenceStream(seconds: 5.0));
            Task finishedFirst = await Task.WhenAny(firstPreparation, Task.Delay(4000));
            Assert.Same(firstPreparation, finishedFirst);
            LipSyncPlayer.PreparedPlayback firstPlayback = await firstPreparation;
            firstSession = firstPlayback.StreamingSession!;

            int completedCount = 0;
            fixture.Player.PlaybackCompleted += () => completedCount++;

            fixture.Player.PlayPrepared(firstPlayback);
            await WaitUntilAsync(sceneTree, () => fixture.Player.AppliedFrameCount > 0, maxFrames: 3000);

            fixture.Player.Stop();

            Assert.False(fixture.Player.IsAudioPlaying);
            Assert.False(fixture.Player.HasActiveStreamingSession);
            int appliedCountAtCut = fixture.Player.AppliedFrameCount;

            // Begin the replacement immediately. Its HTTP request may only be admitted after the cut
            // session's reader has fully settled; this is intentionally not a PlayPrepared hand-off check.
            Task<LipSyncPlayer.PreparedPlayback> secondPreparation = fixture.Player.PreparePlaybackAsync(
                CreateSilenceStream(seconds: 1.5));
            Task finishedSecond = await Task.WhenAny(secondPreparation, Task.Delay(4000));
            Assert.Same(secondPreparation, finishedSecond);

            // The background read loop settles promptly once the session is cut.
            await WaitUntilAsync(sceneTree, () => firstSession.ReadLoop.IsCompleted, maxFrames: 3000);
            Assert.False(firstSession.Buffer.IsCompleted, "A cut stream must never look complete.");
            Assert.Equal(0, completedCount);

            // The server observes the client aborting the connection mid-download.
            await WaitUntilAsync(sceneTree, () => server.ClientAborted, maxFrames: 3000);
            await WaitForFramesAsync(sceneTree, 10);
            Assert.Equal(appliedCountAtCut, fixture.Player.AppliedFrameCount);

            // A fresh streaming session replays cleanly after the cut, with no overlapping request admission.
            fixture.Player.PlayPrepared(await secondPreparation);

            await WaitUntilAsync(sceneTree, () => completedCount == 1, maxFrames: 3000);

            Assert.True(string.IsNullOrWhiteSpace(fixture.Player.PlaybackError), fixture.Player.PlaybackError);
            Assert.Equal(2, server.Requests.Count);
            Assert.Equal(0, Volatile.Read(ref secondRequestAdmittedBeforeFirstReadLoopSettled));
            Assert.True((await secondPreparation).StreamingSession!.Buffer.IsCompleted);
        }
        finally
        {
            await fixture.DisposeAsync();
            server.Dispose();
        }
    }

    /// <summary>
    /// A direct streaming <see cref="LipSyncPlayer.Play"/> replacement cuts the active playback before it
    /// waits for the predecessor reader, and its replacement request is not transport-accepted until that
    /// reader has settled.
    /// </summary>
    [Fact]
    [Headless]
    public async Task A2FStreaming_PlayReplacement_CutsBeforePredecessorSettlementAndRequestAdmission()
    {
        LipSyncPlayer.StreamingPlaybackSession? firstSession = null;
        int replacementAcceptedBeforePredecessorSettled = 0;
        ScriptedA2FServer server = new(
            async session =>
            {
                await session.SendMetadataAsync(StreamFps, ["jawOpen"]);
                if (session.RequestIndex == 1)
                {
                    for (int frameIndex = 0; frameIndex < 4; frameIndex++)
                    {
                        await session.SendFrameAsync(frameIndex, 0.5f);
                    }

                    // Force the local HTTP transport to observe the client abort before the replacement
                    // request needs a connection; small writes can remain buffered after cancellation.
                    await Task.Delay(250);
                    while (true)
                    {
                        await session.SendPaddingAsync(1024 * 1024);
                        await Task.Delay(40);
                    }
                }

                for (int frameIndex = 0; frameIndex < 45; frameIndex++)
                {
                    await session.SendFrameAsync(frameIndex, frameIndex / 45f);
                }

                await session.SendCompleteAsync(45);
            },
            requestIndex =>
            {
                if (requestIndex != 2)
                {
                    return;
                }

                if (firstSession is null || !firstSession.ReadLoop.IsCompleted)
                {
                    _ = Interlocked.Exchange(ref replacementAcceptedBeforePredecessorSettled, 1);
                }

            });

        await using StreamingPlaybackFixture fixture = await StreamingPlaybackFixture.CreateAsync(
            GetSceneTree(),
            server.BlendshapesUrl);
        try
        {
            Task<LipSyncPlayer.PreparedPlayback> firstPreparation = fixture.Player.PreparePlaybackAsync(
                CreateSilenceStream(seconds: 5.0));
            Task firstFinished = await Task.WhenAny(firstPreparation, Task.Delay(4000));
            Assert.Same(firstPreparation, firstFinished);
            LipSyncPlayer.PreparedPlayback firstPlayback = await firstPreparation;
            firstSession = firstPlayback.StreamingSession!;
            fixture.Player.PlayPrepared(firstPlayback);
            await WaitUntilAsync(GetSceneTree(), () => fixture.Player.AppliedFrameCount > 0, maxFrames: 3000);

            fixture.Player.Play(CreateSilenceStream(seconds: 1.5));

            Assert.True(firstSession.ReadLoop.IsCompleted);
            Assert.False(firstSession.Buffer.IsCompleted, "The cut predecessor stream must not complete.");
            Assert.True(server.HasEvent("request-2-transport-accepted"));
            Assert.Equal(0, Volatile.Read(ref replacementAcceptedBeforePredecessorSettled));
            Assert.True(fixture.Player.IsAudioPlaying);
            Assert.True(fixture.Player.HasActiveStreamingSession);
            Assert.True(string.IsNullOrWhiteSpace(fixture.Player.PlaybackError), fixture.Player.PlaybackError);
        }
        finally
        {
            await fixture.DisposeAsync();
            server.Dispose();
        }
    }

    /// <summary>
    /// A cancelled predecessor that does not settle by the exported replacement deadline must prevent the
    /// next streaming request from reaching the backend and expose a backend-unavailable preparation error.
    /// </summary>
    [Fact]
    [Headless]
    public async Task A2FStreaming_WhenPredecessorWillNotSettle_RefusesReplacementWithoutSendingRequest()
    {
        ScriptedA2FServer server = new(async session =>
        {
            await session.SendMetadataAsync(StreamFps, ["jawOpen"]);
            if (session.RequestIndex == 1)
            {
                int frameIndex = 0;
                while (true)
                {
                    await session.SendFrameAsync(frameIndex++, 0.5f);
                    await Task.Delay(40);
                }
            }

            for (int frameIndex = 0; frameIndex < 45; frameIndex++)
            {
                await session.SendFrameAsync(frameIndex, frameIndex / 45f);
            }

            await session.SendCompleteAsync(45);
        });

        NonSettlingA2FLipSyncPlayer player = new();
        await using StreamingPlaybackFixture fixture = await StreamingPlaybackFixture.CreateAsync(
            GetSceneTree(),
            server.BlendshapesUrl,
            configuredPlayer => configuredPlayer.StreamingReplacementSettlementTimeoutSeconds = 0.1f,
            player);

        try
        {
            Task<LipSyncPlayer.PreparedPlayback> firstPreparation = fixture.Player.PreparePlaybackAsync(
                CreateSilenceStream(seconds: 5.0));
            Task firstFinished = await Task.WhenAny(firstPreparation, Task.Delay(4000));
            Assert.Same(firstPreparation, firstFinished);
            LipSyncPlayer.PreparedPlayback firstPlayback = await firstPreparation;
            LipSyncPlayer.StreamingPlaybackSession firstSession = firstPlayback.StreamingSession!;
            fixture.Player.PlayPrepared(firstPlayback);

            await WaitUntilAsync(GetSceneTree(), () => fixture.Player.AppliedFrameCount > 0, maxFrames: 3000);
            int appliedFrameCountAtCut = fixture.Player.AppliedFrameCount;

            // The direct public replacement must cut synchronously before it waits for bounded settlement.
            fixture.Player.Play(CreateSilenceStream(seconds: 1.5));

            Assert.False(fixture.Player.IsAudioPlaying);
            Assert.False(fixture.Player.HasActiveStreamingSession);
            Assert.Equal(appliedFrameCountAtCut, fixture.Player.AppliedFrameCount);
            Assert.Equal(0f, fixture.Mesh.GetBlendShapeValue(0));
            Assert.False(string.IsNullOrWhiteSpace(fixture.Player.PlaybackError));
            Assert.Contains("Audio2Face backend unavailable", fixture.Player.PlaybackError, StringComparison.Ordinal);
            Assert.Contains("predecessor streaming session", fixture.Player.PlaybackError, StringComparison.Ordinal);
            _ = Assert.Single(server.Requests);
            _ = Assert.Single(server.TransportAcceptedRequestIndexes);
            Assert.False(firstSession.ReadLoop.IsCompleted);

            Task cancellationObserved = player.FirstCancellationObserved.Task;
            Assert.True(cancellationObserved.IsCompleted);
            player.ReleaseFirstReadLoop();
            Task firstReadLoopFinished = await Task.WhenAny(firstSession.ReadLoop, Task.Delay(4000));
            Assert.Same(firstSession.ReadLoop, firstReadLoopFinished);
            await WaitForFramesAsync(GetSceneTree(), 10);
            _ = Assert.Single(server.TransportAcceptedRequestIndexes);
        }
        finally
        {
            player.ReleaseFirstReadLoop();
            await fixture.DisposeAsync();
            server.Dispose();
        }
    }

    /// <summary>
    /// The asynchronous preparation path retains its item-level failure while deferring the visible
    /// predecessor-settlement error onto the Godot thread.
    /// </summary>
    [Fact]
    [Headless]
    public async Task A2FStreaming_WhenPredecessorSettlementExpires_PreparationFaultAlsoSetsPlaybackError()
    {
        ScriptedA2FServer server = new(async session =>
        {
            await session.SendMetadataAsync(StreamFps, ["jawOpen"]);
            if (session.RequestIndex == 1)
            {
                int frameIndex = 0;
                while (true)
                {
                    await session.SendFrameAsync(frameIndex++, 0.5f);
                    await Task.Delay(40);
                }
            }

            await session.SendCompleteAsync(0);
        });

        NonSettlingA2FLipSyncPlayer player = new();
        await using StreamingPlaybackFixture fixture = await StreamingPlaybackFixture.CreateAsync(
            GetSceneTree(),
            server.BlendshapesUrl,
            configuredPlayer => configuredPlayer.StreamingReplacementSettlementTimeoutSeconds = 0.1f,
            player);

        try
        {
            Task<LipSyncPlayer.PreparedPlayback> firstPreparation = fixture.Player.PreparePlaybackAsync(
                CreateSilenceStream(seconds: 5.0));
            Task firstFinished = await Task.WhenAny(firstPreparation, Task.Delay(4000));
            Assert.Same(firstPreparation, firstFinished);
            LipSyncPlayer.PreparedPlayback firstPlayback = await firstPreparation;
            LipSyncPlayer.StreamingPlaybackSession firstSession = firstPlayback.StreamingSession!;
            fixture.Player.PlayPrepared(firstPlayback);
            await WaitUntilAsync(GetSceneTree(), () => fixture.Player.AppliedFrameCount > 0, maxFrames: 3000);

            fixture.Player.Stop();
            Task cancellationObserved = player.FirstCancellationObserved.Task;
            Task cancellationFinished = await Task.WhenAny(cancellationObserved, Task.Delay(4000));
            Assert.Same(cancellationObserved, cancellationFinished);

            Task<LipSyncPlayer.PreparedPlayback> replacement = fixture.Player.PreparePlaybackAsync(
                CreateSilenceStream(seconds: 1.5));
            TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(() => replacement);

            Assert.Contains("Audio2Face backend unavailable", error.Message, StringComparison.Ordinal);
            await WaitForFramesAsync(GetSceneTree(), 2);
            Assert.False(string.IsNullOrWhiteSpace(fixture.Player.PlaybackError));
            Assert.Contains("Audio2Face backend unavailable", fixture.Player.PlaybackError, StringComparison.Ordinal);
            _ = Assert.Single(server.TransportAcceptedRequestIndexes);
            Assert.False(firstSession.ReadLoop.IsCompleted);

            player.ReleaseFirstReadLoop();
            Task firstReadLoopFinished = await Task.WhenAny(firstSession.ReadLoop, Task.Delay(4000));
            Assert.Same(firstSession.ReadLoop, firstReadLoopFinished);
        }
        finally
        {
            player.ReleaseFirstReadLoop();
            await fixture.DisposeAsync();
            server.Dispose();
        }
    }

    /// <summary>
    /// A stream that closes without the complete record must fail playback with a clear error instead of
    /// hanging, and still raise the playback-completed notification once.
    /// </summary>
    [Fact]
    [Headless]
    public async Task A2FStreaming_WhenStreamClosesWithoutCompleteRecord_FailsWithClearError()
    {
        ScriptedA2FServer server = new(async session =>
        {
            await session.SendMetadataAsync(StreamFps, ["jawOpen"]);
            for (int frameIndex = 0; frameIndex < 9; frameIndex++)
            {
                await session.SendFrameAsync(frameIndex, frameIndex / 9f);
            }

            // Let playback start before the response closes without a complete record.
            await Task.Delay(300);
        });

        await using StreamingPlaybackFixture fixture = await StreamingPlaybackFixture.CreateAsync(
            GetSceneTree(),
            server.BlendshapesUrl);

        try
        {
            Task<LipSyncPlayer.PreparedPlayback> preparation = fixture.Player.PreparePlaybackAsync(
                CreateSilenceStream(seconds: 2.0));
            Task finishedFirst = await Task.WhenAny(preparation, Task.Delay(4000));
            Assert.Same(preparation, finishedFirst);

            int completedCount = 0;
            fixture.Player.PlaybackCompleted += () => completedCount++;

            fixture.Player.PlayPrepared(await preparation);

            await WaitUntilAsync(GetSceneTree(), () => completedCount == 1, maxFrames: 3000);

            Assert.False(string.IsNullOrWhiteSpace(fixture.Player.PlaybackError));
            Assert.Contains("ended without a complete record", fixture.Player.PlaybackError, StringComparison.Ordinal);
            Assert.False(fixture.Player.IsAudioPlaying);
        }
        finally
        {
            await fixture.DisposeAsync();
            server.Dispose();
        }
    }

    /// <summary>
    /// Policy 4: a diffusion-resolved model (v3 auto-adjusted away from regression) must request the
    /// batch endpoint, never the streaming endpoint, and complete end-to-end on the batch response.
    /// </summary>
    [Fact]
    [Headless]
    public async Task A2FStreaming_WithV3AutoAdjustedMode_UsesBatchEndpoint()
    {
        const int frameCount = 45;
        ScriptedA2FServer server = new(
            async session => await session.SendBatchResponseAsync(frameCount, StreamFps, ["jawOpen"]));

        await using StreamingPlaybackFixture fixture = await StreamingPlaybackFixture.CreateAsync(
            GetSceneTree(),
            server.BlendshapesUrl,
            player => player.ModelId = A2FLipSyncPlayer.ModelIdOption.V3);

        try
        {
            int completedCount = 0;
            fixture.Player.PlaybackCompleted += () => completedCount++;

            fixture.Player.Play(CreateSilenceStream(seconds: 1.5));

            Assert.True(string.IsNullOrWhiteSpace(fixture.Player.PlaybackError), fixture.Player.PlaybackError);

            await WaitUntilAsync(GetSceneTree(), () => completedCount == 1, maxFrames: 3000);

            Assert.True(string.IsNullOrWhiteSpace(fixture.Player.PlaybackError), fixture.Player.PlaybackError);

            ScriptedA2FServer.ObservedRequest request = Assert.Single(server.Requests);
            Assert.StartsWith("/blendshapes?", request.PathAndQuery, StringComparison.Ordinal);
            Assert.DoesNotContain("/stream", request.PathAndQuery, StringComparison.Ordinal);
            Assert.Contains("mode=diffusion", request.PathAndQuery, StringComparison.Ordinal);
            Assert.Contains("model=v3", request.PathAndQuery, StringComparison.Ordinal);

            // The uploaded inference payload must be float32 16 kHz mono PCM: 1.5 s × 16000 samples × 4 bytes.
            Assert.Equal(24000 * sizeof(float), request.Body.Length);

            Assert.True(fixture.Player.AppliedFrameCount > 0);
            Assert.False(fixture.Player.HasActiveStreamingSession);
        }
        finally
        {
            await fixture.DisposeAsync();
            server.Dispose();
        }
    }

    /// <summary>
    /// The overall request deadline must fail a stream that stalls without completing, rather than
    /// hanging until the idle timeout or forever.
    /// </summary>
    [Fact]
    [Headless]
    public async Task A2FStreaming_WhenStreamStallsPastRequestDeadline_FailsWithTimeoutError()
    {
        ScriptedA2FServer server = new(async session =>
        {
            await session.SendMetadataAsync(StreamFps, ["jawOpen"]);
            for (int frameIndex = 0; frameIndex < 5; frameIndex++)
            {
                await session.SendFrameAsync(frameIndex, frameIndex / 5f);
            }

            // Stall long past the one-second request deadline without further records.
            await Task.Delay(TimeSpan.FromSeconds(30));
        });

        await using StreamingPlaybackFixture fixture = await StreamingPlaybackFixture.CreateAsync(
            GetSceneTree(),
            server.BlendshapesUrl,
            player =>
            {
                player.RequestTimeoutSeconds = 1;
                player.StreamingIdleTimeoutSeconds = 15;
            });

        try
        {
            Task<LipSyncPlayer.PreparedPlayback> preparation = fixture.Player.PreparePlaybackAsync(
                CreateSilenceStream(seconds: 3.0));
            Task finishedFirst = await Task.WhenAny(preparation, Task.Delay(4000));
            Assert.Same(preparation, finishedFirst);

            int completedCount = 0;
            fixture.Player.PlaybackCompleted += () => completedCount++;

            fixture.Player.PlayPrepared(await preparation);

            await WaitUntilAsync(GetSceneTree(), () => completedCount == 1, maxFrames: 3000);

            Assert.False(string.IsNullOrWhiteSpace(fixture.Player.PlaybackError));
            Assert.Contains("exceeded the request timeout", fixture.Player.PlaybackError, StringComparison.Ordinal);
            Assert.False(fixture.Player.IsAudioPlaying);
        }
        finally
        {
            await fixture.DisposeAsync();
            server.Dispose();
        }
    }

    /// <summary>
    /// Flagged live smoke test against the real alleycat-audio2face-api container on the default local
    /// endpoint: measures that the playback gate opens after time-to-first-frame (not after the full
    /// download) and that the stream completes with valid frames.
    /// </summary>
    /// <remarks>
    /// Soft-skips when the container is unreachable so CI without the container stays green. Timing
    /// assertions are generous bounds, not strict latency budgets, to stay robust on shared hardware.
    /// </remarks>
    [Fact]
    [Headless]
    public async Task A2FLive_StreamingInferenceAgainstRealServer_GatesEarlyAndCompletes()
    {
        if (!await IsLiveAudio2FaceServerReachableAsync())
        {
            // Live container unavailable in this environment; nothing to assert against.
            return;
        }

        await using StreamingPlaybackFixture fixture = await StreamingPlaybackFixture.CreateAsync(
            GetSceneTree(),
            "http://127.0.0.1:8765/blendshapes");

        try
        {
            var gateStopwatch = Stopwatch.StartNew();
            Task<LipSyncPlayer.PreparedPlayback> preparation = fixture.Player.PreparePlaybackAsync(
                CreateSilenceStream(seconds: 1.5));

            Task finishedFirst = await Task.WhenAny(preparation, Task.Delay(8000));
            Assert.Same(preparation, finishedFirst);
            gateStopwatch.Stop();

            LipSyncPlayer.PreparedPlayback prepared = await preparation;
            LipSyncPlayer.StreamingPlaybackSession session = prepared.StreamingSession!;
            Assert.True(prepared.PreparedFrameCount >= 3, "The startup buffer must have filled before preparation completed.");
            // Warm time-to-first-frame is roughly 0.65 s; the gate must beat the full-download time by
            // a wide margin (a 1.5 s clip at ~1.6x realtime takes well over two seconds to download).
            Assert.True(
                gateStopwatch.Elapsed < TimeSpan.FromSeconds(4),
                $"Playback gate took {gateStopwatch.Elapsed.TotalSeconds:0.##} s to open; expected well under the full download time.");

            int completedCount = 0;
            fixture.Player.PlaybackCompleted += () => completedCount++;
            fixture.Player.PlayPrepared(prepared);

            await WaitUntilAsync(
                GetSceneTree(),
                () => completedCount == 1 && session.Buffer.IsCompleted,
                maxFrames: 3000);

            Assert.True(string.IsNullOrWhiteSpace(fixture.Player.PlaybackError), fixture.Player.PlaybackError);

            int frameCount = session.Buffer.FrameCount;
            Assert.True(frameCount > 0, "The live stream must produce frames.");
            Assert.Equal(frameCount, session.Buffer.DeclaredFrameCount);

            _ = session.Buffer.TryGetFrame(0, out float[] firstFrame);
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                Assert.True(session.Buffer.TryGetFrame(frameIndex, out float[] frame));
                Assert.Equal(firstFrame.Length, frame.Length);
                foreach (float weight in frame)
                {
                    Assert.True(float.IsFinite(weight), $"Frame {frameIndex} contained a non-finite weight.");
                }
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<bool> IsLiveAudio2FaceServerReachableAsync()
    {
        try
        {
            using var probeClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(2),
            };
            using HttpResponseMessage response = await probeClient.GetAsync("http://127.0.0.1:8765/health");
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static AudioStreamWav CreateSilenceStream(double seconds)
    {
        int sampleCount = (int)(16000d * seconds);
        return new AudioStreamWav
        {
            Data = new byte[sampleCount * 2],
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = 16000,
            Stereo = false,
        };
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

    /// <summary>
    /// Minimal playback fixture: an audio player, a skeleton with a jawOpen blendshape mesh, and an A2F
    /// player pointed at a scripted server.
    /// </summary>
    private sealed class StreamingPlaybackFixture : IAsyncDisposable
    {
        private readonly Node3D _root;
        private bool _disposed;

        private StreamingPlaybackFixture(Node3D root, A2FLipSyncPlayer player, MeshInstance3D mesh)
        {
            _root = root;
            Player = player;
            Mesh = mesh;
        }

        public A2FLipSyncPlayer Player
        {
            get;
        }

        public MeshInstance3D Mesh
        {
            get;
        }

        public static async Task<StreamingPlaybackFixture> CreateAsync(
            SceneTree sceneTree,
            string endpointUrl,
            Action<A2FLipSyncPlayer>? configurePlayer = null,
            A2FLipSyncPlayer? player = null)
        {
            Node3D root = new()
            {
                Name = $"A2FStreamingTestRoot_{Guid.NewGuid():N}",
            };
            AudioStreamPlayer3D audioPlayer = new()
            {
                Name = "AudioStreamPlayer3D",
            };
            Skeleton3D skeleton = new()
            {
                Name = "Skeleton3D",
            };
            MeshInstance3D mesh = CreateMeshInstance("GeneratedFace", "jawOpen");
            skeleton.AddChild(mesh);

            player ??= new A2FLipSyncPlayer();
            player.Name = "A2FLipSyncPlayer";
            player.AudioPlayer = audioPlayer;
            player.Skeleton = skeleton;
            player.EndpointUrl = endpointUrl;
            player.RequestTimeoutSeconds = 20;
            player.TranslateEyeRotationsToBlendshapes = false;
            configurePlayer?.Invoke(player);

            root.AddChild(audioPlayer);
            root.AddChild(skeleton);
            root.AddChild(player);
            _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
            await WaitForFramesAsync(sceneTree, 2);

            return new StreamingPlaybackFixture(root, player, mesh);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (GodotObject.IsInstanceValid(Player))
            {
                Player.Stop();
            }

            _root.QueueFree();
            await WaitForFramesAsync(GetSceneTree(), 2);
        }

        private static MeshInstance3D CreateMeshInstance(string name, string blendshapeName)
        {
            ArrayMesh mesh = new();
            mesh.AddBlendShape(blendshapeName);

            return new MeshInstance3D
            {
                Name = name,
                Mesh = mesh,
            };
        }
    }

    /// <summary>
    /// Uses the production HTTP streaming implementation, but deliberately delays the first cancelled
    /// read-loop's final settlement so replacement-deadline handling can be exercised deterministically.
    /// </summary>
    private sealed partial class NonSettlingA2FLipSyncPlayer : A2FLipSyncPlayer
    {
        private readonly TaskCompletionSource _releaseFirstReadLoop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _streamingCallCount;

        public TaskCompletionSource FirstCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal override async Task RunBackendStreamingInferenceAsync(
            AudioStreamWav speech,
            StreamingFrameBuffer frameBuffer,
            CancellationToken cancellationToken)
        {
            int callIndex = Interlocked.Increment(ref _streamingCallCount);
            try
            {
                await base.RunBackendStreamingInferenceAsync(speech, frameBuffer, cancellationToken);
            }
            catch (OperationCanceledException) when (callIndex == 1 && cancellationToken.IsCancellationRequested)
            {
                _ = FirstCancellationObserved.TrySetResult();
                await _releaseFirstReadLoop.Task;
                throw;
            }
        }

        public void ReleaseFirstReadLoop() => _ = _releaseFirstReadLoop.TrySetResult();
    }

    /// <summary>
    /// Local scripted Audio2Face server: records observed requests, emits NDJSON (or batch JSON)
    /// responses under script control with arbitrary delays, and observes client connection aborts.
    /// </summary>
    private sealed class ScriptedA2FServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Func<ServerSession, Task> _script;
        private readonly Action<int>? _onTransportAccepted;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly ConcurrentQueue<ServerEvent> _events = [];
        private readonly int _port;
        private int _requestCount;
        private volatile bool _disposed;
        private volatile bool _clientAborted;

        public ScriptedA2FServer(
            Func<ServerSession, Task> script,
            Action<int>? onTransportAccepted = null)
        {
            _script = script;
            _onTransportAccepted = onTransportAccepted;
            _port = FindFreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public sealed record ObservedRequest(int RequestIndex, string PathAndQuery, byte[] Body);

        public sealed record ServerEvent(string Name, long ElapsedMilliseconds);

        public string BlendshapesUrl => $"http://127.0.0.1:{_port}/blendshapes";

        public ConcurrentQueue<ObservedRequest> Requests { get; } = [];

        public ConcurrentQueue<int> TransportAcceptedRequestIndexes { get; } = [];

        public bool ClientAborted => _clientAborted;

        public bool HasEvent(string name)
        {
            foreach (ServerEvent serverEvent in _events)
            {
                if (string.Equals(serverEvent.Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception)
            {
                // The listener may already be aborted by a completed request pipeline.
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_disposed)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (_disposed)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }

                int requestIndex = Interlocked.Increment(ref _requestCount);
                TransportAcceptedRequestIndexes.Enqueue(requestIndex);
                _events.Enqueue(new ServerEvent(
                    $"request-{requestIndex}-transport-accepted",
                    _stopwatch.ElapsedMilliseconds));
                _onTransportAccepted?.Invoke(requestIndex);
                _ = Task.Run(() => HandleContextAsync(context, requestIndex));
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context, int requestIndex)
        {
            byte[] body = await ReadRequestBodyAsync(context.Request);
            Requests.Enqueue(new ObservedRequest(requestIndex, context.Request.Url?.PathAndQuery ?? string.Empty, body));

            HttpListenerResponse response = context.Response;
            response.SendChunked = true;

            try
            {
                await _script(new ServerSession(
                    response,
                    requestIndex,
                    name => _events.Enqueue(new ServerEvent(name, _stopwatch.ElapsedMilliseconds))));
                response.Close();
            }
            catch (Exception ex) when (IsClientAbort(ex))
            {
                _clientAborted = true;
                AbortResponse(response);
            }
            catch (Exception)
            {
                AbortResponse(response);
            }
        }

        private static bool IsClientAbort(Exception ex)
            => ex is IOException or HttpListenerException or ObjectDisposedException;

        private static void AbortResponse(HttpListenerResponse response)
        {
            try
            {
                response.Abort();
            }
            catch (Exception)
            {
                // The response may already be closed by the aborted client.
            }
        }

        private static async Task<byte[]> ReadRequestBodyAsync(HttpListenerRequest request)
        {
            using MemoryStream buffer = new();
            await request.InputStream.CopyToAsync(buffer);
            return buffer.ToArray();
        }

        private static int FindFreePort()
        {
            TcpListener portProbe = new(IPAddress.Loopback, 0);
            portProbe.Start();
            try
            {
                return ((IPEndPoint)portProbe.LocalEndpoint).Port;
            }
            finally
            {
                portProbe.Stop();
            }
        }

        /// <summary>
        /// Script-facing handle for one server response: NDJSON record writers, a batch JSON writer, and
        /// milestone markers with server-side timestamps.
        /// </summary>
        internal sealed class ServerSession(
            HttpListenerResponse response,
            int requestIndex,
            Action<string> markEvent)
        {
            private bool _contentTypeApplied;

            public int RequestIndex => requestIndex;

            public async Task SendMetadataAsync(float fps, IReadOnlyList<string> blendshapeNames)
            {
                string namesCsv = string.Join(",", blendshapeNames.Select(name => $"\"{name}\""));
                await SendLineAsync(
                    "{\"type\":\"metadata\",\"fps\":" + fps.ToString("0.0#", CultureInfo.InvariantCulture)
                    + ",\"blendshape_names\":[" + namesCsv + "]}");
            }

            public async Task SendFrameAsync(int index, float weight)
            {
                await SendLineAsync(
                    "{\"type\":\"frame\",\"index\":" + index.ToString(CultureInfo.InvariantCulture)
                    + ",\"timestamp\":" + ((int)(index * 16000 / StreamFps)).ToString(CultureInfo.InvariantCulture)
                    + ",\"weights\":[" + weight.ToString("R", CultureInfo.InvariantCulture) + "],\"jaw\":[0.0]}");
            }

            public async Task SendCompleteAsync(int frameCount)
            {
                await SendLineAsync(
                    "{\"type\":\"complete\",\"frame_count\":" + frameCount.ToString(CultureInfo.InvariantCulture) + "}");
            }

            public async Task SendPaddingAsync(int byteCount)
            {
                ApplyContentType("application/x-ndjson");

                byte[] bytes = new byte[byteCount + 1];
                Array.Fill(bytes, (byte)' ');
                bytes[^1] = (byte)'\n';
                await response.OutputStream.WriteAsync(bytes);
                await response.OutputStream.FlushAsync();
            }

            public async Task SendBatchResponseAsync(int frameCount, float fps, IReadOnlyList<string> blendshapeNames)
            {
                ApplyContentType("application/json");

                var frames = new StringBuilder("[");
                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    if (frameIndex > 0)
                    {
                        _ = frames.Append(',');
                    }

                    _ = frames.Append('[').Append((frameIndex / (float)frameCount).ToString("R", CultureInfo.InvariantCulture)).Append(']');
                }

                _ = frames.Append(']');

                string namesCsv = string.Join(",", blendshapeNames.Select(name => $"\"{name}\""));
                string payload = "{\"frames\":" + frames
                    + ",\"blendshape_names\":[" + namesCsv + "]"
                    + ",\"fps\":" + fps.ToString("0.0#", CultureInfo.InvariantCulture) + "}";

                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes);
                await response.OutputStream.FlushAsync();
            }

            public void MarkEvent(string name) => markEvent(name);

            private async Task SendLineAsync(string line)
            {
                ApplyContentType("application/x-ndjson");

                byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                await response.OutputStream.WriteAsync(bytes);
                await response.OutputStream.FlushAsync();
            }

            private void ApplyContentType(string contentType)
            {
                if (_contentTypeApplied)
                {
                    return;
                }

                response.ContentType = contentType;
                _contentTypeApplied = true;
            }
        }
    }
}
