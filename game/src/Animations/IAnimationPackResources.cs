namespace AlleyCat.Animations;

/// <summary>
/// Resource access used by <see cref="AnimationPackLoader" />, so that one validation implementation serves catalogue
/// loading and validation while tests can substitute resource checks.
/// </summary>
public interface IAnimationPackResources
{
    /// <summary>Reads a UTF-8 text file such as a <c>pack.json</c> manifest.</summary>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    string ReadText(string resourcePath);

    /// <summary>Returns whether a file exists at the resolved resource path.</summary>
    bool Exists(string resourcePath);

    /// <summary>Checks that a path is a loadable native <c>Animation</c> resource.</summary>
    /// <returns><see langword="null" /> when accepted; otherwise a reason.</returns>
    string? CheckClip(string resourcePath);

    /// <summary>Checks that the existing ANIM-002 reviewed-set loader accepts a set file.</summary>
    /// <returns><see langword="null" /> when accepted; otherwise a reason.</returns>
    string? CheckReviewedSet(string resourcePath);
}
