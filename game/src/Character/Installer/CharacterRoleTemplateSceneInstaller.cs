using AlleyCat.Core.Installer;
using Godot;

namespace AlleyCat.Character.Installer;

/// <summary>
/// Top-level character installer that exposes a shared role template to child module installers.
/// </summary>
[Tool]
[GlobalClass]
public partial class CharacterRoleTemplateSceneInstaller : SceneInstaller
{
    private CharacterSceneInstaller[] InstallersValue { get; set; } = [];

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
    /// Gets or sets the explicit character module installers run in order for this role.
    /// </summary>
    [Export]
    public CharacterSceneInstaller[] Installers
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
                $"Character role template installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(this)}' requires an assigned PackedScene template.");
        }

        Skeleton3D targetSkeleton;
        Skeleton3D templateSkeleton;
        Node ownedTemplateRoot = Template.Instantiate();
        Node templateRoot = ownedTemplateRoot;
        try
        {
            targetSkeleton = CharacterInstallationContext.ResolveSkeleton(context.TargetRoot, TargetSkeletonPath, "target");
            templateSkeleton = CharacterInstallationContext.ResolveSkeleton(templateRoot, TemplateSkeletonPath, "template");
        }
        catch (InvalidOperationException ex)
        {
            ownedTemplateRoot.Free();
            return SceneInstallationResult.Failed(ex.Message);
        }

        var characterContext = new CharacterInstallationContext(
            context.TargetRoot,
            context.MetadataNamespace,
            templateRoot,
            targetSkeleton,
            templateSkeleton);

        try
        {
            return InstallCharacterModules(characterContext);
        }
        finally
        {
            ownedTemplateRoot.Free();
        }
    }

    /// <inheritdoc />
    protected override void ClearInstallerOwnedOutputForEditorInstall(Node targetRoot, SceneInstallationContext context)
    {
        _ = context;

        ClearInstallerOwnedOutput(targetRoot);
    }

    private SceneInstallationResult InstallCharacterModules(CharacterInstallationContext context)
    {
        Node? targetRoot = ResolveEditorRefreshTargetRoot(context);
        CharacterInstallationContext delegatedContext = targetRoot is null ? context : context.WithTargetRoot(targetRoot);
        List<SceneInstallationResult> results = [];

        CharacterSceneInstaller[] installers = Installers;
        for (int index = 0; index < installers.Length; index++)
        {
            CharacterSceneInstaller? installer = installers[index];
            if (installer is null)
            {
                results.Add(SceneInstallationResult.Failed(
                    $"{DescribeInstaller(this)} has a null character installer at ordered slot {index}."));
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

    private CharacterSceneInstaller[] GetDirectChildInstallers()
    {
        List<CharacterSceneInstaller> childInstallers = [];
        foreach (Node child in GetChildren())
        {
            if (child is CharacterSceneInstaller installer)
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
                $"{nameof(CharacterRoleTemplateSceneInstaller)} node '{Name}' could not resolve a target root from "
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
