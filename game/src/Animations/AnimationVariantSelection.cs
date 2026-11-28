using AlleyCat.Core.Content;
using Godot;

namespace AlleyCat.Animations;

internal static class AnimationVariantSelection
{
    internal static AnimationVariantLayer[] InTemplateOrder(PackedScene template, AnimationVariantLayer[] layers)
    {
        var chain = new Stack<SceneState>();
        for (SceneState? state = template.GetState(); state is not null; state = state.GetBaseSceneState())
        {
            chain.Push(state);
        }

        var rank = new Dictionary<string, (int Depth, string Scene)>(StringComparer.Ordinal);
        int depth = 0;
        foreach (SceneState state in chain)
        {
            string? contribution = null;
            for (int node = 0; node < state.GetNodeCount(); node++)
            {
                string path = state.GetNodePath(node).ToString();
                if (path.StartsWith("./", StringComparison.Ordinal))
                {
                    path = path[2..];
                }

                if (path.StartsWith("AnimationCatalogue/", StringComparison.Ordinal)
                    && layers.Any(layer => path == $"AnimationCatalogue/{layer.Name}")
                    && Enumerable.Range(0, state.GetNodePropertyCount(node))
                        .Any(property => state.GetNodePropertyName(node, property) == AnimationVariantLayer.PropertyName.Operations))
                {
                    if (rank.TryGetValue(path, out (int Depth, string Scene) inherited))
                    {
                        throw new InvalidOperationException(
                            $"Animation variant layer '{path}' in '{state.GetPath()}' overwrites the inherited layer from '{inherited.Scene}'; declare a distinct layer.");
                    }

                    if (contribution is not null)
                    {
                        throw new InvalidOperationException(
                            $"Animation variant template '{state.GetPath()}' declares multiple layers: '{contribution}' and '{path}'; use one ordered layer per template.");
                    }

                    contribution = path;
                    rank.Add(path, (depth, state.GetPath()));
                }
            }

            depth++;
        }

        return [.. layers.OrderBy(layer => rank.TryGetValue($"AnimationCatalogue/{layer.Name}", out (int Depth, string Scene) order)
            ? order.Depth : int.MaxValue)];
    }

    public static IReadOnlyList<AnimationEntry> Compose(
        IReadOnlyList<AnimationVariantLayer> layers, ContentContext active, IAnimationVariantResources resources)
    {
        List<AnimationEntry> entries = [];
        Dictionary<(AnimationCategory, string), (AnimationEntry Entry, string Declaration)> selected = [];
        foreach (AnimationVariantLayer layer in layers)
        {
            HashSet<(AnimationCategory, string)> touched = [];
            Dictionary<(AnimationCategory, string), string> declarations = [];
            for (int index = 0; index < layer.Operations.Length; index++)
            {
                AnimationVariantOperation operation = layer.Operations[index]
                    ?? throw new InvalidOperationException($"Variant layer '{layer.Name}' has an empty operation at index {index}.");
                string declaration = $"{operation.ResourcePath} ({layer.Name}[{index}]: {operation.Kind})";
                _ = AnimationVariantLoader.RequireOwner(operation.ResourcePath.Split("::", 2)[0], active);
                AnimationEntry? entry = null;
                if (operation.Kind is AnimationVariantOperation.Action.Add or AnimationVariantOperation.Action.Replace)
                {
                    entry = AnimationVariantLoader.Load(operation.Descriptor, active, resources);
                }

                (AnimationCategory Category, string ID) key;
                if (operation.Kind == AnimationVariantOperation.Action.Add)
                {
                    if (operation.Category.Length != 0 || operation.EntryID.Length != 0)
                    {
                        throw Conflict("add derives its key from the descriptor");
                    }

                    key = (entry!.Category, entry.ID);
                }
                else if ((operation.Kind is AnimationVariantOperation.Action.Remove or AnimationVariantOperation.Action.Replace)
                         && AnimationCategoryNames.TryParse(operation.Category, out AnimationCategory category)
                         && AnimationVariantLoader.IsIdentifier(operation.EntryID))
                {
                    key = (category, operation.EntryID);
                    if (entry is not null && (entry.Category, entry.ID) != key)
                    {
                        throw Conflict($"replacement descriptor '{operation.Descriptor}' does not match {KeyName(key)}");
                    }

                    if (operation.Kind == AnimationVariantOperation.Action.Remove && operation.Descriptor.Length != 0)
                    {
                        throw Conflict($"remove of {KeyName(key)} cannot include a descriptor");
                    }
                }
                else
                {
                    throw Conflict("invalid operation or inherited category/id");
                }

                if (!touched.Add(key))
                {
                    throw Conflict($"multiple operations on {KeyName(key)} in one layer, first from {declarations[key]}");
                }

                declarations.Add(key, declaration);
                if (operation.Kind == AnimationVariantOperation.Action.Add)
                {
                    if (selected.TryGetValue(key, out (AnimationEntry Entry, string Declaration) existing))
                    {
                        throw Conflict($"cannot add existing {KeyName(key)} selected by {existing.Declaration}");
                    }
                }
                else
                {
                    _ = selected.Remove(key, out (AnimationEntry Entry, string Declaration) inherited) ? entries.Remove(inherited.Entry) : throw Conflict($"no inherited {KeyName(key)}");
                }

                if (entry is not null)
                {
                    selected.Add(key, (entry, declaration));
                    entries.Add(entry);
                }

                InvalidOperationException Conflict(string reason)
                {
                    return new($"Animation variant operation {declaration}: {reason}.");
                }
            }
        }

        return entries.AsReadOnly();
    }

    private static string KeyName((AnimationCategory Category, string ID) key) => $"{key.Category.ToName()}/{key.ID}";
}
