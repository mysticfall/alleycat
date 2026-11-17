using AlleyCat.Navigation;

namespace AlleyCat.IntegrationTests.Motion;

// Minimal native-agent boundary fixture; intentionally has no model/session or normal actor lifecycle.
internal sealed partial class ReachabilityOnlyNavigation : MotionBricksNavigation
{
    public override void _Ready()
    {
    }
    public override void _PhysicsProcess(double delta)
    {
    }
}
