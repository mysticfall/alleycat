using Godot;

namespace AlleyCat.Navigation.Arrival;

/// <summary>
/// Seam tolerance of one character animation variant (NAV-001 Technical Requirement 7): the largest root-relative
/// per-foot ball separation the arrival planner accepts over a crossfade window, with its user-agreement record.
/// Navigation binds the config whose <see cref="PackageVariant"/> matches the bound locomotion package.
/// </summary>
[GlobalClass]
public partial class SeamToleranceConfig : Resource
{
    /// <summary>Locomotion package variant the tolerance applies to (the catalogue's <c>package_variant</c>).</summary>
    [Export]
    public string PackageVariant { get; set; } = "";

    /// <summary>Largest accepted per-foot separation, in runtime metres.</summary>
    [Export(PropertyHint.Range, "0.001,1,0.001,or_greater,suffix:m")]
    public float ToleranceMetres { get; set; } = SeamTolerance.StrictRecommendationMetres;

    /// <summary>Whether the user agreed the tolerance.</summary>
    [Export]
    public SeamToleranceAgreementStatus AgreementStatus { get; set; } = SeamToleranceAgreementStatus.Pending;

    /// <summary>Who agreed, when, and on what evidence; or why agreement is pending.</summary>
    [Export(PropertyHint.MultilineText)]
    public string AgreementRecord { get; set; } = "";

    /// <summary>
    /// The validated tolerance for a bound locomotion package variant.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The config names another variant, or its tolerance or agreement record is invalid.
    /// </exception>
    public SeamTolerance ToSeamTolerance(string packageVariant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageVariant);
        if (!string.Equals(PackageVariant, packageVariant, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Seam tolerance config '{ResourcePath}' is for variant '{PackageVariant}', not '{packageVariant}'.");
        }

        try
        {
            return new SeamTolerance(ToleranceMetres, AgreementStatus, AgreementRecord).Validate();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"Seam tolerance config '{ResourcePath}' is invalid: {exception.Message}", exception);
        }
    }
}
