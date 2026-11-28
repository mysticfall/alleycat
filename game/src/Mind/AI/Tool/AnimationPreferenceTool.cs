using System.ComponentModel;
using AlleyCat.Animations;
using Godot;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Non-parking agent tool that sets the associated character's standing preference for one animation catalogue
/// category (ANIM-003 Mind Integration, Agent Tools). The model supplies only the entry id; the character's variant
/// control is trusted composition state bound at the AgenticMind boundary.
/// </summary>
[Tool]
[GlobalClass]
public abstract partial class AnimationPreferenceTool : AgentTool
{
    private readonly IAnimationVariantControl? _control;

    /// <summary>Creates an unbound tool for editor authoring; it fails clearly at use.</summary>
    protected AnimationPreferenceTool()
    {
    }

    /// <summary>Creates a tool bound to the character's variant control at composition.</summary>
    private protected AnimationPreferenceTool(IAnimationVariantControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        _control = control;
    }

    /// <summary>The catalogue category this tool sets.</summary>
    internal abstract AnimationCategory Category
    {
        get;
    }

    /// <inheritdoc />
    protected override Delegate CreateDelegate() => Change;

    private ValueTask<AgentToolResult> Change(
        ScenarioContext context,
        [Description("The exact id of one entry listed for you, as a bare string without backticks.")]
        string id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IAnimationVariantControl control = _control
            ?? throw new InvalidOperationException($"The {ToolName} tool requires its composition-bound character.");
        return ValueTask.FromResult(new AgentToolResult(AnimationPreferenceRequest.Apply(control, Category, id).Message));
    }
}
