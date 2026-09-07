using System.Reflection;
using AlleyCat.Control;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core;
using AlleyCat.Interaction;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using AlleyCat.Speech.Transcription;
using AlleyCat.TestFramework;
using AlleyCat.UI;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using UIControl = Godot.Control;

namespace AlleyCat.IntegrationTests.UI;

/// <summary>
/// Runtime coverage for the XR-driven game menu widget and its pause-based gameplay-input suppression.
/// </summary>
[Headless]
public sealed partial class GameMenuIntegrationTests
{
    private const string UiOverlayScenePath = "res://assets/ui/ui_overlay.tscn";
    private const string ToggleAction = "menu_button";
    private const string NavigateAction = "primary";
    private const string ConfirmAction = "trigger_click";
    private const string GrabAction = "grip_click";
    private const string GrabFloatAction = "grip";

    /// <summary>
    /// Verifies the packed overlay layout contains the menu, initially hidden with the expected options.
    /// </summary>
    [Fact]
    public async Task GameMenu_InPackedOverlayScene_ExistsInitiallyHiddenWithOptions()
    {
        SceneTree sceneTree = GetSceneTree();
        UIOverlay overlay = LoadPackedScene(UiOverlayScenePath).Instantiate<UIOverlay>();
        sceneTree.Root.AddChild(overlay);

        try
        {
            GameMenu menu = Assert.IsType<GameMenu>(overlay.GetNode("GameMenu"));
            Assert.Same(menu, overlay.FindWidget<GameMenu>());
            Assert.False(menu.Visible);
            Assert.False(menu.IsOpen);
            Assert.Equal(GameMenuOption.Resume, menu.SelectedOption);
            Assert.Equal(Node.ProcessModeEnum.Always, menu.ProcessMode);
            Assert.Equal(UIControl.MouseFilterEnum.Ignore, menu.MouseFilter);

            Label title = menu.GetNode<Label>("Center/Panel/Options/Title");
            Button resumeOption = menu.GetNode<Button>("Center/Panel/Options/ResumeOption");
            Button exitOption = menu.GetNode<Button>("Center/Panel/Options/ExitOption");

            Assert.Equal("Menu", title.Text);
            Assert.Equal("Resume", resumeOption.Text);
            Assert.Equal("Exit Game", exitOption.Text);
            Assert.NotEqual(resumeOption.Modulate, exitOption.Modulate);
        }
        finally
        {
            overlay.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Verifies only the left controller's menu button opens the menu with Resume selected and the tree paused.
    /// </summary>
    [Fact]
    public async Task GameMenu_MenuButtonPress_LeftOpensMenuAndPausesTreeAndRightIsIgnored()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.XRManager.RightController.TriggerActionButtonPressed(ToggleAction);
            await WaitForFramesAsync(sceneTree, 2);

            Assert.False(fixture.Menu.IsOpen);
            Assert.False(fixture.Menu.Visible);
            Assert.False(fixture.Global.Paused);

            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);

            Assert.True(fixture.Menu.IsOpen);
            Assert.True(fixture.Menu.Visible);
            // This fact anchors the Game.Paused API to the underlying SceneTree state.
            Assert.True(sceneTree.Paused);
            Assert.Equal(GameMenuOption.Resume, fixture.Menu.SelectedOption);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies toggling closed with the menu button unpauses the tree at the end of the closing frame.
    /// </summary>
    [Fact]
    public async Task GameMenu_MenuButtonToggle_ClosesMenuAndUnpausesTree()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);
            Assert.True(fixture.Global.Paused);

            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);
            Assert.False(fixture.Menu.IsOpen);
            Assert.False(fixture.Menu.Visible);

            // The unpause is deferred to the end of the closing frame so the closing input edge is
            // still observed as paused by gameplay consumers.
            await WaitForFramesAsync(sceneTree, 2);
            Assert.False(fixture.Global.Paused);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies thumbstick navigation moves exactly one step per gesture and requires a return to neutral.
    /// </summary>
    [Fact]
    public async Task GameMenu_PrimaryAxisNavigation_StepsOncePerGestureFromEitherHand()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);
            Assert.Equal(GameMenuOption.Resume, fixture.Menu.SelectedOption);

            FakeXRHandController leftController = fixture.XRManager.LeftController;
            FakeXRHandController rightController = fixture.XRManager.RightController;

            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.5f));
            Assert.Equal(GameMenuOption.Resume, fixture.Menu.SelectedOption);

            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.8f));
            Assert.Equal(GameMenuOption.ExitGame, fixture.Menu.SelectedOption);

            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.9f));
            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.85f));
            Assert.Equal(GameMenuOption.ExitGame, fixture.Menu.SelectedOption);

            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.05f));
            Assert.Equal(GameMenuOption.ExitGame, fixture.Menu.SelectedOption);

            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, -0.8f));
            Assert.Equal(GameMenuOption.Resume, fixture.Menu.SelectedOption);

            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.05f));
            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, -0.8f));
            Assert.Equal(GameMenuOption.ExitGame, fixture.Menu.SelectedOption);

            rightController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.05f));
            rightController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.8f));
            Assert.Equal(GameMenuOption.Resume, fixture.Menu.SelectedOption);

            // Navigation stays live for the whole session because the menu processes while paused.
            Assert.True(fixture.Global.Paused);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies an upward thumbstick gesture from the top option wraps around to the bottom option.
    /// </summary>
    [Fact]
    public async Task GameMenu_PrimaryAxisNavigation_UpFromTopOption_WrapsToBottomOption()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);
            Assert.Equal(GameMenuOption.Resume, fixture.Menu.SelectedOption);

            FakeXRHandController leftController = fixture.XRManager.LeftController;

            // OpenXR reports +Y for an upward push, which wraps from Resume onto Exit Game.
            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.8f));
            Assert.Equal(GameMenuOption.ExitGame, fixture.Menu.SelectedOption);

            // A further upward gesture steps from the bottom option back onto the top option.
            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.05f));
            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.8f));
            Assert.Equal(GameMenuOption.Resume, fixture.Menu.SelectedOption);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies a downward thumbstick gesture from the bottom option wraps around to the top option.
    /// </summary>
    [Fact]
    public async Task GameMenu_PrimaryAxisNavigation_DownFromBottomOption_WrapsToTopOption()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);
            Assert.Equal(GameMenuOption.Resume, fixture.Menu.SelectedOption);

            FakeXRHandController leftController = fixture.XRManager.LeftController;

            // A downward push (-Y) wraps from Resume onto Exit Game.
            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, -0.8f));
            Assert.Equal(GameMenuOption.ExitGame, fixture.Menu.SelectedOption);

            // A further downward gesture wraps from Exit Game back onto Resume.
            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.05f));
            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, -0.8f));
            Assert.Equal(GameMenuOption.Resume, fixture.Menu.SelectedOption);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies either hand can confirm Exit Game while paused and that the stub game records exit code 0.
    /// </summary>
    [Fact]
    public async Task GameMenu_ConfirmExitGame_FromEitherHand_RequestsCleanExitCodeZero()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);

            fixture.XRManager.LeftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.8f));
            Assert.Equal(GameMenuOption.ExitGame, fixture.Menu.SelectedOption);

            fixture.XRManager.LeftController.TriggerActionButtonPressed(ConfirmAction);
            Assert.Equal([0], fixture.ExitRequests);

            // The menu stays open on Exit Game after the left-hand confirm; an upward gesture now
            // steps up to Resume, and a second upward gesture wraps back onto Exit Game.
            fixture.XRManager.RightController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.05f));
            fixture.XRManager.RightController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.8f));
            Assert.Equal(GameMenuOption.Resume, fixture.Menu.SelectedOption);

            fixture.XRManager.RightController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.05f));
            fixture.XRManager.RightController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.8f));
            Assert.Equal(GameMenuOption.ExitGame, fixture.Menu.SelectedOption);

            fixture.XRManager.RightController.TriggerActionButtonPressed(ConfirmAction);
            Assert.Equal([0, 0], fixture.ExitRequests);

            // The exit request works while the tree is paused and the menu stays open afterwards.
            Assert.True(fixture.Menu.IsOpen);
            Assert.True(fixture.Global.Paused);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies confirming Resume closes the menu and unpauses the tree at the end of the closing frame.
    /// </summary>
    [Fact]
    public async Task GameMenu_ConfirmResume_ClosesMenuAndUnpausesTree()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);

            fixture.XRManager.LeftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.8f));
            fixture.XRManager.LeftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.05f));
            fixture.XRManager.LeftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, -0.8f));
            Assert.Equal(GameMenuOption.Resume, fixture.Menu.SelectedOption);

            fixture.XRManager.LeftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.05f));

            fixture.XRManager.LeftController.TriggerActionButtonPressed(ConfirmAction);
            Assert.False(fixture.Menu.IsOpen);
            Assert.False(fixture.Menu.Visible);

            await WaitForFramesAsync(sceneTree, 2);
            Assert.False(fixture.Global.Paused);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies locomotion, grab, and transcription input does not leak while the paused menu is open, and that the
    /// same events act again once the menu has closed and unpaused the tree.
    /// </summary>
    [Fact]
    public async Task GameMenu_WhileOpenPaused_GameplayInputIsSuppressedUntilUnpause()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            FakeXRHandController leftController = fixture.XRManager.LeftController;
            FakeXRHandController rightController = fixture.XRManager.RightController;

            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.7f));
            rightController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0.5f, 0f));
            Assert.Equal(1, fixture.Locomotion.NonZeroMoveCallCount);
            Assert.Equal(1, fixture.Locomotion.NonZeroRotateCallCount);

            leftController.TriggerActionButtonPressed(ToggleAction);
            Assert.True(fixture.Global.Paused);
            Assert.Equal(Vector2.Zero, fixture.Locomotion.LastMove);
            Assert.Equal(Vector2.Zero, fixture.Locomotion.LastRotate);

            // The mock controllers keep emitting during the pause, which mirrors the always-processing
            // XR runtime; the gameplay consumers must ignore those events.
            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 1f));
            rightController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(1f, 0f));
            Assert.Equal(Vector2.Zero, fixture.Locomotion.LastMove);
            Assert.Equal(Vector2.Zero, fixture.Locomotion.LastRotate);
            Assert.Equal(1, fixture.Locomotion.NonZeroMoveCallCount);
            Assert.Equal(1, fixture.Locomotion.NonZeroRotateCallCount);

            rightController.TriggerActionButtonPressed(GrabAction);
            Assert.Equal(0, fixture.RightHand.GrabCallCount);

            leftController.TriggerActionButtonPressed(ConfirmAction);
            Assert.False(fixture.Transcriber.IsRecording);
            Assert.Equal(0, fixture.Transcriber.TranscribeCallCount);

            // After the menu closed and the tree unpaused, the same events act on the world again.
            await WaitForFramesAsync(sceneTree, 2);
            Assert.False(fixture.Global.Paused);

            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.9f));
            rightController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0.9f, 0f));
            Assert.Equal(new Vector2(0f, 0.9f), fixture.Locomotion.LastMove);
            Assert.Equal(new Vector2(0.9f, 0f), fixture.Locomotion.LastRotate);
            Assert.Equal(2, fixture.Locomotion.NonZeroMoveCallCount);
            Assert.Equal(2, fixture.Locomotion.NonZeroRotateCallCount);

            rightController.TriggerActionButtonPressed(GrabAction);
            Assert.Equal(1, fixture.RightHand.GrabCallCount);

            leftController.TriggerActionButtonPressed(ConfirmAction);
            Assert.True(fixture.Transcriber.IsRecording);
            leftController.TriggerActionButtonReleased(ConfirmAction);
            Assert.False(fixture.Transcriber.IsRecording);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies a stick held deflected through the pause is zeroed at the pause boundary and does not leak stale
    /// movement after unpause until a fresh axis event arrives.
    /// </summary>
    [Fact]
    public async Task PlayerController_StickHeldThroughPause_DoesNotLeakMovementOnUnpause()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            FakeXRHandController leftController = fixture.XRManager.LeftController;

            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.8f));
            Assert.Equal(new Vector2(0f, 0.8f), fixture.Locomotion.LastMove);
            Assert.Equal(1, fixture.Locomotion.NonZeroMoveCallCount);

            leftController.TriggerActionButtonPressed(ToggleAction);
            Assert.True(fixture.Global.Paused);
            Assert.Equal(Vector2.Zero, fixture.Locomotion.LastMove);

            // A held stick that keeps drifting while paused must not update movement input either.
            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.95f));
            Assert.Equal(Vector2.Zero, fixture.Locomotion.LastMove);
            Assert.Equal(1, fixture.Locomotion.NonZeroMoveCallCount);

            leftController.TriggerActionButtonPressed(ToggleAction);
            await WaitForFramesAsync(sceneTree, 2);
            Assert.False(fixture.Global.Paused);

            // No new axis event arrived, so the zeroed movement must not resurrect on its own.
            await WaitForFramesAsync(sceneTree, 3);
            Assert.Equal(Vector2.Zero, fixture.Locomotion.LastMove);

            leftController.TriggerActionVector2InputChanged(NavigateAction, new Vector2(0f, 0.9f));
            Assert.Equal(new Vector2(0f, 0.9f), fixture.Locomotion.LastMove);
            Assert.Equal(2, fixture.Locomotion.NonZeroMoveCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies a recording started before the menu opens stops at the pause boundary and its finalisation
    /// completes once the menu closes and unpauses the tree.
    /// </summary>
    [Fact]
    public async Task Transcriber_ActiveRecording_StopsAtPauseAndFinalisesAfterUnpause()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.XRManager.LeftController.TriggerActionButtonPressed(ConfirmAction);
            Assert.True(fixture.Transcriber.IsRecording);

            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);
            Assert.True(fixture.Global.Paused);
            Assert.False(fixture.Transcriber.IsRecording);
            Assert.True(fixture.Transcriber.IsFinalising);

            // The finalisation drain is driven by processing, so it stays parked while the tree is
            // paused and no transcription request is dispatched.
            await WaitForFramesAsync(sceneTree, 5);
            Assert.Equal(0, fixture.Transcriber.TranscribeCallCount);

            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);
            await WaitUntilAsync(sceneTree, () => fixture.Transcriber.TranscribeCallCount == 1, 120);
            Assert.False(fixture.Global.Paused);
            Assert.False(fixture.Transcriber.IsFinalising);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies a trigger held across the unpause boundary does not start a recording; only a fresh
    /// release-then-press edge does.
    /// </summary>
    [Fact]
    public async Task Transcriber_HeldTriggerAcrossPause_DoesNotStartRecordingAfterUnpause()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            FakeXRHandController leftController = fixture.XRManager.LeftController;

            // Press and hold the record trigger, then open the menu, which stops the recording.
            leftController.TriggerActionButtonPressed(ConfirmAction);
            Assert.True(fixture.Transcriber.IsRecording);

            leftController.TriggerActionButtonPressed(ToggleAction);
            Assert.True(fixture.Global.Paused);
            Assert.False(fixture.Transcriber.IsRecording);

            leftController.TriggerActionButtonPressed(ToggleAction);
            await WaitUntilAsync(sceneTree, () => fixture.Transcriber.TranscribeCallCount == 1, 120);
            Assert.False(fixture.Global.Paused);

            // The trigger is still physically held: no new press edge occurred, so no recording
            // starts on unpause even after the parked finalisation finished.
            await WaitForFramesAsync(sceneTree, 3);
            Assert.False(fixture.Transcriber.IsRecording);

            leftController.TriggerActionButtonReleased(ConfirmAction);
            Assert.False(fixture.Transcriber.IsRecording);

            leftController.TriggerActionButtonPressed(ConfirmAction);
            Assert.True(fixture.Transcriber.IsRecording);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies the trigger press that confirms Resume does not start a recording through the record hand, because
    /// the unpause happens at the end of the closing frame rather than during the press dispatch.
    /// </summary>
    [Fact]
    public async Task Transcriber_ConfirmResumeWithRecordHandTrigger_DoesNotStartRecording()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);
            Assert.True(fixture.Global.Paused);

            // Resume is selected by default; the record-hand trigger confirms it and closes the menu.
            fixture.XRManager.LeftController.TriggerActionButtonPressed(ConfirmAction);
            Assert.False(fixture.Menu.IsOpen);

            await WaitForFramesAsync(sceneTree, 2);
            Assert.False(fixture.Global.Paused);
            Assert.False(fixture.Transcriber.IsRecording);
            Assert.Equal(0, fixture.Transcriber.TranscribeCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies grab presses and releases are suppressed while the menu is paused — no grab or release action
    /// fires mid-pause — and that the approved resume reconciliation then acts exactly once: the open-grip
    /// observation recorded during the pause releases a single time on unpause, and the analogue tracker's
    /// cleared latch makes the next rising edge a genuine fresh grab.
    /// </summary>
    [Fact]
    public async Task PlayerController_WhileMenuPaused_GrabInputIsSuppressedUntilUnpause()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            FakeXRHandController rightController = fixture.XRManager.RightController;

            // Establish both an analogue grab and a button grab before the menu opens.
            rightController.TriggerActionFloatInputChanged(GrabFloatAction, 0.8f);
            Assert.Equal(1, fixture.RightHand.GrabCallCount);

            rightController.TriggerActionButtonPressed(GrabAction);
            Assert.Equal(2, fixture.RightHand.GrabCallCount);

            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);
            Assert.True(fixture.Global.Paused);

            // Both press and release edges are dropped during the pause: the whole world, including
            // hands and held objects, resumes with the world.
            rightController.TriggerActionButtonPressed(GrabAction);
            Assert.Equal(2, fixture.RightHand.GrabCallCount);

            rightController.TriggerActionButtonReleased(GrabAction);
            rightController.TriggerActionFloatInputChanged(GrabFloatAction, 0.1f);
            Assert.Equal(0, fixture.RightHand.ReleaseCallCount);

            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);
            await WaitForFramesAsync(sceneTree, 2);
            Assert.False(fixture.Global.Paused);

            // The open-grip observation recorded during the pause reconciles exactly once on resume.
            Assert.Equal(1, fixture.RightHand.ReleaseCallCount);

            rightController.TriggerActionButtonPressed(GrabAction);
            Assert.Equal(3, fixture.RightHand.GrabCallCount);

            rightController.TriggerActionButtonReleased(GrabAction);
            Assert.Equal(2, fixture.RightHand.ReleaseCallCount);

            // The analogue tracker cleared its pressed state when the paused release was observed, so the
            // rising edge is a genuine fresh grab rather than a consumed repeat.
            rightController.TriggerActionFloatInputChanged(GrabFloatAction, 0.95f);
            Assert.Equal(4, fixture.RightHand.GrabCallCount);

            rightController.TriggerActionFloatInputChanged(GrabFloatAction, 0.1f);
            Assert.Equal(3, fixture.RightHand.ReleaseCallCount);

            rightController.TriggerActionFloatInputChanged(GrabFloatAction, 0.95f);
            Assert.Equal(5, fixture.RightHand.GrabCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies the menu binds through the already-initialised path when added after XR initialisation.
    /// </summary>
    [Fact]
    public async Task GameMenu_AddedAfterXRInitialisation_BindsAndOpens()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            UIOverlay lateOverlay = LoadPackedScene(UiOverlayScenePath).Instantiate<UIOverlay>();
            fixture.SubViewport.AddChild(lateOverlay);
            await WaitForFramesAsync(sceneTree, 2);

            try
            {
                GameMenu lateMenu = lateOverlay.FindWidget<GameMenu>()
                    ?? throw new InvalidOperationException("Expected a GameMenu under the late-bound overlay.");

                fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);
                Assert.True(lateMenu.IsOpen);
                Assert.True(fixture.Global.Paused);
            }
            finally
            {
                lateOverlay.QueueFree();
                await WaitForFramesAsync(sceneTree, 2);
            }
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies freeing the menu while it is open unpauses the tree and stops the menu consuming input.
    /// </summary>
    [Fact]
    public async Task GameMenu_ExitTree_WhileOpenUnpausesTreeAndStopsConsumingInput()
    {
        SceneTree sceneTree = GetSceneTree();
        GameMenuRuntimeFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);
            Assert.True(fixture.Global.Paused);

            fixture.Overlay.QueueFree();
            await WaitForFramesAsync(sceneTree, 3);
            Assert.False(fixture.Global.Paused);

            fixture.XRManager.LeftController.TriggerActionButtonPressed(ToggleAction);
            await WaitForFramesAsync(sceneTree, 2);
            Assert.False(fixture.Global.Paused);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    private static async Task<GameMenuRuntimeFixture> CreateFixtureAsync(SceneTree sceneTree)
    {
        StubGame global = new()
        {
            Name = "Global",
        };

        FakeXRManager xrManager = new()
        {
            Name = "XR",
        };

        SubViewport subViewport = new()
        {
            Name = "SubViewport",
            Disable3D = true,
        };

        UIOverlay overlay = LoadPackedScene(UiOverlayScenePath).Instantiate<UIOverlay>();

        Node player = new()
        {
            Name = "Player",
        };

        RecordingLocomotion locomotion = new()
        {
            Name = "Locomotion",
        };

        FakeHands hands = new()
        {
            Name = "Hands",
        };

        FakeHand rightHand = new(LimbSide.Right)
        {
            Name = "RightHand",
        };

        FakeHand leftHand = new(LimbSide.Left)
        {
            Name = "LeftHand",
        };

        PlayerController controller = new()
        {
            Name = "PlayerController",
            LocomotionNode = locomotion,
            HandHolderNode = hands,
        };

        FakeTranscriber transcriber = new()
        {
            Name = "Transcriber",
            RecordHand = LimbSide.Left,
            AudioCaptureForTesting = new FakeAudioFrameCapture(framesAvailableAfterClear: 1),
        };

        hands.AddChild(rightHand);
        hands.AddChild(leftHand);
        player.AddChild(locomotion);
        player.AddChild(hands);
        player.AddChild(controller);
        subViewport.AddChild(overlay);
        xrManager.AddChild(subViewport);
        global.AddChild(xrManager);
        global.AddChild(player);
        global.AddChild(transcriber);

        global._EnterTree();
        sceneTree.Root.AddChild(global);
        await WaitForFramesAsync(sceneTree, 3);

        // Godot does not dispatch _Ready for nested private Node subclasses without a registered
        // script ancestor, so the fake hand holder is readied manually like the grab-input fixtures.
        hands._Ready();
        await WaitForFramesAsync(sceneTree, 2);

        GameMenu menu = overlay.FindWidget<GameMenu>()
            ?? throw new InvalidOperationException("Expected a GameMenu under the packed UI overlay fixture.");

        return new GameMenuRuntimeFixture(
            global,
            xrManager,
            subViewport,
            overlay,
            menu,
            controller,
            locomotion,
            rightHand,
            leftHand,
            transcriber);
    }

    private static async Task DestroyFixtureAsync(SceneTree sceneTree, GameMenuRuntimeFixture fixture)
    {
        // Defensive teardown hygiene: a failed fact must never leave the tree paused for the next one.
        sceneTree.Paused = false;

        fixture.Global.QueueFree();
        await WaitForFramesAsync(sceneTree, 2);
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

    private sealed record GameMenuRuntimeFixture(
        StubGame Global,
        FakeXRManager XRManager,
        SubViewport SubViewport,
        UIOverlay Overlay,
        GameMenu Menu,
        PlayerController Controller,
        RecordingLocomotion Locomotion,
        FakeHand RightHand,
        FakeHand LeftHand,
        FakeTranscriber Transcriber)
    {
        public List<int> ExitRequests => Global.ExitRequests;
    }

    private sealed partial class StubGame : Game
    {
        public List<int> ExitRequests { get; } = [];

        public override void RequestExit(int exitCode)
            => ExitRequests.Add(exitCode);
    }

    private sealed partial class RecordingLocomotion : Node, ILocomotion
    {
        public int NonZeroMoveCallCount
        {
            get;
            private set;
        }

        public int NonZeroRotateCallCount
        {
            get;
            private set;
        }

        public Vector2 LastMove
        {
            get;
            private set;
        }

        public Vector2 LastRotate
        {
            get;
            private set;
        }

        public void Move(Vector2 input)
        {
            LastMove = input;
            if (input != Vector2.Zero)
            {
                NonZeroMoveCallCount++;
            }
        }

        public void Rotate(Vector2 input)
        {
            LastRotate = input;
            if (input != Vector2.Zero)
            {
                NonZeroRotateCallCount++;
            }
        }
    }

    private sealed partial class FakeHands : Node, IHasHands
    {
        private IComponent[] _components = [];

        public IReadOnlyList<IComponent> Components => _components;

        public override void _Ready() => _components = [.. GetChildren().OfType<IComponent>()];
    }

    private sealed partial class FakeHand(LimbSide side) : Node, IHand
    {
        public LimbSide Side => side;

        public IGrabbable? CurrentGrabbed => null;

        public int GrabCallCount
        {
            get;
            private set;
        }

        public int ReleaseCallCount
        {
            get;
            private set;
        }

        public IGrabbable? Grab()
        {
            GrabCallCount++;
            return null;
        }

        public void Release() => ReleaseCallCount++;
    }

    private sealed partial class FakeTranscriber : Transcriber
    {
        public int TranscribeCallCount
        {
            get;
            private set;
        }

        public override Task<string> Transcribe(RecordedAudioData recording)
        {
            TranscribeCallCount++;
            return Task.FromResult("Game menu test transcript");
        }
    }

    private sealed class FakeAudioFrameCapture(int framesAvailableAfterClear) : IAudioFrameCapture
    {
        public long FramesAvailable
        {
            get;
            private set;
        }

        public long DiscardedFrames
        {
            get;
            set;
        }

        public Vector2[] ReadFrames(int maximumFrames)
        {
            int count = (int)Math.Min(maximumFrames, FramesAvailable);
            var frames = new Vector2[count];
            Array.Fill(frames, new Vector2(0.25f, -0.25f));
            FramesAvailable -= count;
            return frames;
        }

        public void Clear() => FramesAvailable = framesAvailableAfterClear;
    }

    private sealed partial class FakeXRManager : XRManager
    {
        private static readonly FieldInfo _runtimeBackingField = typeof(XRManager)
            .GetField("<Runtime>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected XRManager runtime backing field for game menu tests.");

        private readonly FakeXRRuntime _runtime = new();

        public FakeXRHandController LeftController => _runtime.LeftControllerNode;

        public FakeXRHandController RightController => _runtime.RightControllerNode;

        public override void _Ready()
        {
            _runtimeBackingField.SetValue(this, _runtime);
            InitialisationAttempted = true;
            InitialisationSucceeded = true;
            _ = EmitSignal(SignalName.Initialised, true);
        }
    }

    private sealed class FakeXRRuntime : IXRRuntime
    {
        private readonly XRControllerHandTracking _handTracking;

        public FakeXRRuntime()
        {
            OriginNode = new Node3D();
            CameraNode = new Camera3D();
            LeftControllerNode = new FakeXRHandController();
            RightControllerNode = new FakeXRHandController();
            _handTracking = new XRControllerHandTracking(RightControllerNode, LeftControllerNode);
        }

        public IXROrigin Origin => new FakeXROrigin(OriginNode);

        public IXRCamera Camera => new FakeXRCamera(CameraNode);

        public IXRHandController RightHandController => RightControllerNode;

        public IXRHandController LeftHandController => LeftControllerNode;

        public XRHandTrackingMode HandTrackingMode => XRHandTrackingMode.Controller;

        public IXRHandJointProvider OpticalHandJoints => XREmptyHandJointProvider.Instance;

        public IXRHandPoseSource GetHandPoseSource(LimbSide side) => _handTracking.GetHandPoseSource(side);

#pragma warning disable CS0067
        public event Action? PoseRecentered;

        public event Action? HandTrackingModeChanged;
#pragma warning restore CS0067

        public Node3D OriginNode
        {
            get;
        }

        public Camera3D CameraNode
        {
            get;
        }

        public FakeXRHandController LeftControllerNode
        {
            get;
        }

        public FakeXRHandController RightControllerNode
        {
            get;
        }

        public bool Initialise(SubViewport viewport, int maximumRefreshRate)
        {
            _ = viewport;
            _ = maximumRefreshRate;
            return true;
        }
    }

    private sealed partial class FakeXRHandController : Node3D, IXRHandController
    {
#pragma warning disable CS0067
        public event Action<string>? ActionButtonPressed;
        public event Action<string>? ActionButtonReleased;
        public event Action<string, float>? ActionFloatInputChanged;
        public event Action<string, Vector2>? ActionVector2InputChanged;
#pragma warning restore CS0067

        public Node3D ControllerNode => this;

        public Node3D HandPositionNode => this;

        public void TriggerActionButtonPressed(string actionName)
            => ActionButtonPressed?.Invoke(actionName);

        public void TriggerActionButtonReleased(string actionName)
            => ActionButtonReleased?.Invoke(actionName);

        public void TriggerActionFloatInputChanged(string actionName, float value)
            => ActionFloatInputChanged?.Invoke(actionName, value);

        public void TriggerActionVector2InputChanged(string actionName, Vector2 value)
            => ActionVector2InputChanged?.Invoke(actionName, value);
    }

    private sealed record FakeXROrigin(Node3D OriginNode) : IXROrigin
    {
        public float WorldScale { get; set; } = 1.0f;
    }

    private sealed record FakeXRCamera(Camera3D CameraNode) : IXRCamera;
}
