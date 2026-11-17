using AlleyCat.Motion;

namespace AlleyCat.Control;

/// <summary>Normal CTRL stationary source-landmark policy. Intent time is independent of generated duration.</summary>
internal sealed class MetadataStationaryTurn(StationaryTurnReference reference, double initialYaw)
{
    internal sealed record Snapshot(long Epoch, long Sequence, string Role, double ClockPhase, double ForecastPhase,
        int SelectedPhase, int WindowStart, string EventId, StationaryTurnReference.LandmarkKind EventKind,
        string SubjectFoot, bool IsLandmarkTarget, double Magnitude, double Cap,
        double Scale, double Offset, double ReferenceAngle, double RootYaw, long ServiceCycle, long DroppedCycles, long PassedUnplayed)
    {
        public bool IsPrimaryLift => IsLandmarkTarget && EventKind == StationaryTurnReference.LandmarkKind.PrimaryLift;
        public string? Foot => IsPrimaryLift ? SubjectFoot : null;
        public string? RecoveringFoot => IsLandmarkTarget && EventKind == StationaryTurnReference.LandmarkKind.Recovery ? SubjectFoot : null;
    }

    internal StationaryTurnReference Reference { get; } = reference;
    internal double Phase
    {
        get; private set;
    }
    internal int Floor
    {
        get; private set;
    }
    internal long Cycle
    {
        get; private set;
    }
    internal int EventIndex
    {
        get; private set;
    }
    internal long DroppedCycles
    {
        get; private set;
    }
    internal long PassedUnplayed
    {
        get; private set;
    }
    internal long Epoch { get; private set; } = 1;
    internal long PrimaryLiftCompletions
    {
        get; private set;
    }
    internal long RecoveryCompletions
    {
        get; private set;
    }
    internal long PassedPrimaryLifts
    {
        get; private set;
    }
    internal long PassedRecoveries
    {
        get; private set;
    }
    internal Snapshot? Owner
    {
        get; private set;
    }
    internal double InitialYaw { get; } = initialYaw;
    private double _scale = 1, _offset = reference.Sign * initialYaw, _referencePhase;
    private double _yaw = initialYaw, _lastWrappedYaw = initialYaw;
    private long _sequence;

    internal double TrackRoot(double wrappedYaw)
    {
        if (!double.IsFinite(wrappedYaw))
        {
            throw new ArgumentOutOfRangeException(nameof(wrappedYaw));
        }
        _yaw += Math.IEEERemainder(wrappedYaw - _lastWrappedYaw, Math.Tau);
        _lastWrappedYaw = wrappedYaw;
        return _yaw;
    }

    internal void Advance(double delta, double magnitude)
    {
        if (!double.IsFinite(delta) || delta < 0 || !double.IsFinite(magnitude) || magnitude < 0 || magnitude > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }
        Phase += SourcePhaseClock.FramesPerSecond * magnitude * delta;
    }

    // Retirement preserves same-role clock/cursor/map, but invalidates the immutable owner by identity and epoch.
    internal void Retire()
    {
        Epoch++;
        Owner = null;
    }

    internal Snapshot Select(double magnitude, double horizon, double wrappedRootYaw)
    {
        if (Owner is not null || !double.IsFinite(magnitude) || magnitude <= 0 || magnitude > 1 || !double.IsFinite(horizon) || horizon <= 0)
        {
            throw new InvalidOperationException("Invalid stationary-turn submission opportunity.");
        }
        double actual = Reference.Sign * TrackRoot(wrappedRootYaw);
        double forecast = Phase + (SourcePhaseClock.FramesPerSecond * magnitude * horizon);
        if (forecast + Reference.First + 3 >= int.MaxValue)
        {
            throw new InvalidOperationException("Stationary-turn source phase is not representable.");
        }
        bool passedCue = false;
        // Finish the current group and inspect the next group selected by the existing cycle/drop rule.
        // Never scan arbitrarily many source cycles to repair external tracking overtake.
        int remaining = Reference.Landmarks.Count - EventIndex + Reference.Landmarks.Count;
        for (int inspected = 0; inspected < remaining; inspected++)
        {
            StationaryTurnReference.Landmark landmark = Reference.Landmarks[EventIndex];
            int cue = checked((int)((Cycle * Reference.Period) + landmark.PhaseFrames));
            int selected = Math.Max(Floor, Math.Min(cue, (int)Math.Floor(Math.BitIncrement(forecast))));
            double aRef = (actual - _offset) / _scale;
            double delta = Reference.Angle(selected) - aRef;
            if (passedCue && selected < cue && Reference.Angle(selected) >= Reference.Angle(_referencePhase) && delta < -1e-6)
            {
                // Approved overtake exception only: the intervening forecast window would chase backwards.
                // Select the next ordered cue even beyond the forecast, without moving the intent clock or map.
                selected = cue;
                delta = Reference.Angle(selected) - aRef;
            }
            // Distinguish authored local reverse motion from a forward cue overtaken by actual root movement.
            if (selected == cue && Reference.Angle(selected) >= Reference.Angle(_referencePhase) && delta < -1e-6)
            {
                Floor = selected;
                PassedUnplayed++;
                if (landmark.Kind == StationaryTurnReference.LandmarkKind.PrimaryLift)
                {
                    PassedPrimaryLifts++;
                }
                else
                {
                    PassedRecoveries++;
                }
                passedCue = true;
                AdvanceLandmark();
                continue;
            }
            double cap = Reference.Rate * magnitude * horizon;
            double scale = delta == 0 ? 1 : Math.Min(1, cap / Math.Abs(delta));
            double offset = actual - (scale * aRef);
            Snapshot snapshot = new(Epoch, ++_sequence, Reference.Role, Phase, forecast, selected,
                Reference.First + selected, landmark.Id, landmark.Kind, landmark.SubjectFoot, selected == cue,
                magnitude, cap, scale, offset, aRef, Reference.Sign * ((scale * Reference.Angle(selected)) + offset),
                Cycle, DroppedCycles, PassedUnplayed);
            Floor = selected;
            _scale = scale;
            _offset = offset;
            Owner = snapshot;
            return snapshot;
        }
        throw new InvalidOperationException("No unpassed stationary-turn landmark within the current and next four-event service group; external overtake exceeds the bounded policy.");
    }

    internal bool Complete(Snapshot snapshot, double wrappedRootYaw)
    {
        if (!ReferenceEquals(Owner, snapshot) || snapshot.Epoch != Epoch)
        {
            return false;
        }
        _scale = 1;
        _offset = (Reference.Sign * TrackRoot(wrappedRootYaw)) - Reference.Angle(snapshot.SelectedPhase);
        _referencePhase = snapshot.SelectedPhase;
        Owner = null;
        if (snapshot.IsLandmarkTarget)
        {
            if (snapshot.IsPrimaryLift)
            {
                PrimaryLiftCompletions++;
            }
            else
            {
                RecoveryCompletions++;
            }
            AdvanceLandmark();
        }
        return true;
    }

    private void AdvanceLandmark()
    {
        if (++EventIndex < Reference.Landmarks.Count)
        {
            return;
        }
        long next = Math.Max(Cycle + 1, (long)Math.Floor(Phase / Reference.Period));
        long dropped = next - Cycle - 1;
        _offset -= dropped * Math.Abs(Reference.SignedSpan);
        _referencePhase += dropped * Reference.Period;
        DroppedCycles += dropped;
        Cycle = next;
        EventIndex = 0;
    }
}
