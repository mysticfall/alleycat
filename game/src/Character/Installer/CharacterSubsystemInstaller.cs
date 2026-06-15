using Godot;

namespace AlleyCat.Character.Installer;

/// <summary>
/// Base class for high-level character subsystem installers that require explicit character installation context.
/// </summary>
[Tool]
[GlobalClass]
public abstract partial class CharacterSubsystemInstaller : CharacterSceneInstaller
{
    /// <summary>
    /// Resolves a required node from the installation target root.
    /// </summary>
    protected static T RequireTargetNode<T>(Node targetRoot, string path)
        where T : Node
        => targetRoot.GetNodeOrNull<T>(path)
            ?? throw new InvalidOperationException(
                $"Character subsystem installer could not resolve required {typeof(T).Name} at '{path}' from '{targetRoot.GetPath()}'.");

    /// <summary>
    /// Resolves a required skeleton child node by conventional name.
    /// </summary>
    protected static T RequireSkeletonNode<T>(Skeleton3D skeleton, string path)
        where T : Node
        => skeleton.GetNodeOrNull<T>(path)
            ?? throw new InvalidOperationException(
                $"Character subsystem installer could not resolve required {typeof(T).Name} at skeleton path '{path}' from '{skeleton.GetPath()}'.");
}
