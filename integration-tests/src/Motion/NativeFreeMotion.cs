using AlleyCat.Control;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using Godot;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>
/// Keeps MotionBricks out of fixtures whose behaviour does not depend on generated motion. Every
/// <see cref="MotionBricksNavigation" /> and <see cref="PlayerMotionBricks" /> that enters the tree while the scope is
/// active gets a native-free session factory before its deferred initialisation, so the production nodes stay in place
/// and initialise normally, but no shared CUDA model is loaded.
/// </summary>
/// <remarks>
/// Install the scope before the fixture adds characters, scenes, or installers to the tree, and dispose it in the
/// fixture's teardown. Tests that verify generated motion must not use it; they load the real model in an
/// <c>[IsolatedProcess]</c> session instead.
/// </remarks>
internal sealed class NativeFreeMotion : IDisposable
{
    private readonly SceneTree _tree;
    private bool _disposed;

    private NativeFreeMotion(SceneTree tree)
    {
        _tree = tree;
        _tree.NodeAdded += Bind;
    }

    /// <summary>Starts binding native-free sessions to MotionBricks nodes entering <paramref name="tree" />.</summary>
    /// <param name="tree">The scene tree the fixture populates.</param>
    /// <returns>A scope that stops binding when disposed.</returns>
    public static NativeFreeMotion Install(SceneTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return new NativeFreeMotion(tree);
    }

    /// <summary>Binds a native-free session to <paramref name="node" /> if it is a MotionBricks consumer.</summary>
    /// <param name="node">A node that has not yet run its deferred MotionBricks initialisation.</param>
    public static void Bind(Node node)
    {
        if (node is MotionBricksNavigation { SessionFactory: null } navigation)
        {
            navigation.SessionFactory = static () => new HoldPoseSession();
        }
        else if (node is PlayerMotionBricks { SessionFactory: null } player)
        {
            player.SessionFactory = static () => new HoldPoseSession();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (GodotObject.IsInstanceValid(_tree))
        {
            _tree.NodeAdded -= Bind;
        }
    }

    /// <summary>Holds the latest realised pose: valid output that never calls native inference.</summary>
    private sealed class HoldPoseSession : SomaInferenceWorker.ISession
    {
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
            => [.. Enumerable.Range(0, 4).Select(_ => (Transform3D[])context[3].Clone())];

        public void Dispose()
        {
        }
    }
}
