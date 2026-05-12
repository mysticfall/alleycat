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
            T? match = null;
            int count = 0;

            foreach (IComponent candidate in holder.Components)
            {
                if (candidate is not T typedCandidate)
                {
                    continue;
                }

                count++;
                if (count == 1)
                {
                    match = typedCandidate;
                }
                else
                {
                    component = null;
                    return false;
                }
            }

            component = count == 1 ? match : null;

            return count == 1;
        }

        /// <summary>
        /// Resolves all components assignable to <typeparamref name="T"/> in holder-defined order.
        /// </summary>
        /// <typeparam name="T">The requested component capability type.</typeparam>
        /// <returns>Matching components in deterministic holder order.</returns>
        public IReadOnlyList<T> GetComponents<T>()
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

        /// <summary>
        /// Resolves exactly one component assignable to <typeparamref name="T"/>.
        /// Throws an <see cref="InvalidOperationException"/> when no match or multiple matches exist.
        /// </summary>
        /// <typeparam name="T">The requested component capability type.</typeparam>
        /// <returns>The single matching component.</returns>
        public T RequireComponent<T>()
            where T : class, IComponent
        {
            IReadOnlyList<T> components = holder.GetComponents<T>();
            return components.Count == 1
                ? components[0]
                : throw new InvalidOperationException(
                $"Required exactly one component of type {FormatType(typeof(T))} on {DescribeHolder(holder)}. " +
                $"Expected exactly 1, found {components.Count}.");
        }
    }

    private static string DescribeHolder(IComponentHolder holder)
    {
        string holderType = FormatType(holder.GetType());

        return holder is Node node
            ? $"{holderType} node '{node.Name}' ({node.GetPath()})"
            : holderType;
    }

    private static string FormatType(Type type) => type.FullName ?? type.Name;
}
