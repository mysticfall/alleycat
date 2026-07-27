namespace AlleyCat.Core;

/// <summary>
/// Represents an object with a typed, stable identity.
/// </summary>
public interface IIdentifiable
{
    /// <summary>
    /// Gets or sets the local lower <c>snake_case</c> identifier.
    /// </summary>
    string Id
    {
        get; set;
    }

    /// <summary>
    /// Gets the lower <c>snake_case</c> type identifier.
    /// </summary>
    string Type
    {
        get;
    }

    /// <summary>
    /// Gets the canonical typed identity in <c>Type:Id</c> form.
    /// </summary>
    string FullId => $"{Type}:{Id}";
}
