namespace AlleyCat.Animations;

/// <summary>
/// Skeleton-binding compatibility for native idle clips (ANIM-003 TR11).
/// </summary>
public static class IdleClipCompatibility
{
    /// <summary>
    /// The ANIM TR4 semantic body map: the native humanoid bones that the canonical SOMA source rig maps onto the
    /// reference skeleton. Bones outside this set, such as <c>Root</c>, breasts or twist bones, are optional.
    /// </summary>
    public static IReadOnlySet<string> SomaMappedBones
    {
        get;
    } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head", "Jaw", "LeftEye", "RightEye",
        "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
        "LeftThumbMetacarpal", "LeftThumbProximal", "LeftThumbDistal",
        "LeftIndexProximal", "LeftIndexIntermediate", "LeftIndexDistal",
        "LeftMiddleProximal", "LeftMiddleIntermediate", "LeftMiddleDistal",
        "LeftRingProximal", "LeftRingIntermediate", "LeftRingDistal",
        "LeftLittleProximal", "LeftLittleIntermediate", "LeftLittleDistal",
        "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
        "RightThumbMetacarpal", "RightThumbProximal", "RightThumbDistal",
        "RightIndexProximal", "RightIndexIntermediate", "RightIndexDistal",
        "RightMiddleProximal", "RightMiddleIntermediate", "RightMiddleDistal",
        "RightRingProximal", "RightRingIntermediate", "RightRingDistal",
        "RightLittleProximal", "RightLittleIntermediate", "RightLittleDistal",
        "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes",
        "RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes"
    };

    /// <summary>
    /// Returns the SOMA-mapped bone tracks that do not resolve on the character skeleton. An empty result means the
    /// clip is compatible. A track resolves when it binds to the character's native skeleton node by name (with or
    /// without the scene-unique <c>%</c> prefix) and names one of its bones.
    /// </summary>
    /// <param name="boneTracks">Bone tracks as (skeleton node name, bone name) pairs.</param>
    /// <param name="skeletonName">The node name of the character's native skeleton.</param>
    /// <param name="hasBone">Whether the character's native skeleton has a bone with the given name.</param>
    public static IReadOnlyList<string> FindUnresolvedMappedTracks(
        IEnumerable<(string SkeletonNode, string Bone)> boneTracks,
        string skeletonName,
        Func<string, bool> hasBone)
    {
        ArgumentNullException.ThrowIfNull(boneTracks);
        ArgumentNullException.ThrowIfNull(skeletonName);
        ArgumentNullException.ThrowIfNull(hasBone);

        SortedSet<string> unresolved = new(StringComparer.Ordinal);
        foreach ((string node, string bone) in boneTracks)
        {
            if (!SomaMappedBones.Contains(bone))
            {
                continue;
            }

            if (node.TrimStart('%') != skeletonName || !hasBone(bone))
            {
                _ = unresolved.Add($"{node}:{bone}");
            }
        }

        return [.. unresolved];
    }
}
