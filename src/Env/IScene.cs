using AlleyCat.Actor;
using Godot;
using LanguageExt;

namespace AlleyCat.Env;

public interface IScene : IActorContainer
{
    IO<Viewport> GetViewport();

    IO<T> AddNode<T>(T node) where T : Node;
}
