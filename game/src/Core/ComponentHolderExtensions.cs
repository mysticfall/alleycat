using Godot;

namespace AlleyCat.Core;

/// <summary>
/// Extension methods for querying components from an <see cref="IComponentHolder"/>.
/// </summary>
public static class ComponentHolderExtensions
{
    extension(IComponentHolder holder)
    {
        /// <summary>
        /// Attempts to resolve exactly one component assignable to <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The requested component capability type.</typeparam>
        /// <param name="component">The resolved component when exactly one match exists; otherwise null.</param>
        /// <returns>True when exactly one match exists; false when zero or multiple matches exist.</returns>
        public bool TryGetComponent<T>(out T? component)
            where T : class, IComponent
        {
            IComponent? match = ComponentResolution.FindSingle(holder, typeof(T), out int count);
            component = count == 1 ? (T)match! : null;
            return count == 1;
        }

        /// <summary>
        /// Resolves all components assignable to <typeparamref name="T"/> in holder-defined order.
        /// </summary>
        /// <typeparam name="T">The requested component capability type.</typeparam>
        /// <returns>Matching components in deterministic holder order.</returns>
        public IReadOnlyList<T> GetComponents<T>()
            where T : class, IComponent
            => ComponentResolution.GetComponents<T>(holder);

        /// <summary>
        /// Resolves exactly one component assignable to <typeparamref name="T"/>.
        /// Throws an <see cref="InvalidOperationException"/> when no match or multiple matches exist.
        /// </summary>
        /// <typeparam name="T">The requested component capability type.</typeparam>
        /// <returns>The single matching component.</returns>
        public T RequireComponent<T>()
            where T : class, IComponent
        {
            IComponent? match = ComponentResolution.FindSingle(holder, typeof(T), out int count);
            return count == 1
                ? (T)match!
                : throw ComponentResolution.CreateCardinalityException(holder, typeof(T), count, "Required");
        }
    }
}

internal static class ComponentResolution
{
    internal static object? GetService(IComponentHolder holder, Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        IComponent? match = FindSingle(holder, serviceType, out int count);
        return count switch
        {
            0 => null,
            1 => match,
            _ => throw CreateCardinalityException(holder, serviceType, count, "Resolved"),
        };
    }

    internal static IComponent? FindSingle(IComponentHolder holder, Type requestedType, out int count)
    {
        IComponent? match = null;
        count = 0;

        foreach (IComponent candidate in holder.Components)
        {
            if (!requestedType.IsInstanceOfType(candidate))
            {
                continue;
            }

            count++;
            match ??= candidate;
        }

        return match;
    }

    internal static IReadOnlyList<T> GetComponents<T>(IComponentHolder holder)
        where T : class, IComponent
    {
        List<T> components = [];

        foreach (IComponent candidate in holder.Components)
        {
            if (candidate is T typedCandidate)
            {
                components.Add(typedCandidate);
            }
        }

        return components;
    }

    internal static InvalidOperationException CreateCardinalityException(
        IComponentHolder holder,
        Type requestedType,
        int actualCount,
        string action)
        => new(
            $"{action} exactly one component of type {FormatType(requestedType)} on {DescribeHolder(holder)}. " +
            $"Expected exactly 1, found {actualCount}.");

    private static string DescribeHolder(IComponentHolder holder)
    {
        string holderType = FormatType(holder.GetType());

        return holder is Node node
            ? $"{holderType} node '{node.Name}' ({node.GetPath()})"
            : holderType;
    }

    private static string FormatType(Type type) => type.FullName ?? type.Name;
}
