using AlleyCat.XR;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Testing;

/// <summary>
/// Minimal headless <see cref="Game" /> stand-in for the game-menu visual fixture.
/// </summary>
/// <remarks>
/// <para>
/// Visual fixture runs launch through a <c>tests/...</c> script argument, so
/// <see cref="Game._Ready" /> and the XR-initialisation subscription inside
/// <see cref="Game._EnterTree" /> are already bypassed by
/// <see cref="RuntimeContext.ShouldBypassGlobalStartup(SceneTree)" />. The only remaining startup
/// requirement is that <see cref="Game._EnterTree" /> resolves an <see cref="XRManager" /> from
/// the built service provider, which normally comes from a scene-owned child that this fixture
/// does not have. This stub satisfies that resolution with a detached, never-initialised
/// manager, registered as a provided instance so the container never disposes it (the menu may
/// keep a signal subscription to the resolved manager until teardown).
/// </para>
/// <para>
/// The runner adds the fixture after the autoload singletons initialise, so this stub replaces
/// the autoloaded Global game instance through the integration-test branch of
/// <see cref="Game.Instance" /> management instead of colliding with it. The stub parents the
/// captured viewport like the production global scene, guaranteeing the menu subtree exits the
/// tree before the stub during teardown and <see cref="Game.Instance" /> stays resolvable in the
/// menu's teardown paths.
/// </para>
/// </remarks>
[GlobalClass]
public sealed partial class GameMenuVisualGameStub : Game
{
    /// <inheritdoc />
    protected override void RegisterServices(IServiceCollection services)
    {
        base.RegisterServices(services);
        _ = services.AddSingleton(new XRManager());
    }
}
