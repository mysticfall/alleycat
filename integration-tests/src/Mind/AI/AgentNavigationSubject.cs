using AlleyCat.Core;
using AlleyCat.IntegrationTests.Motion;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Navigation;
using AlleyCat.Rigging.Installation;
using Godot;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Minimal test-only subject of the agent-navigation scene (AI-015 TR-20, TR-22): the reference-female body driven by
/// real MotionBricks navigation, with a perception-free <see cref="AgenticMind" /> and the actor minimap marker — and
/// nothing else, so unrelated character systems (voice, hearing, eyes, hands, IK, perceptions, gaze) cannot affect
/// the navigation tests.
/// </summary>
/// <remarks>
/// <para>Kept, each because a test needs it:</para>
/// <list type="bullet">
/// <item>The imported <c>reference_female.blend</c> body — its <c>Female</c> model with <c>GeneralSkeleton</c>, turned
/// to face −Z as the role templates do, and its <c>AnimationPlayer</c>, which the production MotionBricks mount
/// (<c>native_soma_mount.gd</c>) requires on the actor.</item>
/// <item>An inert <see cref="RigRoleTemplateSceneInstaller" /> named <c>NPCCharacterInstaller</c> (no template, no
/// installers, no auto-install): <c>Character._EnterTree</c> mounts MotionBricks on the skeleton only when a role
/// installer is present, so this triggers the production mount hook without installing anything.</item>
/// <item><see cref="MotionBricksNavigation" /> with the reference NPC template's authored values — the navigation the
/// tools drive.</item>
/// <item>A collision sphere matching the base template's, so generated motion collides with the arena like the
/// production NPC (<c>MoveAndCollide</c> obstruction checks).</item>
/// <item>The NPC role's <c>AnimationCatalogue</c> with the default pack (ANIM-003 TR13, NAV-001 TR14): MotionBricks
/// navigation resolves its locomotion set and idle through it.</item>
/// <item>A perception-free <see cref="AgenticMind" /> named <c>Mind</c>: tool composition, outcome polling and the
/// event timeline.</item>
/// <item>The actor <c>MiniMapMarker</c>, so the subject appears on (and is the gold <c>*You*</c> marker of) the AI
/// minimap.</item>
/// <item>The <c>Actors</c> group: production scene membership of a character.</item>
/// </list>
/// <para>
/// The component projection exposes navigation only (<see cref="ComposeComponents" />), which is all the AI-015
/// composition gate and the tools read; the production <c>Character</c> contract's eyes, voice and hands are therefore
/// not needed, mocked or installed.
/// </para>
/// </remarks>
internal sealed partial class AgentNavigationSubject : CharacterHub
{
    /// <summary>The agent-navigation scene the subject joins.</summary>
    public const string ScenePath = "res://tests/agent_navigation/agent_navigation.tscn";

    /// <summary>The subject's node name in the scene.</summary>
    public const string NodeName = "Subject";

    /// <summary>The reference-character identity (<c>char:reference_female</c>).</summary>
    public const string ReferenceId = "reference_female";

    private const string BodyScenePath = "res://assets/characters/reference/female/reference_female.blend";
    private const string ActorMarkerPath = "res://assets/markers/minimap/actor.tscn";

    /// <summary>The subject's declared pose: world (1, 1.5), facing compass bearing 200°.</summary>
    public static Transform3D DeclaredTransform => new(CompassBearing.ToBasis(200d), new Vector3(1f, 0f, 1.5f));

    /// <summary>
    /// Instantiates the agent-navigation scene with the minimal subject added under its root, before the scene enters
    /// the tree, so the MotionBricks mount runs on tree entry exactly as for an authored character.
    /// </summary>
    public static Node InstantiateScene()
    {
        Node root = TestUtils.LoadPackedScene(ScenePath).Instantiate();
        root.AddChild(Create());
        return root;
    }

    /// <summary>Builds the minimal subject outside the tree.</summary>
    public static AgentNavigationSubject Create()
    {
        AgentNavigationSubject subject = new()
        {
            Name = NodeName,
            Id = ReferenceId,
            MotionModel = "Female",
            Transform = DeclaredTransform,
        };

        Node body = TestUtils.LoadPackedScene(BodyScenePath).Instantiate();
        try
        {
            foreach (string part in (string[])["Female", "AnimationPlayer"])
            {
                Node node = body.GetNode(part);
                body.RemoveChild(node);
                subject.AddChild(node);
            }
        }
        finally
        {
            body.Free();
        }

        // The imported model faces +Z; the role templates turn it to the actor's −Z forward.
        subject.GetNode<Node3D>("Female").Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), Vector3.Zero);
        subject.AddChild(new CollisionShape3D
        {
            Name = "CollisionShape3D",
            Position = new Vector3(0f, 0.1f, 0f),
            Shape = new SphereShape3D { Radius = 0.1f },
        });

        MotionBricksNavigation navigation = new()
        {
            Name = "MotionBricksNavigation",
            DestinationReachedDistance = 1f,
            AvoidanceRadius = 0.5f,
            AvoidanceHeight = 1f,
            AvoidanceMaxSpeed = 10f,
        };
        subject.AddChild(navigation);
        subject.Navigation = navigation;
        subject.AddChild(MotionFixtureCatalogue.Create(npc: true));
        subject.AddChild(new AgenticMind { Name = "Mind" });
        subject.AddChild(TestUtils.LoadPackedScene(ActorMarkerPath).Instantiate());
        subject.AddChild(new RigRoleTemplateSceneInstaller
        {
            Name = "NPCCharacterInstaller",
            AutoInstallOnReady = false,
            TargetSkeletonPath = "Female/GeneralSkeleton",
        });
        subject.AddToGroup("Actors", persistent: true);
        subject.RefreshComponents();
        return subject;
    }

    /// <summary>Projects navigation only: the one capability the coordinate navigation tests use.</summary>
    protected override void ComposeComponents(List<IComponent> components)
    {
        if (Navigation is not null)
        {
            components.Add(Navigation);
        }
    }
}
