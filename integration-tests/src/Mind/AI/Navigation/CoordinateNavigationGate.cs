using AlleyCat.Mind.AI.Tool;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Navigation;

/// <summary>The navigation action a gate task expects.</summary>
internal enum NavigationTaskKind
{
    /// <summary>A <c>walk_to</c> call without a required heading.</summary>
    Walk,

    /// <summary>A stationary <c>turn_to</c> call.</summary>
    Turn,

    /// <summary>A <c>walk_to</c> call whose arrival facing is scored.</summary>
    WalkAndFace,
}

/// <summary>
/// One pre-registered AI-015 TR-21 task: the instruction the companion speaks, the expected action, and the scene
/// nodes whose transforms define the ground truth.
/// </summary>
/// <param name="Id">Stable task identifier used in artefacts.</param>
/// <param name="Instruction">The line the companion says to the subject.</param>
/// <param name="Kind">The expected navigation action.</param>
/// <param name="TargetNodePath">Scene node whose position is the destination (walk tasks) or null.</param>
/// <param name="StandOff">Whether the destination is "next to" an occupied target (stand-off allowance applies).</param>
/// <param name="FaceNodePath">Scene node the subject must face, or null when no heading is required.</param>
/// <param name="DeclaredTarget">Declared destination (x, z) ground truth, checked against the scene.</param>
/// <param name="DeclaredBearing">Declared compass-bearing ground truth in degrees, checked against the scene.</param>
internal sealed record NavigationGateTask(
    string Id,
    string Instruction,
    NavigationTaskKind Kind,
    string? TargetNodePath,
    bool StandOff,
    string? FaceNodePath,
    (double X, double Z)? DeclaredTarget,
    double? DeclaredBearing);

/// <summary>
/// Pre-registered facts of the AI-015 TR-21 live gate: the agent-navigation scene's identities, the fixed five-task
/// set and its ground truth, and the acceptance criteria. Tool names and paces are the production constants; nothing
/// model-facing is duplicated here.
/// </summary>
internal static class CoordinateNavigationGate
{
    public const string SubjectNodePath = AgentNavigationSubject.NodeName;
    public const string CompanionNodePath = "Companion";
    public const string SubjectFullId = "char:" + AgentNavigationSubject.ReferenceId;
    public const string CompanionFullId = "char:reference_male";

    public const string WalkToName = WalkToTool.ProductionToolName;
    public const string TurnToName = TurnToTool.ProductionToolName;
    public const string StopName = StopTool.ProductionToolName;
    public const string MinimapName = MinimapTool.ProductionToolName;
    public const string SpeakName = "speak";
    public const string WaitName = "wait";

    /// <summary>Declared trials per task.</summary>
    public const int TrialsPerTask = 10;

    /// <summary>Target requests one trial may make before it ends without a navigation call.</summary>
    public const int MaxRequestsPerTrial = 4;

    /// <summary>Follow-up requests allowed after the arrival of a heading-less walk (two-step heading path).</summary>
    public const int ArrivalFollowUpRequests = 3;

    /// <summary>Position tolerance in metres around the ground-truth destination.</summary>
    public const double PositionToleranceMetres = 0.75;

    /// <summary>Extra allowance in metres for "next to X" tasks, whose target point is occupied.</summary>
    public const double StandOffAllowanceMetres = 0.75;

    /// <summary>Heading tolerance in degrees either side of the ground-truth bearing.</summary>
    public const double HeadingToleranceDegrees = 30d;

    /// <summary>Required pass rate for position and heading criteria.</summary>
    public const double RequiredPassRate = 0.8;

    /// <summary>The subject's declared position and bearing in the scene.</summary>
    public static readonly (double X, double Z, double Bearing) DeclaredSubjectPose = (1.0, 1.5, 200d);

    /// <summary>The valid pace names, as the production tool declares them.</summary>
    public static readonly string[] Paces = [NavigationPace.Stroll, NavigationPace.Walk, NavigationPace.Hurry];

    /// <summary>The fixed task set, declared before running.</summary>
    public static readonly IReadOnlyList<NavigationGateTask> Tasks =
    [
        new("T1-next-to-crate", "Could you go and stand next to the crate for me?",
            NavigationTaskKind.Walk, "Landmarks/Crate", StandOff: true, FaceNodePath: null,
            DeclaredTarget: (4.5, -2.0), DeclaredBearing: null),
        new("T2-come-to-me", "Come over here and stand next to me, please.",
            NavigationTaskKind.Walk, CompanionNodePath, StandOff: true, FaceNodePath: null,
            DeclaredTarget: (-1.0, -3.5), DeclaredBearing: null),
        new("T3-face-lamp", "Stay where you are, but turn to face the lamp.",
            NavigationTaskKind.Turn, TargetNodePath: null, StandOff: false, FaceNodePath: "Landmarks/Lamp",
            DeclaredTarget: null, DeclaredBearing: 125.5),
        new("T4-rug-face-crate", "Go and stand on the rug, and face the crate once you're there.",
            NavigationTaskKind.WalkAndFace, "Landmarks/Rug", StandOff: false, FaceNodePath: "Landmarks/Crate",
            DeclaredTarget: (-2.5, -1.0), DeclaredBearing: 81.9),
        new("T5-bench-face-me", "Go over by the bench, and when you get there, turn to face me.",
            NavigationTaskKind.WalkAndFace, "Landmarks/Bench", StandOff: true, FaceNodePath: CompanionNodePath,
            DeclaredTarget: (-2.5, 4.5), DeclaredBearing: 10.6),
    ];

    /// <summary>The rehearsal scenario description, supplied to the subject's mind through its scenario manager.</summary>
    public const string ScenarioDescription =
        "You and `char:reference_male` are rehearsing stage blocking together in an open practice yard. "
        + "`char:reference_male` is directing the rehearsal: when they ask you to move somewhere or to face something, "
        + "you cooperate and do it.";

    /// <summary>Fixed current-scene message appended to every request, in the production status shape.</summary>
    public const string CurrentSceneMessage =
        "Current game time: 12 s\nCurrent attended characters:\n- `char:reference_male` is 5.4 m away (observed just now)\n";

    /// <summary>Compass bearing (0 = −Z, clockwise) from one ground point to another.</summary>
    public static double BearingBetween(double fromX, double fromZ, double toX, double toZ)
        => CompassBearing.Normalise(Math.Atan2(toX - fromX, -(toZ - fromZ)) * 180d / Math.PI);

    /// <summary>Compass bearing of a horizontal forward vector (the node's −Z basis).</summary>
    public static double BearingOfForward(double forwardX, double forwardZ)
        => CompassBearing.Normalise(Math.Atan2(forwardX, -forwardZ) * 180d / Math.PI);

    /// <summary>Smallest absolute angular difference between two bearings, in degrees.</summary>
    public static double AngularError(double a, double b)
    {
        double difference = Math.Abs(CompassBearing.Normalise(a) - CompassBearing.Normalise(b));
        return difference > 180d ? 360d - difference : difference;
    }

    /// <summary>
    /// Derives every task's ground truth from the loaded scene and asserts it still matches the declared,
    /// pre-registered values (scene-drift guard).
    /// </summary>
    public static IReadOnlyDictionary<string, NavigationGroundTruth> ComputeGroundTruth(
        Node root,
        (double X, double Z, double Bearing) subjectPose)
    {
        Dictionary<string, NavigationGroundTruth> truths = new(StringComparer.Ordinal);
        foreach (NavigationGateTask task in Tasks)
        {
            Vector3? target = task.TargetNodePath is { } targetPath
                ? root.GetNode<Node3D>(targetPath).GlobalPosition
                : null;
            double? bearing = null;
            if (task.FaceNodePath is { } facePath)
            {
                Vector3 face = root.GetNode<Node3D>(facePath).GlobalPosition;
                (double fromX, double fromZ) = target is { } t ? (t.X, t.Z) : (subjectPose.X, subjectPose.Z);
                bearing = BearingBetween(fromX, fromZ, face.X, face.Z);
            }

            NavigationGroundTruth truth = new(task.Id, target?.X, target?.Z, bearing);
            if (task.DeclaredTarget is { } declared)
            {
                Assert.Equal(declared.X, truth.TargetX!.Value, 2);
                Assert.Equal(declared.Z, truth.TargetZ!.Value, 2);
            }

            if (task.DeclaredBearing is { } declaredBearing)
            {
                Assert.True(AngularError(declaredBearing, truth.Bearing!.Value) < 0.1d,
                    $"{task.Id}: declared bearing {declaredBearing} vs scene {truth.Bearing}");
            }

            truths.Add(task.Id, truth);
        }

        return truths;
    }
}
