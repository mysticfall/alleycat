using AlleyCat.Core.Content;
using Godot;

namespace AlleyCat.Animations;

/// <summary>
/// One animation pack in a character catalogue. Derived templates add pack nodes, or disable inherited ones by
/// overriding <see cref="Enabled" /> (ANIM-003 TR6).
/// </summary>
[GlobalClass]
public partial class AnimationPack : Node
{
    /// <summary>The resource path of the pack's <c>pack.json</c> manifest; authoritative for the pack location.</summary>
    [Export(PropertyHint.File, "*.json")]
    public string Manifest { get; set; } = string.Empty;

    /// <summary>Whether the pack contributes to the catalogue.</summary>
    [Export]
    public bool Enabled { get; set; } = true;

    /// <summary>Application priority; lower priorities apply first, so higher priorities override.</summary>
    [Export]
    public int Priority
    {
        get; set;
    }

    // Test seam: a fixture can stand in for a content pack without being installed under res://content/.
    internal ContentContext? ContextOverride
    {
        get; set;
    }

    /// <summary>The content context that this pack's relative paths resolve against.</summary>
    public ContentContext ResolveContext() => ContextOverride ?? AnimationPackLoader.DeriveContext(Manifest);

    /// <summary>Loads and validates the referenced manifest.</summary>
    /// <exception cref="AnimationPackValidationException">Thrown when validation fails.</exception>
    public AnimationPackManifest LoadManifest(IAnimationPackResources resources)
    {
        return string.IsNullOrWhiteSpace(Manifest)
            ? throw new InvalidOperationException($"Animation pack node '{Name}' has no manifest path.")
            : AnimationPackLoader.Load(Manifest, ResolveContext(), resources);
    }
}
