namespace AlleyCat.Mind.Observation;

/// <summary>
/// Immutable commit-identity tuple an observation may supply so Mind enforces exact-once ingestion
/// (AI-001 TR-9). Components compare with ordinal value equality, order-sensitively; the tuple is ingestion
/// correlation only and never participates in model-facing rendering.
/// </summary>
public sealed class ObservationCommitIdentity : IEquatable<ObservationCommitIdentity>
{
    private readonly object?[] _components;

    /// <summary>
    /// Creates an immutable identity tuple from ordered components.
    /// </summary>
    /// <param name="components">Identity components compared with ordinal value equality, order-sensitively.</param>
    public ObservationCommitIdentity(params object?[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        _components = [.. components];
    }

    /// <summary>Gets the ordered, immutable identity components.</summary>
    public IReadOnlyList<object?> Components => _components;

    /// <summary>Determines whether both tuples carry the same components in the same order.</summary>
    public bool Equals(ObservationCommitIdentity? other)
        => other is not null
            && _components.Length == other._components.Length
            && _components.SequenceEqual(other._components);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ObservationCommitIdentity);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (object? component in _components)
        {
            hash.Add(component);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString()
        => $"ObservationCommitIdentity {{ Components = {string.Join(", ", _components.Select(static component => component?.ToString() ?? "null"))} }}";
}

/// <summary>
/// Optional commit-identity contract for observation ingestion (AI-001 TR-9): any observation type may supply
/// an immutable identity tuple, and Mind enforces exact-once uniqueness atomically at its ingestion gate —
/// enforcement stays in Mind, never in perception. Observations that supply no identity keep their ordinary
/// duplicate policy.
/// </summary>
public interface IHasCommitIdentity
{
    /// <summary>Gets the immutable commit identity, or null when this observation claims none.</summary>
    ObservationCommitIdentity? CommitIdentity
    {
        get;
    }
}
