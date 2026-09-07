using System.Reflection;
using AlleyCat.Control;
using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Control;

/// <summary>
/// Windowed production-player coverage for CTRL-001 optional-navigation arbitration at the manual-input boundary.
/// </summary>
public sealed class PlayerControllerManualNavigationIntegrationTests
{
    private const string PlayerScenePath = "res://assets/characters/reference/ally_player.tscn";

    [Fact]
    public async Task ReferencePlayer_AcceptedNonNeutralTranslation_CancelsNavigationBeforeLocomotion()
    {
        SceneTree sceneTree = GetSceneTree();
        PlayerInputFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.Navigation.ActivateMovementAuthority();

            InvokeMovementInput(fixture.Controller, new Vector2(0.4f, 0.6f));

            Assert.False(fixture.Navigation.IsMovementAuthorityActive);
            Assert.True(fixture.Locomotion.IsManualTranslationAccepted(new Vector2(0.4f, 0.6f)));
            Assert.True(GetMovementInput(fixture.Locomotion).Length() > 0f);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    [Fact]
    public async Task ReferencePlayer_DeadzoneNeutralisedRawTranslation_DoesNotCancelNavigation()
    {
        SceneTree sceneTree = GetSceneTree();
        PlayerInputFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.Navigation.ActivateMovementAuthority();

            InvokeMovementInput(fixture.Controller, new Vector2(0.1f, 0f));

            Assert.True(fixture.Navigation.IsMovementAuthorityActive);
            Assert.True(GetMovementInput(fixture.Locomotion).IsZeroApprox());
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    [Fact]
    public async Task ReferencePlayer_NeutralTranslation_DoesNotCancelNavigation()
    {
        SceneTree sceneTree = GetSceneTree();
        PlayerInputFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.Navigation.ActivateMovementAuthority();

            InvokeMovementInput(fixture.Controller, Vector2.Zero);

            Assert.True(fixture.Navigation.IsMovementAuthorityActive);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    [Fact]
    public async Task ReferencePlayer_WaitingWithNoTranslation_DoesNotCancelNavigation()
    {
        SceneTree sceneTree = GetSceneTree();
        PlayerInputFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.Navigation.ActivateMovementAuthority();

            InvokeMovementInput(fixture.Controller, Vector2.Zero);

            Assert.True(fixture.Navigation.IsMovementAuthorityActive);
            Assert.True(GetMovementInput(fixture.Locomotion).IsZeroApprox());
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    [Fact]
    public async Task ReferencePlayer_BlockedTranslation_DoesNotCancelNavigation()
    {
        SceneTree sceneTree = GetSceneTree();
        PlayerInputFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            SetMovementPermissions(fixture.Locomotion, new MutablePermissionSource(LocomotionPermissions.RotationOnly));
            fixture.Navigation.ActivateMovementAuthority();

            InvokeMovementInput(fixture.Controller, new Vector2(0f, 1f));

            Assert.True(fixture.Navigation.IsMovementAuthorityActive);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    [Fact]
    public async Task ReferencePlayer_PermissionLossBeforeTranslation_DoesNotCancelNavigation()
    {
        SceneTree sceneTree = GetSceneTree();
        PlayerInputFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            var permissions = new MutablePermissionSource(LocomotionPermissions.Allowed);
            SetMovementPermissions(fixture.Locomotion, permissions);
            permissions.Permissions = LocomotionPermissions.RotationOnly;
            fixture.Navigation.ActivateMovementAuthority();

            InvokeMovementInput(fixture.Controller, new Vector2(0.4f, 0.6f));

            Assert.True(fixture.Navigation.IsMovementAuthorityActive);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    [Fact]
    public async Task ReferencePlayer_Braking_DoesNotCancelNavigation()
    {
        SceneTree sceneTree = GetSceneTree();
        PlayerInputFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.Locomotion.Move(new Vector2(0.4f, 0.6f));
            fixture.Navigation.ActivateMovementAuthority();

            InvokeMovementInput(fixture.Controller, Vector2.Zero);

            Assert.True(fixture.Navigation.IsMovementAuthorityActive);
            Assert.True(GetMovementInput(fixture.Locomotion).IsZeroApprox());
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    [Fact]
    public async Task ReferencePlayer_RotationOnlyControllerMouseOrCameraInput_DoesNotCancelNavigation()
    {
        SceneTree sceneTree = GetSceneTree();
        PlayerInputFixture fixture = await CreateFixtureAsync(sceneTree);

        try
        {
            fixture.Navigation.ActivateMovementAuthority();

            InvokeRotationInput(fixture.Controller, new Vector2(0.5f, 0.25f));

            Assert.True(fixture.Navigation.IsMovementAuthorityActive);
            Assert.True(GetRotationInput(fixture.Locomotion).Length() > 0f);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    private static async Task<PlayerInputFixture> CreateFixtureAsync(SceneTree sceneTree)
    {
        Node player = LoadPackedScene(PlayerScenePath).Instantiate();
        sceneTree.Root.AddChild(player);
        EnsureCharacterRuntimeInstalled(player);
        await WaitForFramesAsync(sceneTree, 2);

        PlayerController controller = Assert.IsType<PlayerController>(player.GetNode("PlayerController"), exactMatch: false);
        CharacterLocomotion locomotion = Assert.IsType<CharacterLocomotion>(player.GetNode("Locomotion"), exactMatch: false);
        PlayerNavigation navigation = Assert.IsType<PlayerNavigation>(player.GetNode("PlayerNavigation"), exactMatch: false);

        return new PlayerInputFixture(player, controller, locomotion, navigation);
    }

    private static async Task DestroyFixtureAsync(SceneTree sceneTree, PlayerInputFixture fixture)
    {
        fixture.Player.QueueFree();
        await WaitForFramesAsync(sceneTree, 2);
    }

    private static void InvokeMovementInput(PlayerController controller, Vector2 input)
        => InvokeInputUpdate(controller, "UpdateMovementInput", input);

    private static void InvokeRotationInput(PlayerController controller, Vector2 input)
        => InvokeInputUpdate(controller, "UpdateRotationInput", input);

    private static void InvokeInputUpdate(PlayerController controller, string methodName, Vector2 input)
    {
        MethodInfo method = typeof(PlayerController).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Expected PlayerController {methodName} method.");
        _ = method.Invoke(controller, [input]);
    }

    private static Vector2 GetMovementInput(CharacterLocomotion locomotion) => GetInput(locomotion, "_movementInput");

    private static Vector2 GetRotationInput(CharacterLocomotion locomotion) => GetInput(locomotion, "_rotationInput");

    private static Vector2 GetInput(CharacterLocomotion locomotion, string fieldName)
    {
        FieldInfo field = typeof(CharacterLocomotion).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Expected CharacterLocomotion {fieldName} field.");
        return (Vector2)(field.GetValue(locomotion)
            ?? throw new InvalidOperationException($"Expected CharacterLocomotion {fieldName} to contain a Vector2."));
    }

    private static void SetMovementPermissions(
        CharacterLocomotion locomotion,
        ILocomotionPermissionSource permissionSource)
    {
        FieldInfo field = typeof(LocomotionBase).GetField("_permissionSources", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected LocomotionBase _permissionSources field.");
        field.SetValue(locomotion, new ILocomotionPermissionSource[] { permissionSource });
    }

    private sealed record PlayerInputFixture(
        Node Player,
        PlayerController Controller,
        CharacterLocomotion Locomotion,
        PlayerNavigation Navigation);

    private sealed class MutablePermissionSource(LocomotionPermissions permissions) : ILocomotionPermissionSource
    {
        public LocomotionPermissions Permissions
        {
            get; set;
        } = permissions;

        public LocomotionPermissions LocomotionPermissions => Permissions;
    }
}
