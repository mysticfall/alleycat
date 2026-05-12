using AlleyCat.Core;

namespace AlleyCat.Body.Hands;

/// <summary>
/// Holder trait for entities that own left and/or right hand components.
/// </summary>
public interface IHasHands : IComponentHolder
{
    /// <summary>
    /// Attempts to resolve exactly one hand component for the requested side.
    /// </summary>
    bool TryGetHand(LimbSide side, out IHand? hand)
    {
        IHand? match = null;
        int count = 0;

        foreach (IComponent component in Components)
        {
            if (component is not IHand candidate || candidate.Side != side)
            {
                continue;
            }

            count++;
            if (count == 1)
            {
                match = candidate;
            }
            else
            {
                hand = null;
                return false;
            }
        }

        hand = count == 1 ? match : null;

        return count == 1;
    }

    /// <summary>
    /// Resolves exactly one hand component for the requested side.
    /// </summary>
    IHand RequireHand(LimbSide side)
        => TryGetHand(side, out IHand? hand) && hand is not null
            ? hand
            : throw new InvalidOperationException($"Required exactly one {side} hand component on {GetType().FullName ?? GetType().Name}.");
}
