using Godot;

namespace AlleyCat.Animation;

public interface IAnimatable
{
    AnimationPlayer AnimationPlayer { get; }
}

public interface IStatefulAnimatable : IAnimatable
{
    AnimationTree AnimationTree { get; }

    AnimationPlayer IAnimatable.AnimationPlayer => AnimationTree
        .GetTree()
        .GetCurrentScene()
        .GetNode<AnimationPlayer>(AnimationTree.AnimPlayer);
}