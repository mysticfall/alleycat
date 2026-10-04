using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace AlleyCat.TestFramework;

/// <summary>
/// Non-destructive evidence destination resolution for integration fixtures (CORNER-06). Fixtures used to
/// write evidence into a hardcoded default directory (or an env-selected one) with plain overwrite semantics,
/// which permanently destroyed prior recorded evidence whenever a guard run forgot to point
/// <c>ALLEYCAT_*_EVIDENCE</c> at a fresh tree. <see cref="Resolve(string)"/> makes every destination safe by
/// construction: a destination that does not exist or is empty is used in place; an occupied destination is
/// never overwritten — it is redirected to a fresh unique sibling directory carrying a
/// <c>REDIRECTED-FROM.txt</c> note, unless <see cref="AllowOverwriteEnvironmentVariable"/> is set to
/// <c>1</c>, in which case the destination is reused in place and an <c>OVERWRITTEN-&lt;date&gt;.txt</c> note
/// records exactly what was replaced. Resolution is idempotent per intended path within a process, so several
/// test methods writing per-scenario or per-rig files into one directory share one destination instead of
/// redirecting behind each other. Pure <see cref="System.IO"/>: callers globalize any <c>res://</c> path first.
/// </summary>
public static class EvidenceDestinations
{
    /// <summary>Set to <c>1</c> to deliberately overwrite an occupied evidence destination (a note is still
    /// recorded). Any other value redirects.</summary>
    public const string AllowOverwriteEnvironmentVariable = "ALLEYCAT_ALLOW_EVIDENCE_OVERWRITE";

    private const int MaximumSiblingAttempts = 999;

    private static readonly ConcurrentDictionary<string, string> _resolvedByIntended = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves the writable evidence directory for an intended destination (already globalized to a native
    /// path), creating it when needed. See the class remarks for the redirect and overwrite semantics.
    /// </summary>
    /// <param name="intendedDirectory">The directory the caller intends to write evidence into.</param>
    /// <returns>The directory to write into; it exists when this returns.</returns>
    public static string Resolve(string intendedDirectory)
        => Resolve(intendedDirectory, Environment.GetEnvironmentVariable(AllowOverwriteEnvironmentVariable));

    /// <summary>Resolves with an explicit allow-overwrite value (the unit-test boundary of the env read).</summary>
    /// <param name="intendedDirectory">The directory the caller intends to write evidence into.</param>
    /// <param name="allowOverwrite">The value of <see cref="AllowOverwriteEnvironmentVariable"/>, or null.</param>
    public static string Resolve(string intendedDirectory, string? allowOverwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intendedDirectory);
        string intended = Path.GetFullPath(intendedDirectory);
        return _resolvedByIntended.GetOrAdd(
            intended,
            static (path, overwrite) => ResolveOnce(path, overwrite),
            allowOverwrite);
    }

    private static string ResolveOnce(string intended, string? allowOverwrite)
    {
        if (!IsOccupied(intended))
        {
            _ = Directory.CreateDirectory(intended);
            return intended;
        }

        if (string.Equals(allowOverwrite, "1", StringComparison.Ordinal))
        {
            WriteOverwrittenNote(intended);
            return intended;
        }

        string[] existing = [.. Directory.EnumerateFileSystemEntries(intended).Order(StringComparer.Ordinal)];
        for (int attempt = 2; attempt <= MaximumSiblingAttempts; attempt++)
        {
            string candidate = SiblingPath(intended, attempt);
            if (IsOccupied(candidate))
            {
                continue;
            }

            _ = Directory.CreateDirectory(candidate);
            WriteRedirectedNote(intended, candidate, existing.Length, existing.FirstOrDefault());
            return candidate;
        }

        throw new InvalidOperationException(
            $"No free evidence sibling under '{intended}' after {MaximumSiblingAttempts} attempts; "
            + $"set {AllowOverwriteEnvironmentVariable}=1 to overwrite deliberately.");
    }

    /// <summary>Whether the path exists as a directory containing anything (or exists as a file at all).</summary>
    private static bool IsOccupied(string path)
        => File.Exists(path) || (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any());

    /// <summary>
    /// The attempt-th fresh sibling of an intended directory: the last <c>run-NN</c> path segment is numbered
    /// (<c>run-01/corner</c> → <c>run-01-2/corner</c>); a path without one numbers its final segment
    /// (<c>planner-oracle</c> → <c>planner-oracle-2</c>).
    /// </summary>
    private static string SiblingPath(string intended, int attempt)
    {
        string trimmed = intended.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(trimmed);
        string? parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent))
        {
            return $"{leaf}-{attempt}";
        }

        int position = parent.Length;
        while (position > 0)
        {
            int start = parent.LastIndexOf(Path.DirectorySeparatorChar, position - 1) + 1;
            string segment = parent[start..position];
            if (IsRunSegment(segment))
            {
                // parent[..start] keeps the leading portion; intended[position..] reattaches "/leaf".
                return $"{parent[..start]}{segment}-{attempt}{intended[position..]}";
            }

            position = start - 1;
        }

        return Path.Combine(parent, $"{leaf}-{attempt}");
    }

    private static bool IsRunSegment(string segment)
        => segment.Length is > 4
            && segment.StartsWith("run-", StringComparison.Ordinal)
            && uint.TryParse(segment.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static void WriteRedirectedNote(string intended, string redirected, int existingCount, string? firstExisting)
    {
        StringBuilder note = new();
        _ = note.AppendLine("Redirected evidence destination (nothing was overwritten).");
        _ = note.AppendLine($"Intended: {intended}");
        _ = note.AppendLine($"Redirected to: {redirected}");
        string first = firstExisting is null ? string.Empty : $" (first: {Path.GetFileName(firstExisting)})";
        _ = note.AppendLine(
            $"Reason: the intended directory already contained {existingCount} entr{(existingCount == 1 ? "y" : "ies")}{first}"
            + $" and {AllowOverwriteEnvironmentVariable} was not set to 1.");
        _ = note.AppendLine($"To overwrite deliberately, set {AllowOverwriteEnvironmentVariable}=1 (an OVERWRITTEN note is then recorded);");
        _ = note.AppendLine("to target a fresh tree, point the fixture's ALLEYCAT_*_EVIDENCE variable at a path that does not exist yet.");
        _ = note.AppendLine($"Recorded: {Timestamp()}");
        File.WriteAllText(Path.Combine(redirected, "REDIRECTED-FROM.txt"), note.ToString());
    }

    private static void WriteOverwrittenNote(string intended)
    {
        StringBuilder note = new();
        _ = note.AppendLine($"Deliberate evidence overwrite ({AllowOverwriteEnvironmentVariable}=1).");
        _ = note.AppendLine($"Directory: {intended}");
        string[] existing = [.. Directory.EnumerateFileSystemEntries(intended).Order(StringComparer.Ordinal)];
        _ = note.AppendLine($"Files present before this run's writes ({existing.Length}):");
        foreach (string entry in existing)
        {
            string suffix = File.Exists(entry) ? $" ({new FileInfo(entry).Length} bytes)" : "/ (directory)";
            _ = note.AppendLine($"- {Path.GetFileName(entry)}{suffix}");
        }

        _ = note.AppendLine($"Recorded: {Timestamp()}");
        File.WriteAllText(
            Path.Combine(intended, $"OVERWRITTEN-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt"),
            note.ToString());
    }

    private static string Timestamp()
        => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
