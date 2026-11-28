using Godot;

namespace AlleyCat.Animations;

/// <summary>A single explicit change to the inherited entries.</summary>
[GlobalClass]
public partial class AnimationVariantOperation : Resource
{
    /// <summary>The supported operations on a category/id key.</summary>
    public enum Action
    {
        /// <summary>Append an absent key selected by a descriptor.</summary>
        Add,
        /// <summary>Remove an inherited key.</summary>
        Remove,
        /// <summary>Remove an inherited key and append its explicit replacement.</summary>
        Replace
    }

    /// <summary>The operation to perform.</summary>
    [Export]
    public Action Kind
    {
        get; set;
    }

    /// <summary>Descriptor path for an add or replacement; empty on remove.</summary>
    [Export(PropertyHint.File, "*.json")]
    public string Descriptor { get; set; } = string.Empty;

    /// <summary>Category of the inherited key for remove or replace.</summary>
    [Export(PropertyHint.Enum, ",idle,locomotion")]
    public string Category { get; set; } = string.Empty;

    /// <summary>Id of the inherited key for remove or replace.</summary>
    [Export]
    public string EntryID { get; set; } = string.Empty;
}
