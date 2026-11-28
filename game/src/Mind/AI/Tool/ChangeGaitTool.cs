using AlleyCat.Animations;
using Godot;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>The <c>change_gait</c> tool: sets the character's standing locomotion preference.</summary>
[Tool]
[GlobalClass]
public partial class ChangeGaitTool : AnimationPreferenceTool
{
    /// <summary>Model-facing name of the gait tool.</summary>
    internal const string ProductionToolName = "change_gait";

    private const string DefaultDescription =
        "Change the way you move, giving the id of one of the gaits listed for you. It applies to your current or "
        + "next movement. The call returns at once and is not a pause.";

    /// <summary>Creates the tool with its default model-facing metadata for editor authoring.</summary>
    public ChangeGaitTool()
    {
        ToolName = ProductionToolName;
        ToolDescription = DefaultDescription;
    }

    /// <summary>Creates the tool bound to the character's variant control.</summary>
    internal ChangeGaitTool(IAnimationVariantControl control) : base(control)
    {
        ToolName = ProductionToolName;
        ToolDescription = DefaultDescription;
    }

    /// <inheritdoc />
    internal override AnimationCategory Category => AnimationCategory.Locomotion;
}
