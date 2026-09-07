using System.Runtime.CompilerServices;
using Godot;

namespace AlleyCat.Navigation;

/// <summary>An exact imported Root pose at the float source time supplied to Godot.</summary>
internal readonly record struct NPCImportedRootEndpoint(Vector3 Position, Quaternion Rotation);

/// <summary>An imported Root interval expressed in the navigation semantic frame.</summary>
internal readonly record struct NPCImportedRootDelta(Vector2 Root, float Yaw);

/// <summary>Samples and composes imported Root endpoints without changing the authoritative operation order.</summary>
internal static class NPCExactImportedRootEndpointSampler
{
    public static float ClampSourceTime(Animation animation, double sourceTime)
        => (float)Math.Clamp(sourceTime, 0d, animation.Length);

    public static NPCImportedRootEndpoint Sample(
        Animation animation,
        int positionTrack,
        int rotationTrack,
        float clampedSourceTime)
    {
        Vector3 position = animation.PositionTrackInterpolate(positionTrack, clampedSourceTime);
        Quaternion rotation = rotationTrack >= 0
            ? animation.RotationTrackInterpolate(rotationTrack, clampedSourceTime)
            : Quaternion.Identity;
        return new(position, NormaliseRootRotation(rotation));
    }

    public static NPCImportedRootDelta Compose(
        in NPCImportedRootEndpoint previous,
        in NPCImportedRootEndpoint current,
        float motionScale)
    {
        Vector3 skeletonLocalRoot = new Basis(current.Rotation).Transposed()
            * ((current.Position - previous.Position) * motionScale);
        Vector2 semanticRoot = new(-skeletonLocalRoot.X, skeletonLocalRoot.Z);
        float yaw = (previous.Rotation.Inverse() * current.Rotation).GetEuler().Y;
        return new(semanticRoot, yaw);
    }

    private static Quaternion NormaliseRootRotation(Quaternion rotation)
        => rotation.IsFinite() && rotation.LengthSquared() > 0.0000000001f
            ? rotation.Normalized()
            : Quaternion.Identity;
}

/// <summary>
/// Bounded pass-local exact endpoint reuse. A small per-track last-value tier remains available after the cross-track
/// dictionary reaches capacity, while explicit nested scopes keep one outer alternative search authoritative.
/// </summary>
internal sealed class NPCExactImportedRootEndpointCache<TAnimation>
    where TAnimation : class
{
    private const int LastEndpointSlotCount = 16;

    private readonly Dictionary<Key, NPCImportedRootEndpoint> _values;
    private readonly TrackSlot[] _lastEndpoints = new TrackSlot[LastEndpointSlotCount];
    private readonly int _capacity;
    private object? _activeSnapshot;
    private int _depth;
    private int _generation;
    private int _nextLastEndpointSlot;

    public NPCExactImportedRootEndpointCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _values = new(capacity);
    }

    public Scope BeginScope(object immutableSnapshot)
    {
        ArgumentNullException.ThrowIfNull(immutableSnapshot);
        if (_depth == 0)
        {
            ClearValues();
            _activeSnapshot = immutableSnapshot;
            _depth = 1;
            _generation++;
        }
        else
        {
            if (!ReferenceEquals(_activeSnapshot, immutableSnapshot))
            {
                throw new InvalidOperationException("Nested imported Root sampling scopes must share one immutable snapshot.");
            }

            _depth++;
        }

        return new(this, immutableSnapshot, _generation);
    }

    public void Clear()
    {
        ClearValues();
        _activeSnapshot = null;
        _depth = 0;
        _generation++;
    }

    public bool TryGet(
        object immutableSnapshot,
        TAnimation animation,
        int positionTrack,
        int rotationTrack,
        float clampedSourceTime,
        out NPCImportedRootEndpoint endpoint)
    {
        if (!ReferenceEquals(_activeSnapshot, immutableSnapshot))
        {
            endpoint = default;
            return false;
        }

        var key = new Key(
            immutableSnapshot,
            animation,
            positionTrack,
            rotationTrack,
            clampedSourceTime);
        for (int index = 0; index < _lastEndpoints.Length; index++)
        {
            if (_lastEndpoints[index].Valid && _lastEndpoints[index].Key.Equals(key))
            {
                endpoint = _lastEndpoints[index].Endpoint;
                return true;
            }
        }

        if (_values.TryGetValue(key, out endpoint))
        {
            StoreLastEndpoint(in key, in endpoint);
            return true;
        }

        return false;
    }

    public void Store(
        object immutableSnapshot,
        TAnimation animation,
        int positionTrack,
        int rotationTrack,
        float clampedSourceTime,
        in NPCImportedRootEndpoint endpoint)
    {
        if (!ReferenceEquals(_activeSnapshot, immutableSnapshot))
        {
            return;
        }

        var key = new Key(
            immutableSnapshot,
            animation,
            positionTrack,
            rotationTrack,
            clampedSourceTime);
        StoreLastEndpoint(in key, in endpoint);
        if (_values.Count < _capacity)
        {
            _ = _values.TryAdd(key, endpoint);
        }
    }

    private void EndScope(object immutableSnapshot, int generation)
    {
        if (generation != _generation || !ReferenceEquals(_activeSnapshot, immutableSnapshot) || _depth <= 0)
        {
            return;
        }

        _depth--;
        if (_depth == 0)
        {
            ClearValues();
            _activeSnapshot = null;
        }
    }

    private void StoreLastEndpoint(in Key key, in NPCImportedRootEndpoint endpoint)
    {
        int emptySlot = -1;
        for (int index = 0; index < _lastEndpoints.Length; index++)
        {
            ref TrackSlot slot = ref _lastEndpoints[index];
            if (!slot.Valid)
            {
                emptySlot = index;
                continue;
            }

            if (slot.Key.SameTrack(key))
            {
                slot = new(key, endpoint, true);
                return;
            }
        }

        int destination = emptySlot >= 0 ? emptySlot : _nextLastEndpointSlot;
        _lastEndpoints[destination] = new(key, endpoint, true);
        _nextLastEndpointSlot = (destination + 1) % _lastEndpoints.Length;
    }

    private void ClearValues()
    {
        _values.Clear();
        Array.Clear(_lastEndpoints);
        _nextLastEndpointSlot = 0;
    }

    internal struct Scope(
        NPCExactImportedRootEndpointCache<TAnimation> owner,
        object immutableSnapshot,
        int generation) : IDisposable
    {
        private NPCExactImportedRootEndpointCache<TAnimation>? _owner = owner;

        public void Dispose()
        {
            _owner?.EndScope(immutableSnapshot, generation);
            _owner = null;
        }
    }

    private readonly record struct TrackSlot(Key Key, NPCImportedRootEndpoint Endpoint, bool Valid);

    private readonly struct Key(
        object snapshot,
        object animation,
        int positionTrack,
        int rotationTrack,
        float clampedSourceTime) : IEquatable<Key>
    {
        private readonly object _snapshot = snapshot;
        private readonly object _animation = animation;
        private readonly int _positionTrack = positionTrack;
        private readonly int _rotationTrack = rotationTrack;
        private readonly int _clampedSourceTimeBits = BitConverter.SingleToInt32Bits(clampedSourceTime);

        public bool Equals(Key other)
            => ReferenceEquals(_snapshot, other._snapshot)
                && ReferenceEquals(_animation, other._animation)
                && _positionTrack == other._positionTrack
                && _rotationTrack == other._rotationTrack
                && _clampedSourceTimeBits == other._clampedSourceTimeBits;

        public bool SameTrack(Key other)
            => ReferenceEquals(_snapshot, other._snapshot)
                && ReferenceEquals(_animation, other._animation)
                && _positionTrack == other._positionTrack
                && _rotationTrack == other._rotationTrack;

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(RuntimeHelpers.GetHashCode(_snapshot));
            hash.Add(RuntimeHelpers.GetHashCode(_animation));
            hash.Add(_positionTrack);
            hash.Add(_rotationTrack);
            hash.Add(_clampedSourceTimeBits);
            return hash.ToHashCode();
        }
    }
}
