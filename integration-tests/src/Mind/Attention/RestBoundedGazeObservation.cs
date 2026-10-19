using Godot;

namespace AlleyCat.IntegrationTests.Mind.Attention;

public sealed partial class IsolatedFemaleTrackingIntegrationTests
{
    private sealed partial class Fixture
    {
        private long _animatedEntryEvaluation;
        private long _animatedFinalEvaluation;
        private Transform3D _animatedEntryHead;
        private Transform3D _animatedFinalNativeHead;
        private readonly Transform3D[] _animatedEntryLocals = new Transform3D[5];
        private readonly Transform3D[] _animatedFinalEntryLocals = new Transform3D[5];

        public void ConfigureAnimatedEntryObservation()
        {
            ((InputCaptureCharacterIK)_ik).Observe += (stage, _) =>
            {
                if (stage != "begin_before_provider")
                {
                    return;
                }

                ++_animatedEntryEvaluation;
                _animatedEntryHead = FixtureSkeleton.GlobalTransform * FixtureSkeleton.GetBoneGlobalPose(_indices[0]);
                for (int i = 0; i < 5; i++)
                {
                    _animatedEntryLocals[i] = FixtureSkeleton.GetBonePose(_indices[i]);
                }
            };
        }

        public async Task AnimatedWarmUpAsync()
        {
            // Preserve the original centring/hold/release windows without moving the authored
            // scenario origin to the current animated output.
            await CentreAsync(Anchor(0, 0, 1.4f));
            await ObserveAsync("warmup_hold", 1.6);
            Eyes.ClearLookTarget();
            await ObserveAsync("release", 1.6);
            CheckAnimatedRelease();
        }

        public void CheckAnimatedRelease()
        {
            Basis nativeFace = (_animatedFinalNativeHead.Basis * FixtureViewpoint.Basis).Orthonormalized();
            double error = Degrees((-nativeFace.Z).AngleTo(-_solvedFace.Z));
            Check(_animatedFinalEvaluation > 0, "animated release has a paired actual-entry/native-pose observation");
            Check(Samples[^1].Influence < 0.01f, "animated release influence <0.01");
            Check(double.IsFinite(error) && error <= 0.3d,
                $"animated release face returns to LIVE animation-owned pose {error:F6} deg <=0.3 deg");
        }
    }
}
