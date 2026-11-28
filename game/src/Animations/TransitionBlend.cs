namespace AlleyCat.Animations;

/// <summary>
/// Blend weights for ANIM-003 transitions: the short velocity blend at an interruption or native-idle exit (TR20) and
/// the through-bridge blend of bones outside the SOMA map (TR19).
/// </summary>
public static class TransitionBlend
{
    /// <summary>
    /// Smoothstep weight from 0 to 1 over a duration, with zero slope at both ends so neither the outgoing nor the
    /// incoming motion receives a velocity step. A non-positive duration completes immediately.
    /// </summary>
    public static float Weight(double elapsed, double duration)
    {
        if (duration <= 0 || elapsed >= duration)
        {
            return 1;
        }

        if (elapsed <= 0)
        {
            return 0;
        }

        double u = elapsed / duration;
        return (float)(u * u * (3 - (2 * u)));
    }
}
