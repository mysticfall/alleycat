using AlleyCat.Actor;
using AlleyCat.Env;
using Godot;
using LanguageExt;

namespace AlleyCat.Scene;

public class GodotScene(SceneTree sceneTree) : IScene
{
    public const string ActorGroupName = "Actors";

    public Eff<IEnv, Seq<IActor>> Actors => sceneTree
        .GetNodesInGroup(ActorGroupName)
        .Cast<ActorFactory>()
        .AsIterable()
        .Traverse(x => x.TypedService)
        .Map(x => x.ToSeq())
        .As();

    public SceneTree SceneTree => sceneTree;

    public IO<T> AddNode<T>(T node) where T : Node => IO.lift(() =>
    {
        var root = sceneTree.Root;

        root.AddChild(node, @internal: Node.InternalMode.Back);

        node.Owner = root;

        return node;
    });
}