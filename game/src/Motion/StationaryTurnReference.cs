using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlleyCat.Motion;

/// <summary>Bound measured turn metadata. Joint-origin landmarks are not contact observations.</summary>
internal sealed class StationaryTurnReference
{
    [JsonConverter(typeof(JsonStringEnumConverter<LandmarkKind>))]
    internal enum LandmarkKind
    {
        [JsonStringEnumMemberName("primary_lift")]
        PrimaryLift,
        [JsonStringEnumMemberName("recovery")]
        Recovery
    }

    internal sealed record Landmark(string Id, LandmarkKind Kind, string SubjectFoot, int Frame,
        int PhaseFrames, double PhaseSeconds, double Angle);
    internal sealed record FootEvent(string Foot, int Frame, double PhaseSeconds, double Angle,
        double Height, int ToeFrame, double ToeAngle, double ToeHeight);

    private readonly double[] _angles;
    internal string Role
    {
        get;
    }
    internal int First
    {
        get;
    }
    internal int Period
    {
        get;
    }
    internal double SignedSpan
    {
        get;
    }
    internal double Sign => Math.Sign(SignedSpan);
    internal double Rate => Math.Abs(SignedSpan) * SourcePhaseClock.FramesPerSecond / Period;
    internal IReadOnlyList<FootEvent> Feet
    {
        get;
    }
    internal IReadOnlyList<Landmark> Landmarks
    {
        get;
    }

    internal StationaryTurnReference(string role, JsonElement metadata, JsonElement source, string hash, int first, int end)
    {
        Role = role;
        First = first;
        Period = end - first;
        Check(metadata.GetProperty("schemaProposal").GetString() == "stationary_turn_angular_reference_v1" &&
            metadata.GetProperty("sourceBundleSHA256").GetString() == hash &&
            metadata.GetProperty("first").GetInt32() == first && metadata.GetProperty("endExclusive").GetInt32() == end &&
            metadata.GetProperty("periodFrames").GetInt32() == Period && Period > 0 &&
            metadata.GetProperty("sourceFPS").GetInt32() == SourcePhaseClock.FramesPerSecond, "Turn metadata identity/bounds mismatch.");
        Check(metadata.GetProperty("facingConvention").GetString() ==
            "atan2(evaluated canonical Hips Basis.Z.x, Basis.Z.z); +Y up, canonical forward +Z; left positive; apply existing ModelToWorld conversion" &&
            metadata.GetProperty("orderedAngularProgression").GetString() ==
            "Bound full source samples[].heading_radians; unwrap in source order, including holds/reversals. No inverse yaw clock." &&
            metadata.GetProperty("liftMeasurement").GetString() ==
            "canonical FK foot/toe joint origin +Y, including source root; not sole clearance/contact" &&
            metadata.GetProperty("toePeaksAreAnnotationsNotAdditionalEndpointPromises").GetBoolean(), "Unsupported turn measurement convention.");
        SignedSpan = Number(metadata, "measuredSignedSpanRadians");
        double duration = Number(metadata, "durationSeconds"), rate = Number(metadata, "representativeAngularRateRadiansPerSecond");
        Check(Math.Abs(SignedSpan) > 1e-6 && Math.Sign(SignedSpan) == (role == "left_turn" ? 1 : -1) &&
            Math.Abs(duration - (Period / 30.0)) < 1e-6 && Math.Abs(rate - (SignedSpan / duration)) < 1e-6,
            "Turn span/duration/rate mismatch.");
        JsonElement samples = source.GetProperty("samples");
        int offset = samples[0].GetProperty("frame").GetInt32();
        _angles = new double[Period + 1];
        double previous = 0, accumulated = 0;
        for (int i = 0; i <= Period; i++)
        {
            JsonElement sample = samples[first + i - offset];
            Check(sample.GetProperty("frame").GetInt32() == first + i, "Unordered turn samples.");
            double heading = Number(sample, "heading_radians");
            if (i > 0)
            {
                accumulated += Math.IEEERemainder(heading - previous, Math.Tau);
            }
            _angles[i] = accumulated * Sign;
            previous = heading;
        }
        Check(Math.Abs(accumulated - SignedSpan) < 1e-6, "Measured turn span disagrees with source.");
        var feet = new List<FootEvent>();
        foreach (JsonElement item in metadata.GetProperty("primaryFootEvents").EnumerateArray())
        {
            string foot = item.GetProperty("foot").GetString()!;
            int frame = item.GetProperty("frame").GetInt32(), toe = item.GetProperty("associatedToePeakFrame").GetInt32();
            double phase = Number(item, "phaseSeconds"), angle = Number(item, "sourceAngularProgressRadians");
            double height = Number(item, "jointOriginHeightMetres"), toeAngle = Number(item, "associatedToeSourceAngularProgressRadians"),
                toeHeight = Number(item, "associatedToeOriginHeightMetres");
            Check((foot is "LeftFoot" or "RightFoot") && frame >= first && frame < end && toe >= first && toe < end &&
                (feet.Count == 0 || frame > feet[^1].Frame) && feet.All(f => f.Foot != foot), "Invalid ordered turn foot identity/bounds.");
            Check(Math.Abs(phase - ((frame - first) / 30.0)) < 1e-6 &&
                Math.Abs(angle - (Sign * Angle(frame - first))) < 1e-6 && Math.Abs(toeAngle - (Sign * Angle(toe - first))) < 1e-6,
                "Turn foot phase/angular association disagrees with source.");
            feet.Add(new FootEvent(foot, frame, phase, angle, height, toe, toeAngle, toeHeight));
        }
        Check(feet.Count == 2, "Both turn foot cues are required.");
        Feet = feet.AsReadOnly();
        JsonElement trial = metadata.GetProperty("recoveryTrial");
        Check(trial.GetProperty("schema").GetString() == "stationary_turn_recovery_trial_v1" &&
            trial.GetProperty("status").GetString() == "EXPERIMENTAL_NOT_ADMITTED" &&
            trial.GetProperty("sourceBundleSHA256").GetString() == hash &&
            trial.GetProperty("measurementConvention").GetString() == metadata.GetProperty("liftMeasurement").GetString() &&
            trial.GetProperty("primaryFootEventsRemainAuthoritative").GetBoolean() &&
            !string.IsNullOrWhiteSpace(trial.GetProperty("criterion").GetString()), "Unbound/unsupported recovery trial.");
        JsonElement events = trial.GetProperty("events");
        Check(events.GetArrayLength() == 4, "Recovery trial requires four ordered landmarks.");
        var landmarks = new List<Landmark>(4);
        for (int i = 0; i < 4; i++)
        {
            JsonElement item = events[i];
            FootEvent primary = feet[i / 2];
            bool lift = i % 2 == 0;
            string id = (lift ? "lift_" : "recover_after_") + primary.Foot;
            Check(item.GetProperty("id").GetString() == id &&
                item.GetProperty("kind").GetString() == (lift ? "primary_lift" : "recovery") &&
                item.TryGetProperty(lift ? "foot" : "recoveringFoot", out JsonElement subject) && subject.GetString() == primary.Foot &&
                !item.TryGetProperty(lift ? "recoveringFoot" : "foot", out _), "Invalid landmark identity/kind/foot relationship.");
            int frame = item.GetProperty("frame").GetInt32(), phase = item.GetProperty("phaseFrames").GetInt32();
            double seconds = Number(item, "phaseSeconds"), angle = Number(item, "sourceAngularProgressRadians");
            Check(frame >= first && frame < end && phase == frame - first &&
                (i == 0 || phase > landmarks[^1].PhaseFrames), "Invalid landmark source order/bounds.");
            Check(lift ? frame == primary.Frame : frame > primary.Frame &&
                frame < (i == 1 ? feet[1].Frame : feet[0].Frame + Period) && frame + 3 < end,
                "Recovery must lie strictly between lifts with a seam-free original four-pose window.");
            Check(Math.Abs(seconds - (phase / 30.0)) < 1e-6 && Math.Abs(angle - (Sign * Angle(phase))) < 1e-6 &&
                (!lift || Math.Abs(angle - primary.Angle) < 1e-6), "Landmark timing/angle disagrees with source/primary reference.");
            Check(lift || ((Sign * angle) > (Sign * primary.Angle) && (Sign * angle) <
                ((Sign * feet[((i / 2) + 1) % 2].Angle) + (i == 3 ? Math.Abs(SignedSpan) : 0))),
                "Recovery angular association must lie between its adjacent primary lifts.");
            landmarks.Add(new Landmark(id, lift ? LandmarkKind.PrimaryLift : LandmarkKind.Recovery, primary.Foot,
                frame, phase, seconds, angle));
        }
        Landmarks = landmarks.AsReadOnly();
    }

    internal double Angle(double phase)
    {
        if (!double.IsFinite(phase) || phase < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }
        double cycle = Math.Floor(phase / Period), local = phase - (cycle * Period);
        int frame = (int)Math.Floor(local);
        return (cycle * Math.Abs(SignedSpan)) + _angles[frame] + ((local - frame) * (_angles[frame + 1] - _angles[frame]));
    }

    private static double Number(JsonElement item, string key)
    {
        double value = item.GetProperty(key).GetDouble();
        Check(double.IsFinite(value), "Non-finite turn metadata.");
        return value;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid)
        {
            throw new InvalidDataException(message);
        }
    }
}
