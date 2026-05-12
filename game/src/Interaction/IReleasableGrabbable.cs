namespace AlleyCat.Interaction;

/// <summary>
/// Optional extension contract for grabbables that need state reset when a hand releases them.
/// </summary>
public interface IReleasableGrabbable
{
    /// <summary>
    /// Releases held state previously accepted by <see cref="IGrabbable.Grab" />.
    /// </summary>
    void Release();
}
