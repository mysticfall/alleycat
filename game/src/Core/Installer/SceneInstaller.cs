using Godot;

namespace AlleyCat.Core.Installer;

/// <summary>
/// Base node for scene installers that can be composed in Godot scenes.
/// </summary>
[Tool]
[GlobalClass]
public abstract partial class SceneInstaller : Node, ISceneInstaller
{

    /// <summary>
    /// Gets or sets the explicit target root used by editor installation when this installer is run individually.
    /// </summary>
    [Export]
    public Node? TargetRoot
    {
        get;
        set;
    }

    /// <summary>
    /// Editor-facing trigger that clears installer-owned output and re-runs this installer for visible inspection.
    /// </summary>
    [ExportGroup("Debug")]
    [Export]
    public bool InstallInEditor
    {
        get;
        set
        {
            field = false;
            if (value && Engine.IsEditorHint())
            {
                _ = CallDeferred(MethodName.InstallNowInEditor);
            }
        }
    }

    /// <inheritdoc />
    public abstract SceneInstallationResult Install(SceneInstallationContext context);

    /// <summary>
    /// Clears installer-owned materialised output and immediately re-runs installation for editor/debug inspection.
    /// </summary>
    public virtual void InstallNowInEditor()
    {
        if (TryCommitEditorUndoInstallAction())
        {
            return;
        }

        InstallNowInEditorWithoutUndo();
    }

    /// <summary>
    /// Executes editor installation without registering a nested editor undo action.
    /// </summary>
    public void InstallNowInEditorWithoutUndo()
    {
        Node targetRoot = ResolveEditorRefreshTargetRoot()
            ?? throw new InvalidOperationException(
                $"{GetType().Name} node '{Name}' could not resolve a target root for editor installation.");

        var context = new SceneInstallationContext(targetRoot);
        ClearInstallerOwnedOutputForEditorInstall(targetRoot, context);
        SceneInstallationResult result = Install(context);
        result.ThrowIfFailed();
    }

    /// <summary>
    /// Clears editor-installed output without registering a nested editor undo action.
    /// </summary>
    public void ClearInstallInEditorOutputWithoutUndo()
    {
        Node targetRoot = ResolveEditorRefreshTargetRoot()
            ?? throw new InvalidOperationException(
                $"{GetType().Name} node '{Name}' could not resolve a target root for editor installation undo.");

        var context = new SceneInstallationContext(targetRoot);
        ClearInstallerOwnedOutputForEditorInstall(targetRoot, context);
    }

    /// <summary>
    /// Clears the materialised output that this installer refreshes during editor installation.
    /// </summary>
    protected virtual void ClearInstallerOwnedOutputForEditorInstall(Node targetRoot, SceneInstallationContext context)
        => ClearInstallerOwnedOutput(targetRoot, context, this);

    /// <summary>
    /// Resolves the target root used by editor installation and automatic installs.
    /// </summary>
    protected virtual Node? ResolveEditorRefreshTargetRoot(SceneInstallationContext? context = null)
    {
        Node? parent = GetParent();
        return TargetRoot
            ?? (parent is SceneInstaller parentInstaller && !ReferenceEquals(parentInstaller, this)
                ? parentInstaller.ResolveEditorRefreshTargetRoot(context)
                : parent)
            ?? Owner
            ?? (IsInsideTree() ? GetTree()?.CurrentScene : null)
            ?? context?.TargetRoot;
    }

    private bool TryCommitEditorUndoInstallAction()
    {
        if (!Engine.IsEditorHint())
        {
            return false;
        }

#if TOOLS
        EditorInterface? editorInterface = EditorInterface.Singleton;
        EditorUndoRedoManager? undoRedo = editorInterface?.GetEditorUndoRedo();
        if (undoRedo is null)
        {
            return false;
        }

        // Godot exposes editor undo/redo to tool scripts through EditorInterface; keep this at the
        // InstallInEditor boundary so runtime and direct test installs retain their existing behaviour.
        undoRedo.CreateAction($"Install {Name} In Editor");
        undoRedo.AddDoMethod(this, MethodName.InstallNowInEditorWithoutUndo);
        undoRedo.AddUndoMethod(this, MethodName.ClearInstallInEditorOutputWithoutUndo);
        undoRedo.CommitAction();
        return true;
#else
        return false;
#endif
    }

    /// <summary>
    /// Removes materialised output marked as installer-owned while preserving unmanaged authored nodes.
    /// </summary>
    protected static void ClearInstallerOwnedOutput(Node targetRoot)
    {
        List<Node> nodesToRemove = [];
        CollectTopLevelInstallerOwnedNodes(targetRoot, parentOwned: false, nodesToRemove);
        foreach (Node node in nodesToRemove)
        {
            Node? parent = node.GetParent();
            parent?.RemoveChild(node);
            node.QueueFree();
        }
    }

    /// <summary>
    /// Removes materialised output marked as owned by one installer while preserving unmanaged and sibling-owned nodes.
    /// </summary>
    protected static void ClearInstallerOwnedOutput(Node targetRoot, SceneInstallationContext context, ISceneInstaller installer)
    {
        List<Node> nodesToRemove = [];
        CollectTopLevelInstallerOwnedNodes(targetRoot, context, installer, parentOwned: false, nodesToRemove);
        foreach (Node node in nodesToRemove)
        {
            Node? parent = node.GetParent();
            parent?.RemoveChild(node);
            node.QueueFree();
        }
    }

    private static void CollectTopLevelInstallerOwnedNodes(Node node, bool parentOwned, List<Node> nodesToRemove)
    {
        bool nodeOwned = SceneInstallationMetadata.HasAnyInstalledMarker(node);
        if (nodeOwned && !parentOwned)
        {
            nodesToRemove.Add(node);
            return;
        }

        foreach (Node child in node.GetChildren())
        {
            CollectTopLevelInstallerOwnedNodes(child, nodeOwned, nodesToRemove);
        }
    }

    private static void CollectTopLevelInstallerOwnedNodes(
        Node node,
        SceneInstallationContext context,
        ISceneInstaller installer,
        bool parentOwned,
        List<Node> nodesToRemove)
    {
        bool nodeOwned = SceneInstallationMetadata.HasInstalled(node, context, installer);
        if (nodeOwned && !parentOwned)
        {
            nodesToRemove.Add(node);
            return;
        }

        foreach (Node child in node.GetChildren())
        {
            CollectTopLevelInstallerOwnedNodes(child, context, installer, nodeOwned, nodesToRemove);
        }
    }
}
