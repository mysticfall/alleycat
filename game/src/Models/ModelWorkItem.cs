namespace AlleyCat.Models;

/// <summary>
/// Scheduling envelope of one request against a shared backend. v1 scheduling reads only the consumer identity and
/// supersession key; the metadata bag is carried unchanged for future priority or budget policies.
/// </summary>
/// <param name="consumer">Consumer that submitted the item.</param>
public sealed class ModelWorkItem(ModelConsumerId consumer)
{
    internal static IReadOnlyDictionary<string, object?> EmptyMetadata { get; } = new Dictionary<string, object?>();

    /// <summary>Consumer that submitted the item.</summary>
    public ModelConsumerId Consumer { get; } = consumer ?? throw new ArgumentNullException(nameof(consumer));

    /// <summary>
    /// Optional latest-wins key. A newer item from the same consumer with an equal key supersedes this one.
    /// </summary>
    public object? SupersessionKey
    {
        get; init;
    }

    /// <summary>
    /// Cancels the item. A queued item is dropped without running; a running item finishes, but its result is
    /// discarded and the item settles as cancelled.
    /// </summary>
    public CancellationToken CancellationToken
    {
        get; init;
    }

    /// <summary>Extensible metadata, passed to the scheduling policy unchanged.</summary>
    public IReadOnlyDictionary<string, object?> Metadata
    {
        get; init;
    } = EmptyMetadata;

    /// <inheritdoc />
    public override string ToString() =>
        SupersessionKey is null ? $"{Consumer}" : $"{Consumer} [{SupersessionKey}]";
}
