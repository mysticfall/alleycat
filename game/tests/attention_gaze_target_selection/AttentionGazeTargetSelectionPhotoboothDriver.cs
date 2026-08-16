using AlleyCat.Character;
using AlleyCat.Common;
using AlleyCat.Mind.Attention;
using AlleyCat.Vision;
using Godot;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.Testing;

/// <summary>
/// Test-only GDScript bridge for AI-007 photobooth scenario control and non-visual runtime probe values.
/// </summary>
[GlobalClass]
public sealed partial class AttentionGazeTargetSelectionPhotoboothDriver : Node
{
    private const float HighAttentionContribution = 1f;
    private const float BlinkDurationSeconds = 0.3f;

    private AttentionGazeTargetSelectionPhotoboothMind? _mind;
    private AttentionGazeTargetSelector? _selector;
    private EyesBehaviour? _eyes;
    private AttentionGazeTargetSelectionCueSubject? _leftTarget;
    private AttentionGazeTargetSelectionCueSubject? _rightTarget;
    private bool _isActivated;

    /// <summary>Commits character component projection before the selector resolves Vision.</summary>
    public void Activate()
    {
        if (_isActivated)
        {
            return;
        }

        CharacterHub observer = RequireCharacter("Observer");
        observer.RefreshComponents();

        _mind = this.RequireNode<AttentionGazeTargetSelectionPhotoboothMind>("../Subject/Observer/Mind");
        _selector = this.RequireNode<AttentionGazeTargetSelector>("../Subject/Observer/Mind/AttentionGazeTargetSelector");
        _leftTarget = this.RequireNode<AttentionGazeTargetSelectionCueSubject>("../Subject/LeftTarget");
        _rightTarget = this.RequireNode<AttentionGazeTargetSelectionCueSubject>("../Subject/RightTarget");
        _eyes = ((ICharacter)observer).RequireVision() as EyesBehaviour
            ?? throw new InvalidOperationException("AI-007 photobooth observer must expose EyesBehaviour as IVision.");
        _eyes.MinimumBlinkInterval = 99f;
        _eyes.MaximumBlinkInterval = 99f;
        _eyes.BlinkDuration = BlinkDurationSeconds;
        _eyes.VisualSurveyIntervalSeconds = 99d;
        _eyes.AnimationTree!.Active = true;
        _isActivated = true;
    }

    /// <summary>Routes left-dominant attention through the production selector to the published left cue.</summary>
    public void SetLeftAttendedScenario() => SetScenario(_leftTarget);

    /// <summary>Routes right-dominant attention through the production selector to the published right cue.</summary>
    public void SetRightAttendedScenario() => SetScenario(_rightTarget);

    /// <summary>Requests a normal EyesBehaviour blink without replacing the selector-owned gaze assignment.</summary>
    public void TriggerAssignedTargetBlink()
    {
        EnsureActivated();
        _eyes!.TriggerBlink();
    }

    /// <summary>Returns the current EyesBehaviour look-target path for runner assertions.</summary>
    public string GetAssignedLookTargetPath()
    {
        EnsureActivated();
        Node3D? lookTarget = _eyes!.LookTarget;
        return lookTarget is null ? string.Empty : lookTarget.GetPath().ToString();
    }

    /// <summary>Reports whether EyesBehaviour retains an explicit selector-assigned look target.</summary>
    public bool HasAssignedLookTarget()
    {
        EnsureActivated();
        return _eyes!.HasRuntimeLookTarget();
    }

    /// <summary>Reads a float AnimationTree eye-presentation parameter for runner assertions.</summary>
    public float GetEyeAnimationParameter(string parameter)
    {
        EnsureActivated();
        AnimationTree animationTree = _eyes!.AnimationTree
            ?? throw new InvalidOperationException("AI-007 photobooth EyesBehaviour requires its AnimationTree.");
        return animationTree.Get(new StringName(parameter)).AsSingle();
    }

    private void SetScenario(AttentionGazeTargetSelectionCueSubject? dominantTarget)
    {
        EnsureActivated();
        AttentionGazeTargetSelectionCueSubject dominant = dominantTarget
            ?? throw new InvalidOperationException("AI-007 photobooth scenario has no dominant target.");
        _mind!.SetAttentionWeights(
            dominant.FullId,
            HighAttentionContribution);
        _selector!.RequestEvaluation();
    }

    private void EnsureActivated()
    {
        if (!_isActivated)
        {
            throw new InvalidOperationException("AI-007 photobooth driver must be activated before scenarios run.");
        }
    }

    private CharacterHub RequireCharacter(string nodeName) => this.RequireNode<CharacterHub>($"../Subject/{nodeName}");
}
