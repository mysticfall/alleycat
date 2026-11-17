using Godot;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    private static Transform3D FeedbackTransform(float[] values, int at) => new(new Basis(
        new(values[at], values[at + 1], values[at + 2]), new(values[at + 3], values[at + 4], values[at + 5]),
        new(values[at + 6], values[at + 7], values[at + 8])), new(values[at + 9], values[at + 10], values[at + 11]));
}
