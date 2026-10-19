namespace AlleyCat.IntegrationTests.Mind.Attention;

/// <summary>
/// Independent TR23/TR25 oracle for the authorised yaw-cap/zero-vertical bounded reference. This is
/// test-side analytical geometry, not the production helper or a global residual optimiser. Unsupported
/// geometry and missing/current-context mismatches fail closed. Positions of requested/mediated/solved
/// targets are deliberately absent: they are diagnostics, not attainment gates.
/// </summary>
internal static class RestAtLimitExclusion
{
    internal readonly record struct Vector(double X, double Y, double Z)
    {
        public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector operator *(Vector a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
        public double Dot(Vector other) => (X * other.X) + (Y * other.Y) + (Z * other.Z);
        public double Length => Math.Sqrt(Dot(this));
        public bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    }

    internal readonly record struct Basis(Vector X, Vector Y, Vector Z)
    {
        public static Basis Identity => new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1));
        public Vector Apply(Vector v) => (X * v.X) + (Y * v.Y) + (Z * v.Z);
        public Vector InverseApply(Vector v) => new(X.Dot(v), Y.Dot(v), Z.Dot(v));
        public Basis Compose(Basis child) => new(Apply(child.X), Apply(child.Y), Apply(child.Z));
        public static Basis FromPose(Pose pose)
        {
            double y = Radians(pose.Yaw), p = Radians(pose.Pitch), r = Radians(pose.Roll);
            double sy = Math.Sin(y), cy = Math.Cos(y), sp = Math.Sin(p), cp = Math.Cos(p);
            var x = new Vector(cy, 0, -sy);
            var up = new Vector(sy * sp, cp, cy * sp);
            return new((x * Math.Cos(r)) + (up * Math.Sin(r)), (up * Math.Cos(r)) - (x * Math.Sin(r)), new(sy * cp, -sp, cy * cp));
        }

        public Pose ToPose() => new(Degrees(Math.Atan2(Z.X, Z.Z)),
            Degrees(Math.Atan2(-Z.Y, Math.Sqrt((Z.X * Z.X) + (Z.Z * Z.Z)))), Degrees(Math.Atan2(X.Y, Y.Y)));

        // Numerical validation of float32 observation channels, not a gameplay/attainment tolerance.
        public bool Proper => X.Finite && Y.Finite && Z.Finite
            && Math.Abs(X.Dot(X) - 1) < 1e-5 && Math.Abs(Y.Dot(Y) - 1) < 1e-5 && Math.Abs(Z.Dot(Z) - 1) < 1e-5
            && Math.Abs(X.Dot(Y)) < 1e-5 && Math.Abs(X.Dot(Z)) < 1e-5 && Math.Abs(Y.Dot(Z)) < 1e-5
            && (((X.Y * Y.Z) - (X.Z * Y.Y)) * Z.X)
            + (((X.Z * Y.X) - (X.X * Y.Z)) * Z.Y) + (((X.X * Y.Y) - (X.Y * Y.X)) * Z.Z) > 0;
    }

    internal readonly record struct Transform(Basis Basis, Vector Origin);
    internal readonly record struct Pose(double Yaw, double Pitch, double Roll)
    {
        public bool Finite => double.IsFinite(Yaw) && double.IsFinite(Pitch) && double.IsFinite(Roll);
    }

    internal readonly record struct Stamp(ulong Anchor, ulong Generation, ulong Sequence, ulong Skeleton, long Frame, ulong CommandSequence);

    internal sealed record Geometry(Vector Anchor, Vector Head, Vector Pivot, Basis Upstream, Vector FaceOffset,
        Transform Envelope, double YawLimit, double UpLimit, double DownLimit);

    internal sealed record Context(Geometry Geometry, Stamp Captured, Stamp Current, Vector CurrentAnchor,
        Transform CurrentEnvelope, Transform CapturedSkeleton, Transform CurrentSkeleton,
        Pose RecordedCommand, Pose CurrentCommand, Pose Actual, bool PacketValid, double SampleTime);

    internal readonly record struct Decision(bool Eligible, string Reason, Pose Reference = default,
        double ExpectedResidual = double.NaN, double UnreachableLowerBound = double.NaN);

    public static Decision Evaluate(FreezeSample sample, Context? context)
    {
        if (context is null || context.Geometry is null)
        {
            return new(false, "missing-context");
        }

        Geometry g = context.Geometry;
        Stamp stamp = context.Captured;
        if (!context.PacketValid || !double.IsFinite(sample.TimeSeconds) || sample.TimeSeconds != context.SampleTime
            || stamp != context.Current || stamp.Anchor == 0 || stamp.Generation == 0 || stamp.Sequence == 0
            || stamp.Skeleton == 0 || stamp.Frame < 0 || stamp.CommandSequence != stamp.Sequence
            || g.Anchor != context.CurrentAnchor || g.Envelope != context.CurrentEnvelope
            || context.CapturedSkeleton != context.CurrentSkeleton)
        {
            return new(false, "stale-context");
        }

        if (!g.Anchor.Finite || !g.Head.Finite || !g.Pivot.Finite || !g.FaceOffset.Finite
            || !g.Envelope.Origin.Finite || !g.Upstream.Proper || !g.Envelope.Basis.Proper
            || !context.CurrentSkeleton.Origin.Finite || !context.CurrentSkeleton.Basis.Proper
            || !LegalLimit(g.YawLimit) || !LegalLimit(g.UpLimit) || !LegalLimit(g.DownLimit))
        {
            return new(false, "invalid-geometry");
        }

        // Context and series must describe the same presented head, in their declared frames. This
        // prevents stale actual-pose evidence from excusing an unattained/frozen observation.
        var actualWorld = g.Envelope.Basis.Compose(Basis.FromPose(context.Actual)).ToPose();
        if (!AtReference(actualWorld, new Pose(sample.Yaw, sample.Pitch, sample.Roll)))
        {
            return new(false, "stale-measurement");
        }

        // O(D) = p + D*w, with w = inverse(U)*(h-p) + authored face offset. Convert the target-pivot
        // ray and upstream lever into the transported anatomical envelope, never from solved output.
        Vector a = g.Envelope.Basis.InverseApply(g.Anchor - g.Pivot);
        Vector w = g.Upstream.InverseApply(g.Head - g.Pivot) + g.FaceOffset;
        double bearing = Math.Atan2(-a.X, -a.Z);
        double cap = Radians(g.YawLimit);
        // Supported positive-yaw case only. On this interval the closest yaw maximises horizontal
        // forward projection at +cap; other cases deliberately require different independent proofs.
        if (!(bearing > cap && bearing + cap <= Math.PI && a.Length > w.Length))
        {
            return new(false, "unsupported-yaw-proof");
        }

        double bz = (Math.Sin(cap) * a.X) + (Math.Cos(cap) * a.Z);
        double radius = Math.Sqrt((a.Y * a.Y) + (bz * bz));
        if (!(bz < 0 && radius > Math.Abs(w.Y)))
        {
            return new(false, "unsupported-pitch-branch");
        }

        // Exact admissible branch of a_y*cos(pitch) + b_z*sin(pitch) - w_y = 0.
        double pitch = Math.Atan2(a.Y, -bz) - Math.Asin(w.Y / radius);
        if (pitch < -Radians(g.DownLimit) || pitch > Radians(g.UpLimit))
        {
            return new(false, "unsupported-pitch-branch");
        }

        // All allowed pitches have positive cosine. First maximise the horizontal projection at the
        // proved yaw cap, then relax pitch bounds: this gives a conservative nearest-axis distance.
        // Any predicted eye origin is within |w| of p, so subtract its maximum parallax asin(|w|/|a|).
        double lowerBound = Degrees(Math.Acos(Math.Clamp(radius / a.Length, -1, 1)) - Math.Asin(w.Length / a.Length));
        if (!(lowerBound > FullFreezeDetector.ActiveResidualDegrees))
        {
            return new(false, "unreachable-proof-failed");
        }

        double cp = Math.Cos(pitch), sp = Math.Sin(pitch);
        var local = new Vector((Math.Cos(cap) * a.X) - (Math.Sin(cap) * a.Z) - w.X,
            (cp * a.Y) + (sp * bz) - w.Y, (-sp * a.Y) + (cp * bz) - w.Z);
        if (!(local.Z < 0) || !local.Finite)
        {
            return new(false, "unsupported-pitch-branch");
        }

        double residual = Degrees(Math.Atan2(Math.Sqrt((local.X * local.X) + (local.Y * local.Y)), -local.Z));
        var reference = new Pose(g.YawLimit, Degrees(pitch), 0);
        if (!AtReference(context.RecordedCommand, reference) || !AtReference(context.CurrentCommand, reference))
        {
            return new(false, "wrong-command", reference, residual, lowerBound);
        }

        if (!AtReference(context.Actual, reference))
        {
            return new(false, "unattained", reference, residual, lowerBound);
        }

        // The existing angular freeze tolerance also bounds residual consistency; no fitted residual
        // threshold or positional equality/tolerance is introduced.
        return !double.IsFinite(sample.Residual) || !double.IsFinite(sample.Influence)
            || sample.Residual <= FullFreezeDetector.ActiveResidualDegrees || sample.Influence <= FullFreezeDetector.ActiveInfluence
            || Math.Abs(sample.Residual - residual) > FullFreezeDetector.FreezeToleranceDegrees
            ? new(false, "inconsistent-residual", reference, residual, lowerBound)
            : new(true, "attained-unreachable-rest", reference, residual, lowerBound);
    }

    private static bool LegalLimit(double degrees) => double.IsFinite(degrees) && degrees > 0 && degrees < 90;
    private static double Radians(double degrees) => degrees * Math.PI / 180;
    private static double Degrees(double radians) => radians * 180 / Math.PI;
    private static bool AtReference(Pose pose, Pose reference) => pose.Finite
        && Math.Abs(Math.IEEERemainder(pose.Yaw - reference.Yaw, 360)) <= FullFreezeDetector.FreezeToleranceDegrees
        && Math.Abs(pose.Pitch - reference.Pitch) <= FullFreezeDetector.FreezeToleranceDegrees
        && Math.Abs(Math.IEEERemainder(pose.Roll - reference.Roll, 360)) <= FullFreezeDetector.FreezeToleranceDegrees;
}
