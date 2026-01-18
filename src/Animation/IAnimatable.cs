using Godot;

namespace AlleyCat.Animation;

public interface IAnimatable
{
    AnimationTree AnimationTree { get; }
}