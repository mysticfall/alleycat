using AlleyCat.Scene;
using AlleyCat.Templating;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Vision;

/// <summary>
/// A visual cue with a fixed, authored description template and origin-based position sampling.
/// </summary>
[GlobalClass]
public sealed partial class StaticVisualCue : VisualCue
{
    private ITemplate? _compiledTemplate;
    private string? _compiledDescription;

    /// <summary>
    /// Gets or sets the authored description template source.
    /// </summary>
    [Export(PropertyHint.MultilineText)]
    public string Description { get; set; } = string.Empty;

    /// <inheritdoc />
    public override Vector3 SampleGlobalPosition() => GlobalPosition;

    /// <inheritdoc />
    public override string Describe(ISceneContext scene, IHasVision observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(scene);

        Dictionary<string, object?> root = new(StringComparer.Ordinal)
        {
            ["scene"] = scene,
            ["observer"] = observer,
            ["cue"] = this,
        };

        IVisualSubject? subject = FindNearestSubject();
        if (subject is not null)
        {
            root["subject"] = subject;
        }

        return GetTemplate().Render(root);
    }

    private ITemplate GetTemplate()
    {
        if (_compiledTemplate is not null && string.Equals(_compiledDescription, Description, StringComparison.Ordinal))
        {
            return _compiledTemplate;
        }

        ITemplateCompiler compiler = Game.Instance.GetRequiredService<ITemplateCompiler>();
        _compiledTemplate = compiler.Compile(Description);
        _compiledDescription = Description;
        return _compiledTemplate;
    }

    private IVisualSubject? FindNearestSubject()
    {
        Node? ancestor = GetParent();
        while (ancestor is not null)
        {
            if (ancestor is IVisualSubject subject)
            {
                return subject;
            }

            ancestor = ancestor.GetParent();
        }

        return null;
    }
}
