namespace AlleyCat.Core.Installer;

/// <summary>
/// Represents the outcome of a scene installation step.
/// </summary>
public sealed class SceneInstallationResult
{
    private static readonly SceneInstallationResult _successfulResult = new(true, []);

    private SceneInstallationResult(bool succeeded, IReadOnlyList<string> errors)
    {
        Succeeded = succeeded;
        Errors = errors;
    }

    /// <summary>
    /// Gets a value indicating whether installation succeeded.
    /// </summary>
    public bool Succeeded
    {
        get;
    }

    /// <summary>
    /// Gets authoring or installation errors recorded for the operation.
    /// </summary>
    public IReadOnlyList<string> Errors
    {
        get;
    }

    /// <summary>
    /// Creates a successful installation result.
    /// </summary>
    /// <returns>A successful installation result.</returns>
    public static SceneInstallationResult Successful() => _successfulResult;

    /// <summary>
    /// Creates a failed installation result with one or more diagnostic messages.
    /// </summary>
    /// <param name="errors">The errors that describe the failure.</param>
    /// <returns>A failed installation result.</returns>
    public static SceneInstallationResult Failed(params string[] errors)
    {
        return errors.Length == 0
            ? throw new ArgumentException(
                "At least one error is required for a failed installation result.",
                nameof(errors))
            : new SceneInstallationResult(false, errors);
    }

    /// <summary>
    /// Combines several installation results into a single outcome.
    /// </summary>
    /// <param name="results">The results to merge in order.</param>
    /// <returns>A successful result when all inputs succeeded; otherwise a failed result containing all errors.</returns>
    public static SceneInstallationResult Merge(IEnumerable<SceneInstallationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        List<string> errors = [];

        foreach (SceneInstallationResult result in results)
        {
            if (!result.Succeeded)
            {
                errors.AddRange(result.Errors);
            }
        }

        return errors.Count == 0 ? Successful() : new SceneInstallationResult(false, errors);
    }

    /// <summary>
    /// Throws an exception when this result represents a failed authoring or installation operation.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when installation failed.</exception>
    public void ThrowIfFailed()
    {
        if (!Succeeded)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, Errors));
        }
    }
}
