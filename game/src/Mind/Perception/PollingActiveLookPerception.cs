using AlleyCat.Core.Logging;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.Perception;

/// <summary>
/// Active-look perception base that periodically re-examines the attached active subject.
/// </summary>
public abstract partial class PollingActiveLookPerception : ActiveLookPerception
{
    private double _pollAccumulator;
    private bool _pollInFlight;
    private ILogger<PollingActiveLookPerception>? _logger;

    /// <summary>
    /// Gets or sets the delay in seconds between periodic re-examinations of the active subject. The value must be
    /// finite and greater than zero; it is validated when a subject attaches, before polling starts.
    /// </summary>
    [Export]
    public double PollIntervalSeconds
    {
        get;
        set;
    } = 2d;

    /// <summary>
    /// Accumulates frame time and starts at most one poll per frame while a live subject is attached. A delayed
    /// frame performs exactly one poll and the interval restarts from that poll without catch-up.
    /// </summary>
    public override void _Process(double delta)
    {
        base._Process(delta);

        IVisualSubject? subject = ActiveSubject;
        PerceptionContext? context = LatestContext;
        VisualCue? cue = ActiveCue;
        if (_pollInFlight
            || !HasValidPollInterval
            || subject is null
            || context is null
            || cue is null
            || !IsLiveCue(cue)
            || FindNearestSubject(cue) is not IVisualSubject nearest
            || !ReferenceEquals(subject, nearest))
        {
            return;
        }

        _pollAccumulator += delta;
        if (_pollAccumulator < PollIntervalSeconds)
        {
            return;
        }

        _pollAccumulator = 0d;
        StartPoll(subject, context);
    }

    /// <summary>Re-examines the supplied active subject as one periodic poll.</summary>
    protected abstract ValueTask PollActiveSubjectAsync(
        IVisualSubject subject,
        PerceptionContext context,
        CancellationToken cancellationToken);

    /// <inheritdoc />
    protected override void OnActiveSubjectAttached(IVisualSubject subject)
    {
        base.OnActiveSubjectAttached(subject);
        ValidatePollInterval();
        _pollAccumulator = 0d;
    }

    /// <inheritdoc />
    protected override void OnActiveSubjectDetached(IVisualSubject subject)
    {
        base.OnActiveSubjectDetached(subject);
        _pollAccumulator = 0d;
    }

    private bool HasValidPollInterval => double.IsFinite(PollIntervalSeconds) && PollIntervalSeconds > 0d;

    private void ValidatePollInterval()
    {
        if (!HasValidPollInterval)
        {
            throw new InvalidOperationException(
                $"'{GetType().Name}.PollIntervalSeconds' must be a finite value greater than zero; received '{PollIntervalSeconds}'.");
        }
    }

    private void StartPoll(IVisualSubject subject, PerceptionContext context)
    {
        _pollInFlight = true;
        _ = PollAsync(subject, context, ActivationToken);
    }

    private async Task PollAsync(IVisualSubject subject, PerceptionContext context, CancellationToken cancellationToken)
    {
        try
        {
            await PollActiveSubjectAsync(subject, context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on clear, replacement, cancellation, and exit; polling simply stops.
        }
        catch (Exception exception)
        {
            LogPollFault(exception);
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    private void LogPollFault(Exception exception)
    {
        ILogger<PollingActiveLookPerception>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Error) == true)
        {
            logger.LogError(
                exception,
                "Periodic examination of the active subject failed for perception node '{Path}'.",
                GetPath());
        }
    }

    private ILogger<PollingActiveLookPerception>? TryGetLogger()
    {
        if (_logger is null && GameLoggerResolver.TryResolve(out ILogger<PollingActiveLookPerception>? logger))
        {
            _logger = logger;
        }

        return _logger;
    }
}
