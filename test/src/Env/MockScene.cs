using AlleyCat.Actor;
using AlleyCat.Env;
using AlleyCat.Scene;
using Godot;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Tests.Env;

public readonly struct MockScene(Seq<IActor> actors) : IScene
{
    public Eff<IEnv, Seq<IActor>> Actors => SuccessEff(actors);

    public SceneTree SceneTree => throw new NotImplementedException();

    public IO<T> AddNode<T>(T node) where T : Node => throw new NotImplementedException();
}