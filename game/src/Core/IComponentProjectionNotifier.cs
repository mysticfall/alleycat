namespace AlleyCat.Core;

/// <summary>
/// Notifies consumers after a holder has committed a replacement component projection.
/// </summary>
public interface IComponentProjectionNotifier : IComponentHolder
{
    /// <summary>Gets whether a component projection has been successfully committed.</summary>
    bool HasComponentProjection
    {
        get;
    }

    /// <summary>Occurs after the current component projection has been committed.</summary>
    event Action? ComponentsRefreshed;
}
