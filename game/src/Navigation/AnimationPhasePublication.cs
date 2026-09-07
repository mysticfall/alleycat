namespace AlleyCat.Navigation;

/// <summary>Publishes a normalised animation phase without conflating finite endpoints with cyclic seams.</summary>
public static class AnimationPhasePublication
{
    /// <summary>
    /// Attempts to normalise an evaluated animation clock. Cyclic clocks wrap, while finite clocks clamp and retain
    /// phase one at and beyond their authored endpoint.
    /// </summary>
    public static bool TryNormalise(float playPosition, float playLength, bool isCyclic, out float phase)
    {
        phase = 0f;
        if (!float.IsFinite(playPosition)
            || playPosition < 0f
            || !float.IsFinite(playLength)
            || playLength <= 0f)
        {
            return false;
        }

        float normalised = playPosition / playLength;
        if (!float.IsFinite(normalised))
        {
            return false;
        }

        if (!isCyclic)
        {
            phase = Math.Clamp(normalised, 0f, 1f);
            return true;
        }

        phase = normalised - MathF.Floor(normalised);
        if (phase >= 1f)
        {
            phase = 0f;
        }

        return float.IsFinite(phase) && phase >= 0f;
    }
}
