namespace AlleyCat.Models;

/// <summary>
/// Reference-identity token for one consumer of a shared backend. Scheduling fairness and supersession are per
/// consumer; the name is diagnostic only.
/// </summary>
/// <param name="name">Diagnostic name, such as the owning character or component.</param>
public sealed class ModelConsumerId(string name)
{
    /// <summary>Diagnostic name of the consumer.</summary>
    public string Name { get; } = name;

    /// <inheritdoc />
    public override string ToString() => Name;
}
