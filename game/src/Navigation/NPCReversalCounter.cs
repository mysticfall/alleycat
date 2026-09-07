#pragma warning disable CS1591

namespace AlleyCat.Navigation;

/// <summary>Counts only sustained realised longitudinal direction changes.</summary>
public sealed class NPCReversalCounter(float minimumSpeed, int requiredConsecutiveFrames)
{
    private int _stableSign;
    private int _candidateSign;
    private int _candidateFrames;

    public int Count
    {
        get; private set;
    }
    public float MinimumSpeed { get; } = Math.Max(minimumSpeed, 0f);
    public int RequiredConsecutiveFrames { get; } = Math.Max(requiredConsecutiveFrames, 1);

    public bool Observe(float realisedLongitudinalSpeed)
    {
        int sign = Math.Abs(realisedLongitudinalSpeed) >= MinimumSpeed
            ? Math.Sign(realisedLongitudinalSpeed)
            : 0;
        if (sign == 0)
        {
            _candidateSign = 0;
            _candidateFrames = 0;
            return false;
        }

        if (_stableSign == 0)
        {
            _stableSign = sign;
            return false;
        }

        if (sign == _stableSign)
        {
            _candidateSign = 0;
            _candidateFrames = 0;
            return false;
        }

        if (sign != _candidateSign)
        {
            _candidateSign = sign;
            _candidateFrames = 1;
            return false;
        }

        _candidateFrames++;
        if (_candidateFrames < RequiredConsecutiveFrames)
        {
            return false;
        }

        _stableSign = _candidateSign;
        _candidateSign = 0;
        _candidateFrames = 0;
        Count++;
        return true;
    }

    public void Reset()
    {
        _stableSign = 0;
        _candidateSign = 0;
        _candidateFrames = 0;
        Count = 0;
    }
}
