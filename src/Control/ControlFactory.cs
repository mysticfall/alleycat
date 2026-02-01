using AlleyCat.Service;
using AlleyCat.Service.Typed;
using Godot;

namespace AlleyCat.Control;

[GlobalClass]
public abstract partial class ControlFactory : NodeFactory<IControl>, IServiceFactory
{
    RunOption IServiceFactory.AutoRun => RunOption.Never;
}