using AlleyCat.Actor;
using Godot;
using LanguageExt;

namespace AlleyCat.Scene;

public interface IScene : ISceneLoader, IActorContainer
{
    SceneTree SceneTree { get; }

    IO<T> AddNode<T>(T node) where T : Node;
}