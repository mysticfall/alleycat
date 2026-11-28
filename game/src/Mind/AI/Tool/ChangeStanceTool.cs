using AlleyCat.Animations;
using Godot;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>The <c>change_stance</c> tool: sets the character's standing idle preference.</summary>
[Tool]
[GlobalClass]
public partial class ChangeStanceTool : AnimationPreferenceTool
{
    /// <summary>Model-facing name of the stance tool.</summary>
    internal const string ProductionToolName = "change_stance";

    private const string DefaultDescription =
        "Change how you stand when you are not moving, giving the id of one of the stances listed for you. You shift "
        + "into it while standing still, or once you next stop. The call returns at once and is not a pause.";

    /// <summary>Creates the tool with its default model-facing metadata for editor authoring.</summary>
    public ChangeStanceTool()
    {
        ToolName = ProductionToolName;
        ToolDescription = DefaultDescription;
    }

    /// <summary>Creates the tool bound to the character's variant control.</summary>
    internal ChangeStanceTool(IAnimationVariantControl control) : base(control)
    {
        ToolName = ProductionToolName;
        ToolDescription = DefaultDescription;
    }

    /// <inheritdoc />
    internal override AnimationCategory Category => AnimationCategory.Idle;
}
