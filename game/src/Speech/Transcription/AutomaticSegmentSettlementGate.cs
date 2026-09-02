namespace AlleyCat.Speech.Transcription;

/// <summary>Orders concurrent automatic segment outcomes within one speech group.</summary>
internal sealed class AutomaticSegmentSettlementGate
{
    private readonly Dictionary<int, AutomaticSegmentOutcome> _pending = [];

    public int DispatchedSegments
    {
        get;
        private set;
    }

    public int NextExpectedSegmentIndex
    {
        get;
        private set;
    }

    public bool Closed
    {
        get;
        private set;
    }

    public bool Abandoned
    {
        get;
        private set;
    }

    public bool IsFullySettled => Closed && NextExpectedSegmentIndex == DispatchedSegments;

    public void RegisterDispatch()
    {
        if (Abandoned)
        {
            return;
        }

        DispatchedSegments++;
    }

    public void Close() => Closed = true;

    public void Abandon()
    {
        Abandoned = true;
        _pending.Clear();
    }

    public IReadOnlyList<AutomaticSegmentSettlement> Settle(int segmentIndex, AutomaticSegmentOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (Abandoned)
        {
            return [];
        }

        _pending.Add(segmentIndex, outcome);
        List<AutomaticSegmentSettlement>? settled = null;
        while (_pending.Remove(NextExpectedSegmentIndex, out AutomaticSegmentOutcome? next))
        {
            settled ??= [];
            settled.Add(new(NextExpectedSegmentIndex++, next));
        }

        return settled ?? [];
    }
}

/// <summary>One ordered automatic segment settlement.</summary>
internal sealed record AutomaticSegmentSettlement(int SegmentIndex, AutomaticSegmentOutcome Outcome);

/// <summary>Backend result retained until its group order permits publication.</summary>
internal sealed record AutomaticSegmentOutcome(string? Text, Exception? Exception);
