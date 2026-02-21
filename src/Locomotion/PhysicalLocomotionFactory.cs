using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Locomotion.Rotation;
using AlleyCat.Locomotion.Velocity;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Locomotion;

[GlobalClass]
public partial class PhysicalLocomotionFactory : LocomotionFactory
{
    private const string DefaultGravity = "physics/3d/default_gravity";

    private const string DefaultGravityVector = "physics/3d/default_gravity_vector";

    [Export] public CharacterBody3D? Body { get; set; }

    protected override Eff<IEnv, ILocomotion> CreateService(
        IVelocityCalculator velocityCalculator,
        IRotationCalculator rotationCalculator,
        ILoggerFactory loggerFactory
    ) =>
        from body in Body.Require("Body is not set.")
        let settings = ProjectSettings.Singleton
        from gravity in IO.lift(() => settings.GetSetting(DefaultGravity).AsSingle())
        from gravityVector in IO.lift(() => settings.GetSetting(DefaultGravityVector).AsVector3())
        select (ILocomotion)new PhysicalLocomotion(
            body,
            gravity * gravityVector,
            velocityCalculator,
            rotationCalculator,
            OnPhysicsProcess,
            loggerFactory
        );
}