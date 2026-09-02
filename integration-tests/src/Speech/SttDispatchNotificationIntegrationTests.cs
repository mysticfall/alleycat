using AlleyCat.IntegrationTests.Support;
using AlleyCat.Speech.Transcription;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// End-to-end integration coverage for the speech-owned STT dispatch marker's child-category toggle through the real
/// AlleyCat logging and notification infrastructure.
/// </summary>
public sealed class SttDispatchNotificationIntegrationTests
{
    private const int MaxWaitFrames = 30;

    /// <summary>
    /// The STT child category's configured level alone controls the dispatch marker's toast: debug admission —
    /// inherited from the parent's trace override or set explicitly — posts exactly one toast, while a child
    /// <c>None</c> entry suppresses it even above the parent's trace override, leaving other pipeline notifications
    /// untouched.
    /// </summary>
    [Fact]
    public async Task SttDispatchNotifications_ChildCategoryLevel_AloneControlsToastVisibility()
    {
        SceneTree sceneTree = GetSceneTree();
        var dispatchDuration = TimeSpan.FromSeconds(2.35);
        const int dispatchPcmBytes = 75200;

        // Parent trace override alone: the child inherits debug admission through prefix-rule matching, so the
        // dispatch marker posts exactly one toast — no more, no less.
        NotificationLoggingFixture parentTraceFixture =
            await NotificationLoggingFixture.CreateAsync(sceneTree, enablePipelineTraceLogging: true);
        try
        {
            SttDispatchLog.Dispatch("manual", dispatchDuration, dispatchPcmBytes);

            await WaitUntilAsync(
                sceneTree,
                () => parentTraceFixture.GetNotificationTexts().Count >= 1,
                MaxWaitFrames);

            Assert.Equal(
                "Dispatching manual audio to STT (2.35 seconds, 75200 PCM bytes)",
                Assert.Single(parentTraceFixture.GetNotificationTexts()));
        }
        finally
        {
            await parentTraceFixture.DestroyAsync(sceneTree);
        }

        // Parent trace plus child None — the shipped YAML's documented disable switch — keeps the dispatch toast off
        // without the parent's trace admission leaking through.
        NotificationLoggingFixture childNoneFixture =
            await NotificationLoggingFixture.CreateAsync(
                sceneTree,
                enablePipelineTraceLogging: true,
                sttCategoryLevel: "None");
        try
        {
            SttDispatchLog.Dispatch("manual", dispatchDuration, dispatchPcmBytes);
            await WaitForFramesAsync(sceneTree, 5);

            Assert.Empty(childNoneFixture.GetNotificationTexts());
        }
        finally
        {
            await childNoneFixture.DestroyAsync(sceneTree);
        }

        // Child debug alone — the shipped YAML default — enables the toast even without a parent opt-in.
        NotificationLoggingFixture childDebugOnlyFixture =
            await NotificationLoggingFixture.CreateAsync(
                sceneTree,
                enablePipelineTraceLogging: false,
                sttCategoryLevel: "Debug");
        try
        {
            SttDispatchLog.Dispatch("automatic", dispatchDuration, dispatchPcmBytes);

            await WaitUntilAsync(
                sceneTree,
                () => childDebugOnlyFixture.GetNotificationTexts().Count >= 1,
                MaxWaitFrames);

            Assert.Equal(
                "Dispatching automatic audio to STT (2.35 seconds, 75200 PCM bytes)",
                Assert.Single(childDebugOnlyFixture.GetNotificationTexts()));
        }
        finally
        {
            await childDebugOnlyFixture.DestroyAsync(sceneTree);
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
