using AlleyCat.Models;

namespace AlleyCat.Motion;

/// <summary>
/// Identifies one shared MotionBricks native session: the resolved absolute model path and the execution provider.
/// </summary>
/// <param name="ModelPath">Absolute, globalised model path.</param>
/// <param name="ExecutionProvider">Requested execution provider.</param>
public sealed record SomaModelKey(string ModelPath, string ExecutionProvider) : ModelKey;
