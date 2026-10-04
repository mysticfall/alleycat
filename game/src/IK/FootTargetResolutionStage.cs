namespace AlleyCat.IK;

/// <summary>
/// Marks the span in which <see cref="CharacterIK" /> resolves foot targets inside its foot-provider skeleton modifier
/// stage: after the AnimationTree has applied the current tick and after that tick's actor movement, before lower-leg
/// solving. Stateful foot providers advance only inside the span of their own skeleton (IK-003 Technical Requirement 8).
/// </summary>
/// <remarks>
/// The span is per thread and per owner: while one character resolves its feet, a provider of another character's
/// skeleton, or one queried from another thread, is outside its stage. Spans may nest; each scope restores the span it
/// replaced, and disposing a scope (or a copy of it) again, or out of order, is a no-op.
/// </remarks>
internal static class FootTargetResolutionStage
{
    [ThreadStatic]
    private static object? _owner;

    [ThreadStatic]
    private static ulong _generation;

    // Process-wide so no two scopes on any thread share a generation.
    private static ulong _lastIssued;

    /// <summary>Whether <paramref name="owner" />'s foot targets are currently being resolved on this thread.</summary>
    /// <param name="owner">The resolving skeleton.</param>
    public static bool IsActiveFor(object? owner) => owner is not null && ReferenceEquals(_owner, owner);

    /// <summary>Enters <paramref name="owner" />'s span on this thread; dispose the returned scope to leave it.</summary>
    /// <param name="owner">The resolving skeleton.</param>
    public static Scope Enter(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var scope = new Scope(Interlocked.Increment(ref _lastIssued), _owner, _generation);
        _owner = owner;
        _generation = scope.Generation;
        return scope;
    }

    /// <summary>Leaves the resolution span when disposed.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly object? _previousOwner;
        private readonly ulong _previousGeneration;

        internal Scope(ulong generation, object? previousOwner, ulong previousGeneration)
        {
            Generation = generation;
            _previousOwner = previousOwner;
            _previousGeneration = previousGeneration;
        }

        internal ulong Generation
        {
            get;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Only the innermost live scope on this thread restores; a repeated, copied, default, or out-of-order
            // dispose finds a different generation and does nothing.
            if (Generation == 0 || _generation != Generation)
            {
                return;
            }

            _owner = _previousOwner;
            _generation = _previousGeneration;
        }
    }
}
