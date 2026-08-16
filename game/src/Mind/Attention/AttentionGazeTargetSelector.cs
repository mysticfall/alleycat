using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Logging;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.Attention;

/// <summary>
/// Adapts this Mind's current attention snapshot into stable, cue-owned gaze assignments for its owning character.
/// </summary>
/// <remarks>
/// The periodic trigger is deliberately isolated from the policy evaluation path. A future perception or attention
/// subscriber can call <see cref="RequestEvaluation"/> without bypassing dwell state or creating an interrupt path.
/// </remarks>
[GlobalClass]
public partial class AttentionGazeTargetSelector : Node
{
    private Func<ISceneContext> _sceneContextLoader = static ()
        => Game.Instance.GetRequiredService<ISceneContextProvider>().GetCurrent();
    private IAttentionGazeRandom _random = SharedAttentionGazeRandom.Instance;
    private Mind? _mind;
    private ICharacter? _character;
    private IComponentProjectionNotifier? _componentProjectionNotifier;
    private AttentionGazeTargetSettings? _settings;
    private AttentionGazeTargetPolicy<VisualCue>? _policy;
    private IVision? _vision;
    private ILogger<AttentionGazeTargetSelector>? _logger;
    private double _secondsSinceLastEvaluation;
    private bool _isReady;
    private bool _initialEvaluationPending;
    private bool _evaluationRequested;

    /// <summary>
    /// Gets or sets the initial periodic interval used to request normal gaze-policy evaluations. A future event-based
    /// trigger may request the same evaluation path between these intervals.
    /// </summary>
    [ExportGroup("Cadence")]
    [Export(PropertyHint.Range, "0.01,60,0.01,or_greater")]
    public float EvaluationIntervalSeconds
    {
        get; set;
    } = 0.5f;

    /// <summary>Gets or sets how long a primary attention-derived cue remains stable before reselection.</summary>
    [ExportGroup("Primary Dwell")]
    [Export(PropertyHint.Range, "0.01,60,0.01,or_greater")]
    public float PrimaryDwellSeconds
    {
        get; set;
    } = 2.0f;

    /// <summary>Gets or sets the probability of taking a secondary glance at a primary dwell boundary.</summary>
    [ExportGroup("Secondary Glance")]
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float SecondaryGlanceProbability
    {
        get; set;
    } = 0.2f;

    /// <summary>Gets or sets the shorter dwell duration for an optional secondary glance.</summary>
    [Export(PropertyHint.Range, "0.01,60,0.01,or_greater")]
    public float SecondaryDwellSeconds
    {
        get; set;
    } = 0.5f;

    /// <inheritdoc />
    public override void _Ready()
    {
        _settings = CreateValidatedSettings();
        _mind = GetParent() as Mind
            ?? throw new InvalidOperationException(
                $"Attention gaze selector '{GetPath()}' requires a direct {nameof(Mind)} parent.");
        _character = _mind.OwningCharacter;
        _componentProjectionNotifier = _character as IComponentProjectionNotifier
            ?? throw new InvalidOperationException(
                $"Attention gaze selector '{GetPath()}' requires its owning character '{_character.GetType().FullName}' to implement {nameof(IComponentProjectionNotifier)}.");

        _componentProjectionNotifier.ComponentsRefreshed += OnComponentsRefreshed;
        _isReady = true;
        _initialEvaluationPending = true;

        if (_componentProjectionNotifier.HasComponentProjection)
        {
            BindVisionAfterProjection();
        }
        else
        {
            LogWaitingForProjection();
        }
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (!_isReady || _vision is null || _policy is null)
        {
            return;
        }

        if (!double.IsFinite(delta) || delta < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delta),
                delta,
                "Attention gaze selector process delta must be finite and non-negative.");
        }

        if (_initialEvaluationPending)
        {
            _initialEvaluationPending = false;
            _evaluationRequested = false;
            EvaluatePolicy(0d);
            return;
        }

        _secondsSinceLastEvaluation += delta;
        if (!_evaluationRequested && _secondsSinceLastEvaluation < EvaluationIntervalSeconds)
        {
            return;
        }

        double evaluationDelta = _secondsSinceLastEvaluation;
        _secondsSinceLastEvaluation = 0d;
        _evaluationRequested = false;
        EvaluatePolicy(evaluationDelta);
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_componentProjectionNotifier is { } componentProjectionNotifier)
        {
            componentProjectionNotifier.ComponentsRefreshed -= OnComponentsRefreshed;
            _componentProjectionNotifier = null;
        }

        _vision = null;
        _policy = null;
        _character = null;
        _mind = null;
        _isReady = false;
        _initialEvaluationPending = false;
        _evaluationRequested = false;
        _secondsSinceLastEvaluation = 0d;
    }

    /// <summary>
    /// Requests one normal evaluation on the next process callback. This does not interrupt or otherwise alter policy
    /// dwell state; its elapsed process time is passed to the same policy path as periodic evaluations.
    /// </summary>
    public void RequestEvaluation()
    {
        if (_isReady)
        {
            _evaluationRequested = true;
        }
    }

    /// <summary>Replaces scene-context resolution before activation for focused runtime coverage.</summary>
    internal void SetSceneContextLoaderForTesting(Func<ISceneContext> sceneContextLoader)
    {
        ArgumentNullException.ThrowIfNull(sceneContextLoader);
        ThrowIfActivatedForTesting(nameof(SetSceneContextLoaderForTesting));
        _sceneContextLoader = sceneContextLoader;
    }

    /// <summary>Replaces secondary-glance randomness before activation for deterministic focused runtime coverage.</summary>
    internal void SetRandomForTesting(IAttentionGazeRandom random)
    {
        ArgumentNullException.ThrowIfNull(random);
        ThrowIfActivatedForTesting(nameof(SetRandomForTesting));
        _random = random;
    }

    private AttentionGazeTargetSettings CreateValidatedSettings()
    {
        if (!float.IsFinite(EvaluationIntervalSeconds) || EvaluationIntervalSeconds <= 0f)
        {
            throw new InvalidOperationException(
                $"Attention gaze selector '{GetPath()}' requires {nameof(EvaluationIntervalSeconds)} to be finite and positive, but found '{EvaluationIntervalSeconds}'.");
        }

        try
        {
            return new AttentionGazeTargetSettings(
                PrimaryDwellSeconds,
                SecondaryDwellSeconds,
                SecondaryGlanceProbability);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException(
                $"Attention gaze selector '{GetPath()}' has invalid dwell or secondary-glance authoring.",
                exception);
        }
    }

    private void OnComponentsRefreshed()
    {
        if (_isReady)
        {
            BindVisionAfterProjection();
        }
    }

    private void BindVisionAfterProjection()
    {
        ICharacter character = _character
            ?? throw new InvalidOperationException("Attention gaze selector has no resolved owning character after activation.");
        IVision vision = character.RequireVision();
        if (ReferenceEquals(_vision, vision))
        {
            LogProjectionRefreshWithoutVisionChange();
            return;
        }

        _vision = vision;
        _policy = new AttentionGazeTargetPolicy<VisualCue>(
            _settings ?? throw new InvalidOperationException("Attention gaze selector settings were not initialised."),
            _random);
        _initialEvaluationPending = true;
        _evaluationRequested = false;
        _secondsSinceLastEvaluation = 0d;
        LogVisionBound(vision);
    }

    private void EvaluatePolicy(double deltaSeconds)
    {
        Mind mind = _mind
            ?? throw new InvalidOperationException("Attention gaze selector has no Mind parent after activation.");
        ISceneContext scene = _sceneContextLoader();
        AttentionSnapshot snapshot = mind.GetAttentionSnapshot();
        List<AttentionGazeTargetCandidate<VisualCue>> candidates = ResolveCandidates(snapshot, scene);
        AttentionGazeTargetDecision<VisualCue> decision = (_policy
            ?? throw new InvalidOperationException("Attention gaze selector policy was not initialised."))
            .Evaluate(deltaSeconds, candidates);
        ApplyDecision(
            _vision ?? throw new InvalidOperationException("Attention gaze selector Vision binding was lost during evaluation."),
            decision);
    }

    private static List<AttentionGazeTargetCandidate<VisualCue>> ResolveCandidates(
        AttentionSnapshot snapshot,
        ISceneContext scene)
    {
        var candidates = new List<AttentionGazeTargetCandidate<VisualCue>>();
        foreach (KeyValuePair<string, float> attention in snapshot.Values)
        {
            if (scene.Find(attention.Key) is not IVisualSubject subject)
            {
                continue;
            }

            IReadOnlyList<VisualCue> cues = subject.VisualCues;
            for (int cueOrder = 0; cueOrder < cues.Count; cueOrder++)
            {
                VisualCue? cue = cues[cueOrder];
                if (cue is null || !IsInstanceValid(cue) || !float.IsFinite(cue.Prominence) || cue.Prominence <= 0f)
                {
                    continue;
                }

                candidates.Add(new AttentionGazeTargetCandidate<VisualCue>(
                    attention.Key,
                    cue.ID,
                    cueOrder,
                    cue,
                    attention.Value,
                    cue.Prominence,
                    IsValid: true));
            }
        }

        return candidates;
    }

    private void ApplyDecision(IVision vision, AttentionGazeTargetDecision<VisualCue> decision)
    {
        switch (decision.Action)
        {
            case AttentionGazeTargetAction.None:
                return;
            case AttentionGazeTargetAction.SetLookTarget:
                VisualCue target = decision.Target
                    ?? throw new InvalidOperationException("Attention gaze target policy requested a target assignment without a target.");
                vision.SetLookTarget(target);
                LogSetDecision(target);
                return;
            case AttentionGazeTargetAction.ClearLookTarget:
                vision.ClearLookTarget();
                LogClearDecision();
                return;
            default:
                throw new InvalidOperationException($"Unknown attention gaze target action '{decision.Action}'.");
        }
    }

    private void ThrowIfActivatedForTesting(string operation)
    {
        if (_isReady)
        {
            throw new InvalidOperationException($"{operation} must be called before the attention gaze selector activates.");
        }
    }

    private void LogWaitingForProjection()
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug("Attention gaze selector is waiting for its owner's component projection.");
        }
    }

    private void LogVisionBound(IVision vision)
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug(
                "Attention gaze selector bound Vision component {VisionType} after component projection.",
                vision.GetType().FullName ?? vision.GetType().Name);
        }
    }

    private void LogProjectionRefreshWithoutVisionChange()
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug("Attention gaze selector observed a component projection refresh without a Vision binding change.");
        }
    }

    private void LogSetDecision(VisualCue target)
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Trace) == true)
        {
            logger.LogTrace("Attention gaze selector applied SetLookTarget for cue {CueId}.", target.ID);
        }
    }

    private void LogClearDecision()
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Trace) == true)
        {
            logger.LogTrace("Attention gaze selector applied ClearLookTarget after its policy found no candidate.");
        }
    }

    private ILogger<AttentionGazeTargetSelector>? TryGetLogger()
    {
        if (_logger is null && GameLoggerResolver.TryResolve(out ILogger<AttentionGazeTargetSelector>? logger))
        {
            _logger = logger;
        }

        return _logger;
    }

    private sealed class SharedAttentionGazeRandom : IAttentionGazeRandom
    {
        public static SharedAttentionGazeRandom Instance
        {
            get;
        } = new();

        public double NextUnitInterval() => Random.Shared.NextDouble();
    }
}
