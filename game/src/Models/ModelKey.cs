namespace AlleyCat.Models;

/// <summary>
/// Identifies one shared backend. Model-specific layers derive a record whose value equality covers everything that
/// changes the loaded artefact, such as the resolved absolute model path and the requested execution provider.
/// Equal keys share one backend; unequal keys, including keys of different derived types, never share.
/// </summary>
public abstract record ModelKey;
