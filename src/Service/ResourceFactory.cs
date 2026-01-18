using AlleyCat.Env;
using Godot;
using LanguageExt;

namespace AlleyCat.Service;

[GlobalClass]
public abstract partial class ResourceFactory : Resource, IServiceFactory
{
    public abstract Type ServiceType { get; }

    public abstract Eff<IEnv, object> Service { get; }
}