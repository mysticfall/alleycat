namespace AlleyCat.Speech.Transcription;

/// <summary>States of the sample-indexed automatic segment and group lifecycle.</summary>
public enum AutomaticUtteranceState
{
    /// <inheritdoc/>
    Monitoring,
    /// <inheritdoc/>
    Candidate,
    /// <inheritdoc/>
    CapturingSegment,
    /// <inheritdoc/>
    AwaitingContinuation,
    /// <inheritdoc/>
    RearmSuppressed,
}

/// <summary>Describes a meaningful automatic segment or group transition.</summary>
public enum AutomaticUtteranceAction
{
    /// <inheritdoc/>
    None,
    /// <inheritdoc/>
    Started,
    /// <inheritdoc/>
    Endpointed,
    /// <inheritdoc/>
    Continued,
    /// <inheritdoc/>
    Closed,
    /// <inheritdoc/>
    ForceClosed,
    /// <inheritdoc/>
    Rearmed,
}

/// <summary>
/// One item in an ordered set of automatic lifecycle transitions. Whole-frame VAD decisions cannot report an
/// intra-frame onset or offset, but a silent frame can yield ordered endpoint and group-close actions.
/// </summary>
public readonly record struct AutomaticUtteranceTransition(
    AutomaticUtteranceAction Action,
    AutomaticUtteranceState State,
    Guid? SpeechGroupID,
    int? SegmentIndex,
    long? SegmentStartSample,
    long? SegmentEndSample)
{
    /// <summary>Gets whether this transition identifies a continued segment.</summary>
    public bool Continued => SegmentIndex > 0;

    /// <summary>Compatibility alias for the historical group start field.</summary>
    public long? UtteranceStartSample => SegmentStartSample;

    /// <summary>Compatibility alias for the historical close field.</summary>
    public long? UtteranceEndSample => SegmentEndSample;
}

/// <summary>
/// Ordered transitions returned for one detection frame. The scalar members retain source compatibility for callers
/// which only need the first transition; new callers must enumerate every item.
/// </summary>
public sealed class AutomaticUtteranceTransitions : IReadOnlyList<AutomaticUtteranceTransition>
{
    private readonly AutomaticUtteranceTransition[] _items;

    internal AutomaticUtteranceTransitions(params AutomaticUtteranceTransition[] items)
    {
        _items = items;
    }

    /// <inheritdoc/>
    public int Count => _items.Length;

    /// <inheritdoc/>
    public AutomaticUtteranceTransition this[int index] => _items[index];

    /// <inheritdoc/>
    public AutomaticUtteranceAction Action => Count == 0 ? AutomaticUtteranceAction.None : _items[0].Action;

    /// <inheritdoc/>
    public AutomaticUtteranceState State => Count == 0 ? AutomaticUtteranceState.Monitoring : _items[^1].State;

    /// <inheritdoc/>
    public long? UtteranceStartSample => Count == 0 ? null : _items[0].UtteranceStartSample;

    /// <inheritdoc/>
    public long? UtteranceEndSample => Count == 0 ? null : _items[0].UtteranceEndSample;

    /// <inheritdoc/>
    public IEnumerator<AutomaticUtteranceTransition> GetEnumerator() => ((IEnumerable<AutomaticUtteranceTransition>)_items).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();
}

/// <summary>Coordinates automatic segment and group timing exclusively on the 16 kHz sample clock.</summary>
public sealed class AutomaticUtteranceCoordinator
{
    private readonly long _minimumVoicedSamples;
    private readonly long _endpointSilenceSamples;
    private readonly long _continuationGapSamples;
    private readonly long _maximumDurationSamples;
    private readonly long _rearmSilenceSamples;
    private long _candidateStartSample;
    private long _groupStartSample;
    private long _segmentStartSample;
    private long _latestVoicedEndSample;
    private long _rearmSilenceStartSample;
    private long _nextFrameStartSample = -1;
    private Guid _speechGroupID;
    private int _segmentIndex;

    /// <inheritdoc/>
    public AutomaticUtteranceCoordinator(int sampleRate, AutomaticVoiceInputOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentNullException.ThrowIfNull(options);
        _minimumVoicedSamples = ToSamples(options.MinimumVoicedDuration, sampleRate);
        _endpointSilenceSamples = ToSamples(options.EndpointSilence, sampleRate);
        _continuationGapSamples = ToSamples(options.ContinuationGap, sampleRate);
        _maximumDurationSamples = ToSamples(options.MaximumUtteranceDuration, sampleRate);
        _rearmSilenceSamples = ToSamples(options.RearmSilence ?? options.EndpointSilence, sampleRate);
    }

    /// <inheritdoc/>
    public AutomaticUtteranceState State
    {
        get; private set;
    }

    /// <summary>Processes a contiguous whole-frame VAD decision and returns reachable boundaries in causal order.</summary>
    public AutomaticUtteranceTransitions ProcessFrame(long startSample, int sampleCount, VoiceActivityDetection detection)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startSample);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        if (_nextFrameStartSample >= 0 && startSample != _nextFrameStartSample)
        {
            throw new ArgumentOutOfRangeException(nameof(startSample), "Frames must be strictly contiguous.");
        }

        long endSample = checked(startSample + sampleCount);
        _nextFrameStartSample = endSample;
        var transitions = new List<AutomaticUtteranceTransition>(3);
        ProcessFrameCore(startSample, endSample, detection, transitions);
        return new AutomaticUtteranceTransitions([.. transitions]);
    }

    /// <inheritdoc/>
    public void Reset()
    {
        State = AutomaticUtteranceState.Monitoring;
        _candidateStartSample = 0;
        _groupStartSample = 0;
        _segmentStartSample = 0;
        _latestVoicedEndSample = 0;
        _rearmSilenceStartSample = 0;
        _nextFrameStartSample = -1;
        _speechGroupID = Guid.Empty;
        _segmentIndex = 0;
    }

    private void ProcessFrameCore(
        long startSample,
        long endSample,
        VoiceActivityDetection detection,
        List<AutomaticUtteranceTransition> transitions)
    {
        if (State == AutomaticUtteranceState.RearmSuppressed)
        {
            if (detection.IsVoiced)
            {
                _rearmSilenceStartSample = endSample;
            }
            else if (endSample - _rearmSilenceStartSample >= _rearmSilenceSamples)
            {
                State = AutomaticUtteranceState.Monitoring;
                transitions.Add(new(AutomaticUtteranceAction.Rearmed, State, null, null, null, null));
            }

            return;
        }

        if (State is AutomaticUtteranceState.CapturingSegment or AutomaticUtteranceState.AwaitingContinuation)
        {
            long maximumDeadline = checked(_groupStartSample + _maximumDurationSamples);
            if (startSample >= maximumDeadline || endSample >= maximumDeadline)
            {
                ForceClose(maximumDeadline, endSample, transitions);
                return;
            }

            long endpointDeadline = checked(_latestVoicedEndSample + _endpointSilenceSamples);
            long continuationDeadline = checked(_latestVoicedEndSample + _continuationGapSamples);

            if (State == AutomaticUtteranceState.CapturingSegment)
            {
                if (detection.IsVoiced)
                {
                    _latestVoicedEndSample = endSample;
                    return;
                }

                if (endSample >= endpointDeadline)
                {
                    Endpoint(endpointDeadline, transitions);
                    if (endSample >= continuationDeadline)
                    {
                        CloseGroup(continuationDeadline, transitions);
                    }

                    return;
                }

                return;
            }

            // A voiced boundary belongs to this group only when its frame starts strictly before the deadline.
            if (detection.IsVoiced && startSample < continuationDeadline)
            {
                Resume(startSample, endSample, transitions);
                return;
            }

            if (!detection.IsVoiced && endSample >= continuationDeadline)
            {
                CloseGroup(continuationDeadline, transitions);
            }

            return;
        }

        if (State == AutomaticUtteranceState.Monitoring && detection.IsVoiced)
        {
            _candidateStartSample = startSample;
            State = AutomaticUtteranceState.Candidate;
        }

        if (State != AutomaticUtteranceState.Candidate)
        {
            return;
        }

        if (!detection.IsVoiced)
        {
            State = AutomaticUtteranceState.Monitoring;
        }
        else if (endSample - _candidateStartSample >= _minimumVoicedSamples)
        {
            _speechGroupID = Guid.NewGuid();
            _groupStartSample = _candidateStartSample;
            _segmentStartSample = _candidateStartSample;
            _latestVoicedEndSample = endSample;
            _segmentIndex = 0;
            State = AutomaticUtteranceState.CapturingSegment;
            transitions.Add(new(AutomaticUtteranceAction.Started, State, _speechGroupID, _segmentIndex, _segmentStartSample, null));
        }
    }

    private void Endpoint(long deadline, List<AutomaticUtteranceTransition> transitions)
    {
        State = AutomaticUtteranceState.AwaitingContinuation;
        transitions.Add(new(AutomaticUtteranceAction.Endpointed, State, _speechGroupID, _segmentIndex, _segmentStartSample, deadline));
    }

    private void Resume(long startSample, long endSample, List<AutomaticUtteranceTransition> transitions)
    {
        _segmentIndex++;
        _segmentStartSample = startSample;
        _latestVoicedEndSample = endSample;
        State = AutomaticUtteranceState.CapturingSegment;
        transitions.Add(new(AutomaticUtteranceAction.Continued, State, _speechGroupID, _segmentIndex, _segmentStartSample, null));
    }

    private void CloseGroup(long deadline, List<AutomaticUtteranceTransition> transitions)
    {
        Guid groupID = _speechGroupID;
        State = AutomaticUtteranceState.Monitoring;
        transitions.Add(new(AutomaticUtteranceAction.Closed, State, groupID, null, _groupStartSample, deadline));
        _speechGroupID = Guid.Empty;
    }

    private void ForceClose(long deadline, long processedFrameEnd, List<AutomaticUtteranceTransition> transitions)
    {
        Guid groupID = _speechGroupID;
        if (State == AutomaticUtteranceState.CapturingSegment)
        {
            transitions.Add(new(AutomaticUtteranceAction.ForceClosed, AutomaticUtteranceState.RearmSuppressed, groupID, _segmentIndex, _segmentStartSample, deadline));
        }

        State = AutomaticUtteranceState.RearmSuppressed;
        _rearmSilenceStartSample = processedFrameEnd;
        transitions.Add(new(AutomaticUtteranceAction.Closed, State, groupID, null, _groupStartSample, deadline));
        _speechGroupID = Guid.Empty;
    }

    private static long ToSamples(TimeSpan duration, int sampleRate) => checked((long)Math.Ceiling(duration.TotalSeconds * sampleRate));
}
