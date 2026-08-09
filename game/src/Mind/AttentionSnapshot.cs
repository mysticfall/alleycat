using System.Collections.ObjectModel;

namespace AlleyCat.Mind;

/// <summary>Immutable, ordinally ordered attention state at one monotonic instant.</summary>
public sealed class AttentionSnapshot
{
    internal AttentionSnapshot(double timestamp, IDictionary<string, float> values)
    {
        Timestamp = timestamp;
        Values = new ReadOnlyDictionary<string, float>(values);
    }

    /// <summary>Gets the monotonic timestamp at which decay was applied.</summary>
    public double Timestamp
    {
        get;
    }

    /// <summary>Gets attention values keyed only by canonical FullId.</summary>
    public IReadOnlyDictionary<string, float> Values
    {
        get;
    }
}
