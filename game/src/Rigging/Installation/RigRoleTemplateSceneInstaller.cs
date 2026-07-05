using AlleyCat.Core.Installer;
using Godot;

namespace AlleyCat.Rigging.Installation;

/// <summary>
/// Top-level rig installer that exposes a shared role template to child module installers.
/// </summary>
[Tool]
[GlobalClass]
public partial class RigRoleTemplateSceneInstaller : SceneInstaller
{
    private RigSceneInstaller[] InstallersValue { get; set; } = [];

    private bool _automaticallyInstalled;
    private bool _pendingAutomaticInstall;

    /// <summary>
    /// Gets or sets the role/template scene instantiated once for the top-level installation pass.
    /// </summary>
    [ExportGroup("Template")]
    [Export]
    public PackedScene? Template
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the baseline scene inherited by the role template. When assigned, template subtree installers copy
    /// only nodes that the role template adds above this baseline, preventing full reference-character duplication.
    /// </summary>
    [Export]
    public PackedScene? TemplateBaseline
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets an optional path to the skeleton inside the target character root.
    /// </summary>
    [Export]
    public NodePath TargetSkeletonPath { get; set; } = new();

    /// <summary>
    /// Gets or sets an optional path to the skeleton inside the role template root.
    /// </summary>
    [Export]
    public NodePath TemplateSkeletonPath { get; set; } = new();

    /// <summary>
    /// Gets or sets the explicit rig module installers run in order for this role.
    /// </summary>
    [Export]
    public RigSceneInstaller[] Installers
    {
        get => InstallersValue.Length > 0 ? InstallersValue : GetDirectChildInstallers();
        set => InstallersValue = value ?? [];
    }

    /// <summary>
    /// Gets or sets whether this role coordinator installs itself during <see cref="Node._Ready" />.
    /// </summary>
    [Export]
    public bool AutoInstallOnReady
    {
        get; set;
    }

    /// <inheritdoc />
    public override void _Notification(int what)
    {
        if (what is (int)NotificationParented or (int)NotificationSceneInstantiated)
        {
            TryAutoInstall();
        }
    }

    /// <inheritdoc />
    public override void _EnterTree()
        => TryAutoInstall();

    /// <inheritdoc />
    public override void _Ready()
        => TryAutoInstall();

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        _ = delta;

        if (!_pendingAutomaticInstall)
        {
            SetProcess(false);
            return;
        }

        InstallDeferred();
    }

    /// <inheritdoc />
    public override SceneInstallationResult Install(SceneInstallationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Template is null)
        {
            return SceneInstallationResult.Failed(
                $"Rig role template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(this)}' requires an assigned PackedScene template.");
        }

        Skeleton3D targetSkeleton;
        Skeleton3D templateSkeleton;
        Node ownedTemplateRoot = Template.Instantiate();
        Node? ownedTemplateBaselineRoot = TemplateBaseline?.Instantiate();
        Node templateRoot = ownedTemplateRoot;
        try
        {
            targetSkeleton = RigInstallationContext.ResolveSkeleton(context.TargetRoot, TargetSkeletonPath, "target");
            templateSkeleton = RigInstallationContext.ResolveSkeleton(templateRoot, TemplateSkeletonPath, "template");
        }
        catch (InvalidOperationException ex)
        {
            ownedTemplateBaselineRoot?.Free();
            ownedTemplateRoot.Free();
            return SceneInstallationResult.Failed(ex.Message);
        }

        var characterContext = new RigInstallationContext(
            context.TargetRoot,
            context.MetadataNamespace,
            templateRoot,
            targetSkeleton,
            templateSkeleton,
            ownedTemplateBaselineRoot);

        try
        {
            return InstallCharacterModules(characterContext);
        }
        finally
        {
            ownedTemplateBaselineRoot?.Free();
            ownedTemplateRoot.Free();
        }
    }

    /// <inheritdoc />
    protected override void ClearInstallerOwnedOutputForEditorInstall(Node targetRoot, SceneInstallationContext context)
    {
        _ = context;

        ClearInstallerOwnedOutput(targetRoot);
    }

    private SceneInstallationResult InstallCharacterModules(RigInstallationContext context)
    {
        Node? targetRoot = ResolveEditorRefreshTargetRoot(context);
        RigInstallationContext delegatedContext = targetRoot is null ? context : context.WithTargetRoot(targetRoot);
        List<SceneInstallationResult> results = [];

        RigSceneInstaller[] installers = Installers;
        for (int index = 0; index < installers.Length; index++)
        {
            RigSceneInstaller? installer = installers[index];
            if (installer is null)
            {
                results.Add(SceneInstallationResult.Failed(
                    $"{DescribeInstaller(this)} has a null rig installer at ordered slot {index}."));
                continue;
            }

            if (ReferenceEquals(installer, this))
            {
                results.Add(SceneInstallationResult.Failed(
                    $"{DescribeInstaller(this)} cannot delegate to itself at ordered slot {index}."));
                continue;
            }

            SceneInstallationResult result = installer.Install(delegatedContext);
            if (!result.Succeeded)
            {
                List<string> installerErrors =
                [
                    $"{DescribeInstaller(this)} failed while running {DescribeInstaller(installer)} at ordered slot {index}.",
                ];
                installerErrors.AddRange(result.Errors);
                results.Add(SceneInstallationResult.Failed([.. installerErrors]));
                continue;
            }

            results.Add(result);
        }

        return SceneInstallationResult.Merge(results);
    }

    private RigSceneInstaller[] GetDirectChildInstallers()
    {
        List<RigSceneInstaller> childInstallers = [];
        foreach (Node child in GetChildren())
        {
            if (child is RigSceneInstaller installer)
            {
                childInstallers.Add(installer);
            }
        }

        return [.. childInstallers];
    }

    private void TryAutoInstall()
    {
        if (!AutoInstallOnReady || _automaticallyInstalled || Engine.IsEditorHint())
        {
            return;
        }

        if (!IsInsideTree())
        {
            return;
        }

        _automaticallyInstalled = true;
        _pendingAutomaticInstall = true;
        SetProcess(true);
        _ = CallDeferred(MethodName.InstallDeferred);
    }

    private void InstallDeferred()
    {
        if (!IsInsideTree())
        {
            return;
        }

        Node targetRoot = ResolveEditorRefreshTargetRoot()
            ?? throw new InvalidOperationException(
                $"{nameof(RigRoleTemplateSceneInstaller)} node '{Name}' could not resolve a target root from "
                + $"{nameof(TargetRoot)}, its parent, owner, or current scene when {nameof(AutoInstallOnReady)} is enabled.");

        SceneInstallationResult result = Install(new SceneInstallationContext(targetRoot));
        result.ThrowIfFailed();

        _pendingAutomaticInstall = false;
        SetProcess(false);
    }

    private static string DescribeInstaller(SceneInstaller installer)
    {
        string installerKey = SceneInstallationMetadata.GetEffectiveInstallerKey(installer);
        return $"{installer.GetType().Name} '{installer.Name}' ({installer.GetPath()}) [{installerKey}]";
    }
}
