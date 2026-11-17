using AlleyCat.Motion;
using Godot;

namespace AlleyCat.Navigation;

public partial class MotionBricksNavigation
{
    private sealed class NavigationRefusalException(string message) : Exception(message);
    private bool _nativeDeparturePending = true;

    internal sealed record CurveIntent(double Heading, double GoalError, double Speed, double AngularRate,
        bool Arrival, double[] Times, float[] EncodedHeadings, float[][] Positions);
    internal CurveIntent? SourceRateCurveIntent
    {
        get; private set;
    }

    private float AnatomicalHeading(Transform3D[] pose)
    {
        int right = Array.IndexOf(_data!.Names, "RightLeg"), left = Array.IndexOf(_data.Names, "LeftLeg");
        return _data.Parents[right] != 0 || _data.Parents[left] != 0
            ? throw new InvalidDataException("Unproved hip-heading parent frame.")
            : SomaInference.Heading(pose[0] * pose[right].Origin, pose[0] * pose[left].Origin);
    }

    private void SubmitNativePathTargets(Transform3D[][] context, Vector3 start, Vector3 next)
    {
        if (_reviewed is null)
        {
            _failureDiagnosticCategory = "source-rate-configuration";
            throw new InvalidOperationException("Native path targets require the shared source roles.");
        }
        NavigationRouteSnapshot route = CurrentRouteSnapshot!;
        if (!OwnsRoute(route) || route.PathPoints.Count == 0)
        {
            throw new InvalidDataException("Native path does not belong to the current request.");
        }
        if (SubmitPositioning(context, start, next))
        {
            _nativeDeparturePending = false;
            return;
        }
        const double horizon = 49.0 / 30;
        float heading = AnatomicalHeading(context[3]) + Mathf.Pi;
        float speed = _reviewed.ForwardReferenceSpeed * NormalisedSpeed;
        Vector3 final = -Destination.Basis.Z;
        float finalYaw = Mathf.Atan2(final.X, final.Z);
        float finalError = Mathf.Wrap(finalYaw - heading, -Mathf.Pi, Mathf.Pi);
        float finalRate = Math.Abs(_reviewed.TurnReferenceAngularVelocity(finalError > 0));
        NativePathMotionTargets.Sample lead = NativePathMotionTargets.At(route.PathPoints, route.ActivePathIndex, start, speed * (float)horizon);
        _arrival = NativeDestinationLeg && lead.AtEnd && _intent.RemainingPathDistance <= (speed * horizon) + .001 && Math.Abs(finalError) <= finalRate * horizon;
        Transform3D[][] target;
        if (_arrival)
        {
            _ = _reviewed.SetIntent("", ObservationTime);
            MotionCatalogue.Stage idle = _catalogue!.Select(0);
            Transform3D[][] clip = MotionCatalogue.Clip(_data!, idle.Action);
            Basis facing = new(Vector3.Up, finalYaw - Mathf.Pi);
            Vector3 placement = Ground(SomaMotionData.ModelToWorld.Inverse() * Destination.Origin);
            Vector3 origin = Ground(clip[idle.Entry][0].Origin);
            target = new Transform3D[4][];
            for (int i = 0; i < 4; i++)
            {
                target[i] = (Transform3D[])clip[idle.Entry + i].Clone();
                target[i][0] = new(facing * target[i][0].Basis, placement + (facing * (target[i][0].Origin - origin)));
            }
        }
        else
        {
            _ = _reviewed.SetIntent("forward", ObservationTime);
            target = _reviewed.Targets(ObservationTime, horizon, Basis.Identity, Vector3.Zero, NormalisedSpeed);
            SourceRateCurveIntent = PlaceNativeTravelTargets(target, route.PathPoints, route.ActivePathIndex, start, heading, speed);
            _submissionCorridorEnd = route.NextPathPoint;
            SubmitTarget(context, target);
            return;
        }
        double[] times = new double[4];
        float[] encoded = new float[4];
        float[][] positions = new float[4][];
        double firstError = finalError, firstRate = finalRate;
        for (int i = 0; i < 4; i++)
        {
            times[i] = horizon + (i / 30.0);
            encoded[i] = AnatomicalHeading(target[i]) + Mathf.Pi;
            positions[i] = RefusalPoint(SomaMotionData.ModelToWorld * target[i][0].Origin);
        }
        SourceRateCurveIntent = new(heading, firstError, speed, firstRate, _arrival, times, encoded, positions);
        _nativeDeparturePending = false;
        _submissionCorridorEnd = route.NextPathPoint;
        SubmitTarget(context, target);
    }

    // Kept at the placement boundary so frozen conditioning inputs can exercise the production adapter without inference.
    internal CurveIntent PlaceNativeTravelTargets(Transform3D[][] target, IReadOnlyList<Vector3> path,
        int activeIndex, Vector3 start, float heading, float speed)
    {
        const double horizon = 49.0 / 30;
        float stationSpeed = speed;
        if (_nativeDeparturePending)
        {
            NativePathMotionTargets.Sample lead = NativePathMotionTargets.At(path, activeIndex, start, speed * (float)horizon);
            float desired = lead.Direction.LengthSquared() > 0 ? Mathf.Atan2(lead.Direction.X, lead.Direction.Z) : heading;
            float error = Mathf.Wrap(desired - heading, -Mathf.Pi, Mathf.Pi);
            double budget = Math.Abs(_reviewed!.TurnReferenceAngularVelocity(error > 0)) * horizon;
            // A positive native advance is still non-forward after the entire facing budget. Share its magnitude
            // by available/required turning time, not a fixed half-lead or a replacement route/heading curve.
            if (Math.Abs(error) - budget >= Math.PI / 2 && budget > 0)
            {
                stationSpeed *= (float)(budget / Math.Abs(error));
            }
        }
        _nativeDeparturePending = false;
        double[] times = new double[4];
        float[] encoded = new float[4];
        float[][] positions = new float[4][];
        double firstError = 0, firstRate = 0;
        for (int i = 0; i < 4; i++)
        {
            times[i] = horizon + (i / 30.0);
            NativePathMotionTargets.Sample sample = NativePathMotionTargets.At(path, activeIndex, start, stationSpeed * (float)times[i]);
            float desired = sample.Direction.LengthSquared() > 0 ? Mathf.Atan2(sample.Direction.X, sample.Direction.Z) : heading;
            float error = Mathf.Wrap(desired - heading, -Mathf.Pi, Mathf.Pi);
            float rate = Math.Abs(_reviewed!.TurnReferenceAngularVelocity(error > 0));
            float placedHeading = heading + (float)Math.Clamp(error, -rate * times[i], rate * times[i]);
            target[i][0].Basis = new Basis(Vector3.Up, placedHeading - Mathf.Pi - AnatomicalHeading(target[i])) * target[i][0].Basis;
            Vector3 model = SomaMotionData.ModelToWorld.Inverse() * sample.Position;
            model.Y += target[i][0].Origin.Y;
            target[i][0].Origin = model;
            if (i == 0)
            {
                firstError = error;
                firstRate = Math.CopySign(rate, error);
            }
            encoded[i] = AnatomicalHeading(target[i]) + Mathf.Pi;
            positions[i] = RefusalPoint(SomaMotionData.ModelToWorld * target[i][0].Origin);
        }
        return new(heading, firstError, speed, firstRate, false, times, encoded, positions);
    }
}
