using System.Collections.ObjectModel;
using AlleyCat.Core.Threading;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using Godot;
using Microsoft.Extensions.AI;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.Mind.AI.Watch;

/// <summary>Direct-child, Mind-scoped runtime registry for bounded authorable watches.</summary>
[GlobalClass]
public sealed partial class WatchRegistry : Node
{
    /// <summary>Maximum number of watches that may remain armed for one Mind session.</summary>
    public const int DefaultMaximumActiveWatches = 32;

    private readonly Lock _stateLock = new();
    private readonly SortedDictionary<long, WatchRuntime> _active = [];
    private MindBase? _mind;
    private long _nextWatchID = 1;
    private long _sessionEpoch;
    private bool _sessionBound;

    /// <summary>Authorable typed condition tools exposed by this Mind's session.</summary>
    [Export]
    public Godot.Collections.Array<WatchConditionTool> Conditions { get; set; } = [];

    /// <summary>Maximum simultaneously active watches. This is validated only at session composition.</summary>
    [Export(PropertyHint.Range, "1,256,1")]
    public int MaximumActiveWatches { get; set; } = DefaultMaximumActiveWatches;

    /// <inheritdoc />
    public override void _ExitTree() => EndSession();

    /// <summary>Builds all condition functions after typed session-only binding.</summary>
    internal IReadOnlyList<AITool> BindSessionAndCreateTools(
        ScenarioContext context,
        MindBase mind,
        IMainThreadDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mind);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (GetParent() != mind)
        {
            throw new InvalidOperationException("WatchRegistry must be a direct child of the AgenticMind it serves.");
        }

        if (MaximumActiveWatches is < 1 or > DefaultMaximumActiveWatches)
        {
            throw new InvalidOperationException(
                $"WatchRegistry maximum active watches must be between 1 and {DefaultMaximumActiveWatches}.");
        }

        List<WatchConditionTool> conditions = [];
        HashSet<string> conditionIds = new(StringComparer.Ordinal);
        HashSet<string> toolNames = new(StringComparer.Ordinal);
        foreach (WatchConditionTool? condition in Conditions)
        {
            if (condition is null)
            {
                throw new InvalidOperationException("WatchRegistry conditions cannot contain null resources.");
            }

            if (string.IsNullOrWhiteSpace(condition.ConditionId)
                || !string.Equals(condition.ConditionId, condition.ConditionId.Trim(), StringComparison.Ordinal)
                || !conditionIds.Add(condition.ConditionId))
            {
                throw new InvalidOperationException("WatchRegistry condition IDs must be unique non-empty exact strings.");
            }

            if (string.IsNullOrWhiteSpace(condition.ToolName) || !toolNames.Add(condition.ToolName.Trim()))
            {
                throw new InvalidOperationException("WatchRegistry condition tool names must be unique non-empty exact strings.");
            }

            conditions.Add(condition);
        }

        lock (_stateLock)
        {
            if (_sessionBound)
            {
                throw new InvalidOperationException("WatchRegistry is already bound to an active Mind session.");
            }

            _mind = mind;
            _sessionBound = true;
            _sessionEpoch++;
        }

        mind.RetainedObservationChanged += OnRetainedObservationChanged;
        try
        {
            List<AITool> tools = new(conditions.Count);
            foreach (WatchConditionTool condition in conditions)
            {
                tools.Add(condition.CreateWatchFunction(context, mind, dispatcher, this));
            }

            return new ReadOnlyCollection<AITool>(tools);
        }
        catch
        {
            EndSession();
            throw;
        }
    }

    /// <summary>Arms a condition immediately, initialising it from retained accepted observations without emitting an event.</summary>
    internal AgentToolResult Arm(WatchRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        MindBase mind = GetBoundMind();
        IReadOnlyList<AcceptedObservationEntry> retained = mind.GetRetainedObservationSnapshot();
        string watchId;
        lock (_stateLock)
        {
            EnsureSessionBound();
            if (_active.Count >= MaximumActiveWatches)
            {
                return new AgentToolResult($"Cannot arm another watch: this Mind already has {MaximumActiveWatches} active watches.");
            }

            long numericId = _nextWatchID++;
            watchId = $"w{numericId}";
            runtime.Initialise(retained);
            _active.Add(numericId, runtime);
        }

        WatchStatusSnapshot snapshot = runtime.Snapshot(watchId);
        return new AgentToolResult(runtime.DescribeArmResult(snapshot));
    }

    /// <summary>Removes one active watch without producing a semantic observation.</summary>
    internal AgentToolResult Unwatch(string watchId)
    {
        if (string.IsNullOrWhiteSpace(watchId))
        {
            return new AgentToolResult("No watch ID was supplied; no watch was removed.");
        }

        lock (_stateLock)
        {
            if (!_sessionBound || !TryParseWatchId(watchId, out long numericId) || !_active.Remove(numericId))
            {
                return new AgentToolResult($"No active watch with ID '{watchId}' exists; nothing was removed.");
            }
        }

        return new AgentToolResult($"Removed watch {watchId}.");
    }

    /// <summary>Returns an immutable deterministically ordered active-watch snapshot.</summary>
    public IReadOnlyList<WatchStatusSnapshot> GetActiveWatchSnapshot()
    {
        lock (_stateLock)
        {
            List<WatchStatusSnapshot> snapshot = new(_active.Count);
            foreach ((long id, WatchRuntime runtime) in _active)
            {
                snapshot.Add(runtime.Snapshot($"w{id}"));
            }

            return new ReadOnlyCollection<WatchStatusSnapshot>(snapshot);
        }
    }

    /// <summary>Unsubscribes and clears all active state at session fatal end or Mind exit.</summary>
    internal void EndSession()
    {
        MindBase? mind;
        lock (_stateLock)
        {
            mind = _mind;
            _mind = null;
            _sessionBound = false;
            _sessionEpoch++;
            _active.Clear();
        }

        if (mind is not null)
        {
            mind.RetainedObservationChanged -= OnRetainedObservationChanged;
        }
    }

    private void OnRetainedObservationChanged(RetainedObservationChange change)
    {
        lock (_stateLock)
        {
            MindBase? mind = _mind;
            if (!_sessionBound || mind is null || mind.HasNodeLifetimeEnded)
            {
                return;
            }

            long sessionEpoch = _sessionEpoch;
            IReadOnlyList<AcceptedObservationEntry> retained = mind.GetRetainedObservationSnapshot();
            foreach ((_, WatchRuntime runtime) in _active)
            {
                AgentObservation? observation = runtime.Evaluate(change, retained);
                if (observation is not null)
                {
                    // Keep enqueue admission under the registry lock. EndSession cannot detach the source between
                    // evaluation and queueing; the source-neutral queue guard drops any item deferred past teardown.
                    mind.EnqueueObservation(observation, () => IsSessionCurrent(mind, sessionEpoch));
                }
            }
        }
    }

    private bool IsSessionCurrent(MindBase mind, long sessionEpoch)
    {
        lock (_stateLock)
        {
            return _sessionBound
                && _sessionEpoch == sessionEpoch
                && ReferenceEquals(_mind, mind)
                && !mind.HasNodeLifetimeEnded;
        }
    }

    private MindBase GetBoundMind()
    {
        lock (_stateLock)
        {
            EnsureSessionBound();
            return _mind!;
        }
    }

    private void EnsureSessionBound()
    {
        if (!_sessionBound || _mind is null)
        {
            throw new InvalidOperationException("WatchRegistry is not bound to an active AgenticMind session.");
        }
    }

    private static bool TryParseWatchId(string watchId, out long numericId)
    {
        numericId = 0;
        return watchId.Length > 1
            && watchId[0] == 'w'
            && long.TryParse(watchId.AsSpan(1), out numericId)
            && numericId > 0;
    }
}
