using Xunit;

namespace AlleyCat.Tests.Interaction;

/// <summary>
/// Static coverage for the editor-only INTR-001 grab-point authoring gizmos.
/// </summary>
public sealed class CylindricalGrabPointEditorGizmoTests
{
    /// <summary>
    /// The project-owned editor addon is registered and enabled for convenient authoring.
    /// </summary>
    [Fact]
    public void AlleyCatEditorAddon_IsConfiguredAndEnabled()
    {
        string repositoryRoot = GetRepositoryRoot();
        string projectConfig = File.ReadAllText(Path.Combine(repositoryRoot, "game", "project.godot"));
        string pluginConfig = File.ReadAllText(Path.Combine(repositoryRoot, "game", "addons", "alleycat_editor", "plugin.cfg"));
        string pluginScript = File.ReadAllText(Path.Combine(repositoryRoot, "game", "addons", "alleycat_editor", "plugin.gd"));

        Assert.Contains("res://addons/alleycat_editor/plugin.cfg", projectConfig, StringComparison.Ordinal);
        Assert.Contains("script=\"plugin.gd\"", pluginConfig, StringComparison.Ordinal);
        Assert.Contains("add_node_3d_gizmo_plugin", pluginScript, StringComparison.Ordinal);
        Assert.Contains("remove_node_3d_gizmo_plugin", pluginScript, StringComparison.Ordinal);
        Assert.Contains("cylindrical_grab_point_gizmo_plugin.gd", pluginScript, StringComparison.Ordinal);
        Assert.Contains("spherical_grab_point_gizmo_plugin.gd", pluginScript, StringComparison.Ordinal);
        Assert.Contains("_cylindrical_grab_point_gizmo_plugin = CylindricalGrabPointGizmoPlugin.new(selection)", pluginScript, StringComparison.Ordinal);
        Assert.Contains("_spherical_grab_point_gizmo_plugin = SphericalGrabPointGizmoPlugin.new(selection)", pluginScript, StringComparison.Ordinal);
        Assert.Contains("_last_selected_nodes = selection.get_selected_nodes()", pluginScript, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gizmo script targets CylindricalGrabPoint and includes the required selected-only visual cue contracts.
    /// </summary>
    [Fact]
    public void CylindricalGrabPointGizmoScript_DefinesSelectedOnlyAuthoringCues()
    {
        string repositoryRoot = GetRepositoryRoot();
        string gizmoScript = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "game",
            "addons",
            "alleycat_editor",
            "cylindrical_grab_point_gizmo_plugin.gd"));

        Assert.Contains("CylindricalGrabPoint.cs", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("_is_selected", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("LengthMetres", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("ReachDistanceMetres", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("SnapDistanceMetres", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("GrabPointPositionOffsetFromHand", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("GrabPointRotationOffsetFromHand", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("affine_inverse", gizmoScript, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gizmo script targets SphericalGrabPoint and includes the required selected-only visual cue contracts.
    /// </summary>
    [Fact]
    public void SphericalGrabPointGizmoScript_DefinesSelectedOnlyAuthoringCues()
    {
        string repositoryRoot = GetRepositoryRoot();
        string gizmoScript = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "game",
            "addons",
            "alleycat_editor",
            "spherical_grab_point_gizmo_plugin.gd"));

        Assert.Contains("SphericalGrabPoint.cs", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("_is_selected", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("ReachDistanceMetres", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("_draw_origin_marker", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("_draw_wire_sphere", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("PalmLocalDirection", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("hand-local", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("runtime hand transform", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("GrabPointPositionOffsetFromHand", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("GrabPointRotationOffsetFromHand", gizmoScript, StringComparison.Ordinal);
        Assert.Contains("affine_inverse", gizmoScript, StringComparison.Ordinal);
    }

    private static string GetRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AlleyCat.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
