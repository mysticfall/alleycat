using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AlleyCat.UI;
using Godot;

namespace AlleyCat.Testing;

/// <summary>
/// Test-only GDScript bridge that forces a <see cref="GameMenu" /> into its screenshot scenarios and exposes
/// non-visual state probes for pre-capture assertions.
/// </summary>
/// <remarks>
/// <para>
/// The menu exposes no public open or navigate surface because it is driven exclusively by XR controller events,
/// which are unavailable in a windowed visual-fixture run with XR disabled. This driver invokes the same private
/// methods (<c>OpenMenu</c> and <c>NavigateSelection</c>) that the XR input handlers call, so captured screenshots
/// show production visuals rather than re-authored stand-ins.
/// </para>
/// </remarks>
[GlobalClass]
public sealed partial class GameMenuVisualStateDriver : Node
{
    private static readonly MethodInfo _openMenuMethod = GetRequiredMethod("OpenMenu");

    private static readonly MethodInfo _navigateSelectionMethod = GetRequiredMethod("NavigateSelection");

    /// <summary>
    /// Returns whether the menu reports itself open.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance methods are required for GDScript binding.")]
    public bool GetIsOpen(Node menuNode) => ResolveMenu(menuNode).IsOpen;

    /// <summary>
    /// Returns the index of the menu's currently selected option.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance methods are required for GDScript binding.")]
    public int GetSelectedOption(Node menuNode) => (int)ResolveMenu(menuNode).SelectedOption;

    /// <summary>
    /// Opens the menu through its real open path; returns whether the menu ended up open and visible.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance methods are required for GDScript binding.")]
    public bool TryOpenMenu(Node menuNode)
    {
        GameMenu menu = ResolveMenu(menuNode);
        _ = _openMenuMethod.Invoke(menu, null);
        return menu.IsOpen && menu.Visible;
    }

    /// <summary>
    /// Navigates the selection one step downwards through the real navigation path; returns the resulting option.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance methods are required for GDScript binding.")]
    public int NavigateSelectionDown(Node menuNode)
    {
        GameMenu menu = ResolveMenu(menuNode);
        _ = _navigateSelectionMethod.Invoke(menu, [1]);
        return (int)menu.SelectedOption;
    }

    private static GameMenu ResolveMenu(Node menuNode)
        => menuNode as GameMenu
            ?? throw new InvalidOperationException(
                $"Node '{menuNode?.GetPath()}' is not a {nameof(GameMenu)}.");

    private static MethodInfo GetRequiredMethod(string methodName)
        => typeof(GameMenu).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameMenu no longer exposes the private method '{methodName}' required by the visual fixture.");
}
