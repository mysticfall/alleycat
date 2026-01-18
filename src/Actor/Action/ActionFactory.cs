using AlleyCat.Service.Typed;
using Godot;

namespace AlleyCat.Actor.Action;

[GlobalClass]
public abstract partial class ActionFactory : ResourceFactory<IAction>;