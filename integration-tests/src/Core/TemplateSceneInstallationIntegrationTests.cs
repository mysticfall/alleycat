using AlleyCat.Core.Installer;
using AlleyCat.Rigging.Installation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Core;

/// <summary>
/// Integration coverage for reusable template-context and selected-subtree installer mechanics.
/// </summary>
public sealed class TemplateSceneInstallationIntegrationTests
{
    /// <summary>
    /// Selected-node mode copies the resolved source subtree without moving it out of the template source.
    /// </summary>
    [Headless]
    [Fact]
    public void Install_SelectedNode_CopiesSourceSubtreeToTargetParent()
    {
        using var target = new Node { Name = "Target" };
        var destination = new Node { Name = "Destination" };
        target.AddChild(destination);
        using PackedScene template = CreateTemplate();
        using Node templateRoot = template.Instantiate();
        using var installer = new TestTemplateSubtreeInstaller
        {
            Name = "SelectedNodeInstaller",
            InstallMode = TemplateInstallMode.SelectedNode,
            SourcePath = new NodePath("VisualRoot/SelectedNode"),
            TargetParentPath = new NodePath("Destination"),
        };
        var context = new TemplateSceneInstallationContext(target, SceneInstallationMetadata.DefaultNamespace, templateRoot);

        SceneInstallationResult result = installer.Install(context);

        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
        Assert.True(destination.HasNode("SelectedNode/AuthoredLeaf"));
        Node selectedNode = destination.GetNode("SelectedNode");
        Assert.True(SceneInstallationMetadata.HasInstalled(selectedNode, context, installer));
        Assert.True(SceneInstallationMetadata.HasInstalled(selectedNode.GetNode("AuthoredLeaf"), context, installer));

        using Node templateInstance = template.Instantiate();
        Assert.True(templateInstance.HasNode("VisualRoot/SelectedNode/AuthoredLeaf"));
    }

    /// <summary>
    /// Selected-node-children mode copies only the selected source node's direct children.
    /// </summary>
    [Headless]
    [Fact]
    public void Install_SelectedNodeChildren_CopiesSelectedChildrenOnly()
    {
        using var target = new Node { Name = "Target" };
        using PackedScene template = CreateTemplate();
        using Node templateRoot = template.Instantiate();
        using var installer = new TestTemplateSubtreeInstaller
        {
            InstallMode = TemplateInstallMode.SelectedNodeChildren,
            SourcePath = new NodePath("Modules"),
        };

        var context = new TemplateSceneInstallationContext(target, SceneInstallationMetadata.DefaultNamespace, templateRoot);

        SceneInstallationResult result = installer.Install(context);

        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
        Assert.True(target.HasNode("ModuleA"));
        Assert.True(target.HasNode("ModuleB"));
        Assert.False(target.HasNode("Modules"));
    }

    /// <summary>
    /// Template-root-children mode ignores root children already supplied by the configured baseline scene.
    /// </summary>
    [Headless]
    [Fact]
    public void Install_TemplateRootChildren_WithBaseline_CopiesOnlyTemplateAddedChildren()
    {
        using var target = new Node { Name = "Target" };
        using Node templateRoot = CreateBaselineDiffTemplateRoot();
        using Node baselineRoot = CreateBaselineRoot();
        using var installer = new TestTemplateSubtreeInstaller
        {
            InstallMode = TemplateInstallMode.TemplateRootChildren,
        };
        var context = new TemplateSceneInstallationContext(
            target,
            SceneInstallationMetadata.DefaultNamespace,
            templateRoot,
            baselineRoot);

        SceneInstallationResult result = installer.Install(context);

        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
        Assert.False(target.HasNode("ReferenceVisual"));
        Assert.True(target.HasNode("RuntimeModule"));
    }

    /// <summary>
    /// Selected-node-children mode ignores children already supplied by the corresponding baseline source node.
    /// </summary>
    [Headless]
    [Fact]
    public void Install_SelectedNodeChildren_WithBaseline_CopiesOnlyTemplateAddedChildren()
    {
        using var target = new Node { Name = "Target" };
        using Node templateRoot = CreateBaselineDiffTemplateRoot();
        using Node baselineRoot = CreateBaselineRoot();
        using var installer = new TestTemplateSubtreeInstaller
        {
            InstallMode = TemplateInstallMode.SelectedNodeChildren,
            SourcePath = new NodePath("ReferenceVisual/Skeleton"),
        };
        var context = new TemplateSceneInstallationContext(
            target,
            SceneInstallationMetadata.DefaultNamespace,
            templateRoot,
            baselineRoot);

        SceneInstallationResult result = installer.Install(context);

        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
        Assert.False(target.HasNode("BaselineAttachment"));
        Assert.True(target.HasNode("TemplateAttachment"));
    }

    /// <summary>
    /// Repeated selected-subtree installation reuses same-name/type nodes and preserves sibling installer output.
    /// </summary>
    [Headless]
    [Fact]
    public void Install_SelectedSubtreeRepeated_DoesNotDuplicateOrRemoveSiblingOutput()
    {
        using var target = new Node { Name = "Target" };
        using PackedScene template = CreateTemplate();
        using Node templateRoot = template.Instantiate();
        using var selectedInstaller = new TestTemplateSubtreeInstaller
        {
            Name = "SelectedInstaller",
            InstallMode = TemplateInstallMode.SelectedNode,
            SourcePath = new NodePath("VisualRoot/SelectedNode"),
        };
        using var siblingInstaller = new TestTemplateSubtreeInstaller
        {
            Name = "SiblingInstaller",
            InstallMode = TemplateInstallMode.SelectedNode,
            SourcePath = new NodePath("Modules/ModuleA"),
        };
        var context = new TemplateSceneInstallationContext(target, SceneInstallationMetadata.DefaultNamespace, templateRoot);

        SceneInstallationResult siblingResult = siblingInstaller.Install(context);
        SceneInstallationResult first = selectedInstaller.Install(context);
        SceneInstallationResult second = selectedInstaller.Install(context);

        Assert.True(siblingResult.Succeeded, string.Join('\n', siblingResult.Errors));
        Assert.True(first.Succeeded, string.Join('\n', first.Errors));
        Assert.True(second.Succeeded, string.Join('\n', second.Errors));
        Assert.Equal(1, CountDirectChildren(target, "SelectedNode"));
        Assert.Equal(1, CountDirectChildren(target, "ModuleA"));
        Assert.True(SceneInstallationMetadata.HasInstalled(target.GetNode("ModuleA"), context, siblingInstaller));
    }

    /// <summary>
    /// Reused same-name 3D nodes keep the authored template transform instead of retaining stale scene-instance axes.
    /// </summary>
    [Headless]
    [Fact]
    public void Install_ReusedNode3D_AppliesTemplateTransform()
    {
        using var target = new Node { Name = "Target" };
        var existingVisualRoot = new Node3D { Name = "VisualRoot" };
        target.AddChild(existingVisualRoot);
        using PackedScene template = CreateTemplate();
        using Node templateRoot = template.Instantiate();
        Node3D templateVisualRoot = templateRoot.GetNode<Node3D>("VisualRoot");
        templateVisualRoot.Transform = new Transform3D(Basis.FromEuler(new Vector3(0.0f, Mathf.Pi, 0.0f)), Vector3.Zero);
        using var installer = new TestTemplateSubtreeInstaller
        {
            Name = "VisualRootInstaller",
            InstallMode = TemplateInstallMode.SelectedNode,
            SourcePath = new NodePath("VisualRoot"),
        };
        var context = new TemplateSceneInstallationContext(target, SceneInstallationMetadata.DefaultNamespace, templateRoot);

        SceneInstallationResult result = installer.Install(context);

        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
        Assert.Same(existingVisualRoot, target.GetNode<Node3D>("VisualRoot"));
        AssertBasisApproximately(templateVisualRoot.Transform.Basis, existingVisualRoot.Transform.Basis);
        Assert.Equal(1, CountDirectChildren(target, "VisualRoot"));
    }

    /// <summary>
    /// A top-level character role template installer exposes its instantiated template to child installers.
    /// </summary>
    [Headless]
    [Fact]
    public void RigRoleTemplateSceneInstaller_ChildInstaller_ConsumesTemplateContext()
    {
        using var target = new Node { Name = "Target" };
        target.AddChild(new Skeleton3D { Name = "CharacterSkeleton" });
        using PackedScene template = CreateTemplate();
        using var visualInstaller = new RigTemplateSubtreeInstaller
        {
            Name = "VisualRootInstaller",
            InstallMode = TemplateInstallMode.SelectedNode,
            SourcePath = new NodePath("VisualRoot"),
        };
        using var childInstaller = new RigTemplateSubtreeInstaller
        {
            Name = "ChildModuleInstaller",
            InstallMode = TemplateInstallMode.SelectedNode,
            SourcePath = new NodePath("Modules/ModuleB"),
            TargetParentPath = new NodePath("VisualRoot"),
        };
        using var roleInstaller = new RigRoleTemplateSceneInstaller
        {
            Name = "RoleTemplateInstaller",
            Template = template,
            Installers = [visualInstaller, childInstaller],
        };

        SceneInstallationResult first = roleInstaller.Install(new SceneInstallationContext(target));
        SceneInstallationResult second = roleInstaller.Install(new SceneInstallationContext(target));

        Assert.True(first.Succeeded, string.Join('\n', first.Errors));
        Assert.True(second.Succeeded, string.Join('\n', second.Errors));
        Assert.True(target.HasNode("VisualRoot/ModuleB"));
        Assert.Equal(1, CountDirectChildren(target, "VisualRoot"));
        Assert.Equal(1, CountDirectChildren(target.GetNode("VisualRoot"), "ModuleB"));
        Assert.False(HasExportedProperty(visualInstaller, "Template"));
        Assert.False(HasExportedProperty(childInstaller, "Template"));
    }

    /// <summary>
    /// Template-local node references are rebased before newly installed runtime nodes enter the scene tree.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Install_TemplateRootChildren_RebasesExportedReferencesBeforeReady()
    {
        SceneTree sceneTree = Support.TestUtils.GetSceneTree();
        var target = new Node { Name = "Target" };
        sceneTree.Root.AddChild(target);

        try
        {
            using Node templateRoot = CreateReadyProbeTemplateRoot();
            using var installer = new TestTemplateSubtreeInstaller
            {
                InstallMode = TemplateInstallMode.TemplateRootChildren,
            };
            var context = new TemplateSceneInstallationContext(target, SceneInstallationMetadata.DefaultNamespace, templateRoot);
            Assert.Same(templateRoot.GetNode("Reference"), templateRoot.GetNode<ReadyReferenceProbe>("Probe").Target);

            SceneInstallationResult result = installer.Install(context);
            await Support.TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.True(result.Succeeded, string.Join('\n', result.Errors));
            Node targetReference = target.GetNode("Reference");
            ReadyReferenceProbe probe = target.GetNode<ReadyReferenceProbe>("Probe");

            Assert.Same(targetReference, probe.Target);
            Assert.False(probe.TargetWasAssignedInsideTree);
        }
        finally
        {
            target.QueueFree();
        }
    }

    /// <summary>
    /// References inside a copied source subtree are mapped to the duplicate candidate before lifecycle callbacks run.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Install_SelectedNode_RebasesOwnDescendantReferenceBeforeReady()
    {
        SceneTree sceneTree = Support.TestUtils.GetSceneTree();
        var target = new Node { Name = "Target" };
        sceneTree.Root.AddChild(target);

        try
        {
            using Node templateRoot = CreateOwnDescendantProbeTemplateRoot();
            using var installer = new TestTemplateSubtreeInstaller
            {
                InstallMode = TemplateInstallMode.SelectedNode,
                SourcePath = new NodePath("Probe"),
            };
            var context = new TemplateSceneInstallationContext(target, SceneInstallationMetadata.DefaultNamespace, templateRoot);
            Node templateChild = templateRoot.GetNode("Probe/ChildReference");
            Assert.Same(templateChild, templateRoot.GetNode<ReadyReferenceProbe>("Probe").Target);

            SceneInstallationResult result = installer.Install(context);
            await Support.TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.True(result.Succeeded, string.Join('\n', result.Errors));
            ReadyReferenceProbe probe = target.GetNode<ReadyReferenceProbe>("Probe");
            Node runtimeChild = target.GetNode("Probe/ChildReference");
            Assert.Same(runtimeChild, probe.Target);
            Assert.False(probe.TargetWasAssignedInsideTree);
        }
        finally
        {
            target.QueueFree();
        }
    }

    /// <summary>
    /// References to later TemplateRootChildren siblings are mapped to runtime candidates rather than template nodes.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Install_TemplateRootChildren_RebasesLaterSiblingReferenceBeforeReady()
    {
        SceneTree sceneTree = Support.TestUtils.GetSceneTree();
        var target = new Node { Name = "Target" };
        sceneTree.Root.AddChild(target);

        try
        {
            using Node templateRoot = CreateLaterSiblingProbeTemplateRoot();
            using var installer = new TestTemplateSubtreeInstaller
            {
                InstallMode = TemplateInstallMode.TemplateRootChildren,
            };
            var context = new TemplateSceneInstallationContext(target, SceneInstallationMetadata.DefaultNamespace, templateRoot);
            Node templateReference = templateRoot.GetNode("Reference");
            Assert.Same(templateReference, templateRoot.GetNode<ReadyReferenceProbe>("Probe").Target);

            SceneInstallationResult result = installer.Install(context);
            await Support.TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.True(result.Succeeded, string.Join('\n', result.Errors));
            ReadyReferenceProbe probe = target.GetNode<ReadyReferenceProbe>("Probe");
            Node runtimeReference = target.GetNode("Reference");
            Assert.Same(runtimeReference, probe.Target);
            Assert.False(probe.TargetWasAssignedInsideTree);
        }
        finally
        {
            target.QueueFree();
        }
    }

    /// <summary>
    /// Reused existing nodes refresh exported references from the original source template, not from stale candidates.
    /// </summary>
    [Headless]
    [Fact]
    public void Install_ReusedExistingNode_RefreshesExportedReferenceFromTemplateSource()
    {
        using var target = new Node { Name = "Target" };
        var existingProbe = new ReadyReferenceProbe { Name = "Probe" };
        var existingChild = new Node { Name = "ChildReference" };
        var staleReference = new Node { Name = "StaleReference" };
        existingProbe.Target = staleReference;
        existingProbe.AddChild(existingChild);
        target.AddChild(existingProbe);
        target.AddChild(staleReference);
        using Node templateRoot = CreateOwnDescendantProbeTemplateRoot();
        using var installer = new TestTemplateSubtreeInstaller
        {
            InstallMode = TemplateInstallMode.SelectedNode,
            SourcePath = new NodePath("Probe"),
        };
        var context = new TemplateSceneInstallationContext(target, SceneInstallationMetadata.DefaultNamespace, templateRoot);

        SceneInstallationResult result = installer.Install(context);

        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
        Assert.Same(existingProbe, target.GetNode<ReadyReferenceProbe>("Probe"));
        Assert.Same(existingChild, existingProbe.Target);
        Assert.NotSame(staleReference, existingProbe.Target);
    }

    /// <summary>
    /// Reused subtrees are reconciled by authored child identity so new descendants appear automatically.
    /// </summary>
    [Headless]
    [Fact]
    public void Install_ReusedExistingSubtree_AddsMissingDescendantAndRebasesReferences()
    {
        using var target = new Node { Name = "Target" };
        var existingProbe = new ReadyReferenceProbe { Name = "Probe" };
        var preservedSibling = new Node { Name = "InstallerOwnedSibling" };
        SceneInstallationMetadata.MarkInstalled(preservedSibling, new SceneInstallationContext(target), new TestTemplateSubtreeInstaller { Name = "OtherInstaller" });
        existingProbe.AddChild(preservedSibling);
        target.AddChild(existingProbe);
        using Node templateRoot = CreateOwnDescendantProbeTemplateRoot();
        using var installer = new TestTemplateSubtreeInstaller
        {
            InstallMode = TemplateInstallMode.SelectedNode,
            SourcePath = new NodePath("Probe"),
        };
        var context = new TemplateSceneInstallationContext(target, SceneInstallationMetadata.DefaultNamespace, templateRoot);

        SceneInstallationResult first = installer.Install(context);
        SceneInstallationResult second = installer.Install(context);

        Assert.True(first.Succeeded, string.Join('\n', first.Errors));
        Assert.True(second.Succeeded, string.Join('\n', second.Errors));
        Node childReference = existingProbe.GetNode("ChildReference");
        Assert.Same(existingProbe, target.GetNode<ReadyReferenceProbe>("Probe"));
        Assert.Same(childReference, existingProbe.Target);
        Assert.True(existingProbe.HasNode("InstallerOwnedSibling"));
        Assert.True(SceneInstallationMetadata.HasInstalled(childReference, context, installer));
        Assert.Equal(1, CountDirectChildren(existingProbe, "ChildReference"));
    }

    private static PackedScene CreateTemplate()
    {
        var root = new Node { Name = "TemplateRoot" };
        var visualRoot = new Node3D { Name = "VisualRoot" };
        var selectedNode = new Node3D { Name = "SelectedNode" };
        selectedNode.AddChild(new Marker3D { Name = "AuthoredLeaf" });
        visualRoot.AddChild(selectedNode);
        root.AddChild(visualRoot);

        var modules = new Node { Name = "Modules" };
        modules.AddChild(new Node3D { Name = "ModuleA" });
        modules.AddChild(new Marker3D { Name = "ModuleB" });
        root.AddChild(modules);
        root.AddChild(new Skeleton3D { Name = "TemplateSkeleton" });

        AssignTemplateOwnerRecursively(root, root);
        var packedScene = new PackedScene();
        Assert.Equal(Error.Ok, packedScene.Pack(root));
        root.Dispose();
        return packedScene;
    }

    private static Node CreateReadyProbeTemplateRoot()
    {
        var root = new Node { Name = "TemplateRoot" };
        var reference = new Node { Name = "Reference" };
        var probe = new ReadyReferenceProbe { Name = "Probe", Target = reference };
        root.AddChild(reference);
        root.AddChild(probe);
        return root;
    }

    private static Node CreateOwnDescendantProbeTemplateRoot()
    {
        var root = new Node { Name = "TemplateRoot" };
        var probe = new ReadyReferenceProbe { Name = "Probe" };
        var reference = new Node { Name = "ChildReference" };
        probe.AddChild(reference);
        probe.Target = reference;
        root.AddChild(probe);
        return root;
    }

    private static Node CreateLaterSiblingProbeTemplateRoot()
    {
        var root = new Node { Name = "TemplateRoot" };
        var reference = new Node { Name = "Reference" };
        var probe = new ReadyReferenceProbe { Name = "Probe", Target = reference };
        root.AddChild(probe);
        root.AddChild(reference);
        return root;
    }

    private static Node CreateBaselineRoot()
    {
        var root = new Node { Name = "TemplateRoot" };
        var visual = new Node3D { Name = "ReferenceVisual" };
        var skeleton = new Skeleton3D { Name = "Skeleton" };
        skeleton.AddChild(new Marker3D { Name = "BaselineAttachment" });
        visual.AddChild(skeleton);
        root.AddChild(visual);
        return root;
    }

    private static Node CreateBaselineDiffTemplateRoot()
    {
        Node root = CreateBaselineRoot();
        root.GetNode("ReferenceVisual/Skeleton").AddChild(new Marker3D { Name = "TemplateAttachment" });
        root.AddChild(new Node3D { Name = "RuntimeModule" });
        return root;
    }

    private static void AssignTemplateOwnerRecursively(Node owner, Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            child.Owner = owner;
            AssignTemplateOwnerRecursively(owner, child);
        }
    }

    private static int CountDirectChildren(Node parent, string childName)
    {
        int count = 0;
        for (int index = 0; index < parent.GetChildCount(); index++)
        {
            if (parent.GetChild(index).Name == childName)
            {
                count++;
            }
        }

        return count;
    }

    private static bool HasExportedProperty(Node node, string propertyName)
    {
        foreach (Godot.Collections.Dictionary property in node.GetPropertyList())
        {
            if (property["name"].AsString() == propertyName)
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertBasisApproximately(Basis expected, Basis actual)
    {
        const float tolerance = 0.0001f;
        Assert.True(expected.X.DistanceTo(actual.X) <= tolerance, $"Expected X basis {expected.X}, got {actual.X}.");
        Assert.True(expected.Y.DistanceTo(actual.Y) <= tolerance, $"Expected Y basis {expected.Y}, got {actual.Y}.");
        Assert.True(expected.Z.DistanceTo(actual.Z) <= tolerance, $"Expected Z basis {expected.Z}, got {actual.Z}.");
    }

    private sealed partial class TestTemplateSubtreeInstaller : SceneInstaller, ISceneInstaller<TemplateSceneInstallationContext>
    {
        public TemplateInstallMode InstallMode { get; init; } = TemplateInstallMode.TemplateRoot;

        public NodePath SourcePath { get; init; } = new();

        public NodePath TargetParentPath { get; init; } = new();

        public SceneInstallationResult Install(TemplateSceneInstallationContext context)
            => TemplateSceneInstallation.Install(this, context, ResolveTargetParent(context), InstallMode, SourcePath);

        public override SceneInstallationResult Install(SceneInstallationContext context)
            => context is TemplateSceneInstallationContext templateContext
                ? Install(templateContext)
                : SceneInstallationResult.Failed(
                    $"Test template installer requires a {nameof(TemplateSceneInstallationContext)}.");

        private Node? ResolveTargetParent(TemplateSceneInstallationContext context)
            => string.IsNullOrWhiteSpace(TargetParentPath.ToString())
                ? context.TargetRoot
                : context.TargetRoot.GetNodeOrNull(TargetParentPath);
    }

    /// <summary>
    /// Test probe that caches an exported node reference when it enters the scene tree.
    /// </summary>
    [GlobalClass]
    public sealed partial class ReadyReferenceProbe : Node
    {
        /// <summary>
        /// Exported reference authored in the source template.
        /// </summary>
        [Export]
        public Node? Target
        {
            get;
            set
            {
                field = value;
                TargetWasAssignedInsideTree |= IsInsideTree();
            }
        }

        /// <summary>
        /// Whether the exported target was assigned after the probe entered the scene tree.
        /// </summary>
        public bool TargetWasAssignedInsideTree
        {
            get; private set;
        }

    }
}
