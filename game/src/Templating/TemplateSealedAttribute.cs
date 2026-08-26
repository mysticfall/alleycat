namespace AlleyCat.Templating;

/// <summary>
/// Seals an interface's template surface: exactly its curated members (properties marked with
/// <see cref="TemplateExposedAttribute" />, including members curated on other implemented interfaces) resolve for
/// implementers, and every other member access renders nil instead of reflecting the runtime object.
/// </summary>
/// <remarks>
/// <para>
/// Sealing applies through interface composition: any type whose interface graph includes the sealed interface has
/// its unlisted members hidden from templates, even when the concrete class exposes them as public properties.
/// </para>
/// <para>
/// Types that implement no sealed interface keep the engine's permissive reflective member access, preserving the
/// pre-existing template behaviour for plain view and data objects.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class TemplateSealedAttribute : Attribute
{
}
