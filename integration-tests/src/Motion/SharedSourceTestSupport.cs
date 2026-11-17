using System.Reflection;
using AlleyCat.Control;
using AlleyCat.Motion;
using Godot;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    private sealed class TargetEndpointSession(bool followsFacing) : SomaInferenceWorker.ISession
    {
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            var result = new Transform3D[50][];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = (Transform3D[])context[3].Clone();
                Basis rotation = followsFacing ? context[3][0].Basis.Slerp(target[0][0].Basis, Math.Min(1, i / 25f)) : context[3][0].Basis;
                result[i][0] = new Transform3D(rotation, context[3][0].Origin.Lerp(target[0][0].Origin, i / 49f));
            }
            return result;
        }

        public void Dispose()
        {
        }
    }

    private static FieldInfo ExhaustionField(string name) => typeof(WalkingExperimentDriver)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(name);

    private sealed class ExhaustionContextSession : SomaInferenceWorker.ISession
    {
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target) =>
            [.. Enumerable.Range(0, 40).Select(_ => (Transform3D[])context[3].Clone())];

        public void Dispose()
        {
        }
    }
}
