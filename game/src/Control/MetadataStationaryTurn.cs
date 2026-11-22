using AlleyCat.Motion;

namespace AlleyCat.Control;

/// <summary>Player-owned sequence of angular cycles; a submitted lift and its allocation stay immutable.</summary>
internal sealed class MetadataStationaryTurn(StationaryTurnReference reference, double initialYaw)
{
    internal sealed record Snapshot(long Epoch, long Sequence, string Role, int WindowStart, int LocalPhase,
        string Foot, double Magnitude, double Allocation, double CycleStart, double Scale, double RootYaw, int ServiceCycle, AngularTurnInterval Playback)
    {
        public string EventId { get; } = "lift_" + Foot;
    }

    internal StationaryTurnReference Reference => reference;
    internal long Epoch
    {
        get; private set;
    }
    internal Snapshot? Owner
    {
        get; private set;
    }
    internal int Cycle
    {
        get; private set;
    }
    internal int EventIndex
    {
        get; private set;
    }
    private double _start = initialYaw, _magnitude;
    private AngularTurnCycle _mapping, _previous;
    private long _sequence;

    internal void Retire()
    {
        Epoch++;
        Owner = null;
    }

    internal Snapshot Select(double magnitude)
    {
        if (Owner is not null || !double.IsFinite(magnitude) || magnitude <= 0 || magnitude > 1)
        {
            throw new InvalidOperationException("Invalid stationary-turn submission opportunity.");
        }
        if (EventIndex == 0)
        {
            _previous = _mapping;
            _magnitude = magnitude;
            _mapping = AngularTurnCycle.Allocate(reference, _start, magnitude * AngularTurnCycle.Maximum);
        }
        StationaryTurnReference.FootEvent foot = reference.Feet[EventIndex];
        int phase = foot.Frame - reference.First;
        int from = EventIndex > 0 ? reference.Feet[EventIndex - 1].Frame - reference.First
            : Cycle == 0 ? 0 : reference.Feet[^1].Frame - reference.First - reference.Period;
        Owner = new(Epoch, ++_sequence, reference.Role, checked(foot.Frame + (Cycle * reference.Period)), phase,
            foot.Foot, _magnitude, _mapping.Allocation, _start, _mapping.Scale, _mapping.YawAt(phase), Cycle, new(_previous, _mapping, from, phase));
        return Owner;
    }

    internal bool Complete(Snapshot snapshot)
    {
        if (!ReferenceEquals(Owner, snapshot) || snapshot.Epoch != Epoch)
        {
            return false;
        }
        Owner = null;
        if (++EventIndex == reference.Feet.Count)
        {
            _start += _mapping.Allocation;
            Cycle++;
            EventIndex = 0;
        }
        return true;
    }
}
