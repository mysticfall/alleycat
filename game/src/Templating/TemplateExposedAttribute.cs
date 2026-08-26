namespace AlleyCat.Templating;

/// <summary>
/// Marks an interface property as a curated member that Fluid templates may read.
/// </summary>
/// <remarks>
/// <para>
/// Curated members are discovered once per process by scanning the game assembly for interfaces carrying this
/// attribute on their properties. Each member is registered against its declaring interface, and every concrete
/// implementer resolves it through Fluid's interface walk, so no per-type registration is required.
/// </para>
/// <para>
/// A curated member name must be unique across all interfaces (the single-level rule). Because Fluid enumerates a
/// type's interfaces in an unspecified order, curating the same name on two interfaces would resolve ambiguously,
/// so discovery fails loudly instead.
/// </para>
/// <para>
/// Unsealed types keep the engine's permissive reflective member access; only interfaces marked with
/// <see cref="TemplateSealedAttribute" /> restrict their template surface to curated members.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class TemplateExposedAttribute : Attribute
{
}
