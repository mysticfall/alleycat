using AlleyCat.Service.Typed;
using Godot;

namespace AlleyCat.Locomotion.Velocity;

[GlobalClass]
public abstract partial class VelocityCalculatorFactory : NodeFactory<IVelocityCalculator>;