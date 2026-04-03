using Godot;

namespace AlleyCat.Common;

/// <summary>
/// Extension methods for Godot's <see cref="Node"/> class.
/// </summary>
public static class NodeExtensions
{
    extension(Node node)
    {
        /// <summary>
        /// Fetches a required child node at <paramref name="path"/> and casts it to <typeparamref name="T"/>.
        /// Throws an <see cref="InvalidOperationException"/> if the node is not found or is not of the expected type.
        /// </summary>
        /// <param name="path">The path to the child node (e.g. "Label3D").</param>
        /// <typeparam name="T">The expected node type.</typeparam>
        /// <returns>The resolved node, never null.</returns>
        public T RequireNode<T>(NodePath path) where T : class
        {
            T result = node.GetNode<T>(path);

            return result ?? throw new InvalidOperationException(
                $"Required node '{path}' of type {typeof(T).Name} not found on {node.Name} " +
                $"({node.GetType().FullName}).");
        }
    }
}
