using AlleyCat.Motion;

namespace AlleyCat.Navigation;

/// <summary>Finite source-angle allocation; playback and realised-context ownership remain with NAV.</summary>
internal sealed class FiniteTerminalTurnPlan
{
    internal sealed record Cue(int Index, int Cycle, int Frame, string Foot, double Allocation, double CycleStart,
        double Scale, int LocalPhase, int VirtualFrame, double MappedWorldYaw);
    internal IReadOnlyList<double> Allocations
    {
        get;
    }
    internal IReadOnlyList<Cue> Cues
    {
        get;
    }
    internal string Role
    {
        get;
    }
    internal double StartingWorldYaw
    {
        get;
    }
    internal double SignedAngle
    {
        get;
    }
    internal double SourceSignedSpan
    {
        get;
    }
    internal string SourceSHA256
    {
        get;
    }
    internal int Served
    {
        get; private set;
    }
    internal bool FinishedCues => Served == Cues.Count;
    internal Cue Current => Cues[Served];

    internal FiniteTerminalTurnPlan(StationaryTurnReference reference, double startingWorldYaw, double signedAngle, string sourceSHA256 = "")
    {
        if (!double.IsFinite(startingWorldYaw + signedAngle) || signedAngle * reference.Sign <= 0 || Math.Abs(signedAngle) > Math.PI + 1e-6)
        {
            throw new ArgumentOutOfRangeException(nameof(signedAngle));
        }
        Role = reference.Role;
        StartingWorldYaw = startingWorldYaw;
        SignedAngle = signedAngle;
        SourceSignedSpan = reference.SignedSpan;
        SourceSHA256 = sourceSHA256;
        var allocations = new List<double>();
        var cues = new List<Cue>();
        double remaining = Math.Abs(signedAngle), start = startingWorldYaw;
        while (remaining > 0)
        {
            var mapping = AngularTurnCycle.Allocate(reference, start, remaining);
            double allocation = mapping.Allocation;
            int cycle = allocations.Count;
            allocations.Add(allocation);
            foreach (StationaryTurnReference.FootEvent foot in reference.Feet.OrderBy(f => f.Frame))
            {
                int phase = foot.Frame - reference.First;
                cues.Add(new(cues.Count, cycle, foot.Frame, foot.Foot, allocation, start, mapping.Scale, phase,
                    foot.Frame + (cycle * reference.Period), mapping.YawAt(phase)));
            }
            start += allocation;
            remaining -= Math.Abs(allocation);
        }
        Allocations = allocations.AsReadOnly();
        Cues = cues.AsReadOnly();
    }

    internal AngularTurnInterval Playback(StationaryTurnReference reference)
    {
        Cue cue = FinishedCues ? Cues[^1] : Current;
        AngularTurnCycle current = new(reference, cue.CycleStart, cue.Allocation);
        if (FinishedCues)
        {
            return new(current, current, cue.LocalPhase, reference.Period);
        }
        if (Served == 0)
        {
            return new(current, current, 0, cue.LocalPhase);
        }
        Cue last = Cues[Served - 1];
        return new(new(reference, last.CycleStart, last.Allocation), current,
            last.LocalPhase - ((cue.Cycle - last.Cycle) * reference.Period), cue.LocalPhase);
    }

    internal void Service() => Served++;
}
