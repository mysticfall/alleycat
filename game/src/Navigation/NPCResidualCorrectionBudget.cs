using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

public enum NPCResidualCorrectionScope
{
    None,
    Travel,
    Exit,
    Settlement,
}

/// <summary>
/// Revisioned accounting for synthetic NPC correction. Authored Root motion never enters this ledger.
/// </summary>
public sealed class NPCResidualCorrectionBudget
{
    public long DestinationGeneration
    {
        get; private set;
    }
    public long RouteRevision
    {
        get; private set;
    }
    public NPCResidualCorrectionScope Scope
    {
        get; private set;
    }
    public float PlanarLimit
    {
        get; private set;
    }
    public float GenericPlanarLimit
    {
        get; private set;
    }
    public float TerminalPlanarLimit
    {
        get; private set;
    }
    public float YawLimit
    {
        get; private set;
    }
    public float RealisedPlanar
    {
        get; private set;
    }
    public float RealisedYaw
    {
        get; private set;
    }
    public float RealisedGenericPlanar
    {
        get; private set;
    }
    public float RealisedTerminalPlanar
    {
        get; private set;
    }
    public float RemainingPlanar => Math.Max(PlanarLimit - RealisedPlanar, 0f);
    public float RemainingGenericPlanar => Math.Max(GenericPlanarLimit - RealisedGenericPlanar, 0f);
    public float RemainingTerminalPlanarCapacity => Math.Min(
        RemainingPlanar,
        Math.Max(TerminalPlanarLimit - RealisedTerminalPlanar, 0f));
    public float RemainingYaw => Math.Max(YawLimit - RealisedYaw, 0f);
    public bool HasTerminalReservation
    {
        get; private set;
    }
    public float ReservedTerminalPlanar
    {
        get; private set;
    }
    public float ReservedTerminalYaw
    {
        get; private set;
    }
    public float RealisedReservedTerminalPlanar
    {
        get; private set;
    }
    public float RemainingReservedTerminalPlanar => HasTerminalReservation
        ? Math.Max(ReservedTerminalPlanar - RealisedReservedTerminalPlanar, 0f)
        : 0f;

    public void BeginRoute(long destinationGeneration, long routeRevision, float planarLimit, float yawLimit)
        => BeginRoute(destinationGeneration, routeRevision, planarLimit, planarLimit, yawLimit);

    public void BeginRoute(
        long destinationGeneration,
        long routeRevision,
        float genericPlanarLimit,
        float terminalPlanarLimit,
        float yawLimit)
    {
        bool preserveRealisedAccounting = Scope != NPCResidualCorrectionScope.None
            && DestinationGeneration == destinationGeneration;
        float realisedPlanar = RealisedPlanar;
        float realisedYaw = RealisedYaw;
        float realisedGenericPlanar = RealisedGenericPlanar;
        float realisedTerminalPlanar = RealisedTerminalPlanar;
        DestinationGeneration = destinationGeneration;
        RouteRevision = routeRevision;
        HasTerminalReservation = false;
        ReservedTerminalPlanar = 0f;
        ReservedTerminalYaw = 0f;
        RealisedReservedTerminalPlanar = 0f;
        Scope = NPCResidualCorrectionScope.Travel;
        GenericPlanarLimit = SafeLimit(genericPlanarLimit);
        TerminalPlanarLimit = SafeLimit(terminalPlanarLimit);
        PlanarLimit = Math.Max(GenericPlanarLimit, TerminalPlanarLimit);
        YawLimit = SafeLimit(yawLimit);
        RealisedPlanar = preserveRealisedAccounting ? realisedPlanar : 0f;
        RealisedYaw = preserveRealisedAccounting ? realisedYaw : 0f;
        RealisedGenericPlanar = preserveRealisedAccounting ? realisedGenericPlanar : 0f;
        RealisedTerminalPlanar = preserveRealisedAccounting ? realisedTerminalPlanar : 0f;
    }

    public void BeginScope(NPCResidualCorrectionScope scope) => Scope = scope;

    public static bool CanReserveTerminal(float requestedPlanar, float requestedYaw, float planarCapacity, float yawCapacity)
        => float.IsFinite(requestedPlanar)
            && float.IsFinite(requestedYaw)
            && requestedPlanar >= 0f
            && requestedYaw >= 0f
            && requestedPlanar <= SafeLimit(planarCapacity)
            && requestedYaw <= SafeLimit(yawCapacity);

    public bool ReserveTerminal(float requestedPlanar, float requestedYaw, float planarCapacity, float yawCapacity)
    {
        if (HasTerminalReservation)
        {
            return false;
        }

        float availablePlanar = Math.Min(RemainingTerminalPlanarCapacity, SafeLimit(planarCapacity));
        float availableYaw = Math.Min(RemainingYaw, SafeLimit(yawCapacity));
        if (!CanReserveTerminal(requestedPlanar, requestedYaw, availablePlanar, availableYaw))
        {
            return false;
        }

        HasTerminalReservation = true;
        ReservedTerminalPlanar = requestedPlanar;
        ReservedTerminalYaw = requestedYaw;
        RealisedReservedTerminalPlanar = 0f;
        return true;
    }

    public bool ReserveTerminal(float requestedPlanar, float requestedYaw, float yawCapacity)
        => ReserveTerminal(requestedPlanar, requestedYaw, TerminalPlanarLimit, yawCapacity);

    /// <summary>
    /// Records a travel terminal reservation for observability only: exit-entry commitments
    /// reserve their full forecast residual without a capacity gate — planned shaping plus unbounded tapered
    /// settlement land the request — while realised correction still charges the ledger through
    /// <see cref="AccountTerminalRealised" />.
    /// </summary>
    public void RecordTerminal(float requestedPlanar, float requestedYaw)
    {
        HasTerminalReservation = true;
        ReservedTerminalPlanar = SafeLimit(requestedPlanar);
        ReservedTerminalYaw = SafeLimit(requestedYaw);
        RealisedReservedTerminalPlanar = 0f;
    }

    public void CancelTerminalReservation()
    {
        HasTerminalReservation = false;
        ReservedTerminalPlanar = 0f;
        ReservedTerminalYaw = 0f;
        RealisedReservedTerminalPlanar = 0f;
    }

    public Vector2 LimitPlanarRequest(Vector2 requested)
        => LimitPlanarRequest(requested, Math.Min(RemainingPlanar, RemainingGenericPlanar));

    public float LimitYawRequest(float requested)
        => float.IsFinite(requested) ? Mathf.Clamp(requested, -RemainingYaw, RemainingYaw) : 0f;

    public void AccountRealised(Vector2 requestedPlanar, Vector2 realisedPlanar, float requestedYaw, float realisedYaw)
    {
        float planar = Math.Min(
            AccountedMagnitude(requestedPlanar, realisedPlanar),
            Math.Min(RemainingPlanar, RemainingGenericPlanar));
        RealisedPlanar += planar;
        RealisedGenericPlanar += planar;
        AccountRealisedYaw(requestedYaw, realisedYaw);
    }

    public void AccountTerminalRealised(Vector2 requestedPlanar, Vector2 realisedPlanar, float requestedYaw, float realisedYaw)
    {
        float planar = Math.Min(
            AccountedMagnitude(requestedPlanar, realisedPlanar),
            Math.Min(RemainingPlanar, RemainingReservedTerminalPlanar));
        RealisedPlanar += planar;
        RealisedTerminalPlanar += planar;
        RealisedReservedTerminalPlanar += planar;
        AccountRealisedYaw(requestedYaw, realisedYaw);
    }

    public void Invalidate()
    {
        DestinationGeneration = 0;
        RouteRevision = 0;
        Scope = NPCResidualCorrectionScope.None;
        PlanarLimit = 0f;
        GenericPlanarLimit = 0f;
        TerminalPlanarLimit = 0f;
        YawLimit = 0f;
        RealisedPlanar = 0f;
        RealisedYaw = 0f;
        RealisedGenericPlanar = 0f;
        RealisedTerminalPlanar = 0f;
        HasTerminalReservation = false;
        ReservedTerminalPlanar = 0f;
        ReservedTerminalYaw = 0f;
        RealisedReservedTerminalPlanar = 0f;
    }

    private void AccountRealisedYaw(float requestedYaw, float realisedYaw)
        => RealisedYaw += Math.Min(
            NPCYawCommandStep.AccountedRealisedMagnitude(requestedYaw, realisedYaw),
            RemainingYaw);

    private static Vector2 LimitPlanarRequest(Vector2 requested, float capacity)
        => requested.IsFinite() ? requested.LimitLength(Math.Max(capacity, 0f)) : Vector2.Zero;

    private static float AccountedMagnitude(Vector2 requested, Vector2 realised)
    {
        float requestedLength = requested.Length();
        return requestedLength <= 0f || !requested.IsFinite() || !realised.IsFinite()
            ? 0f
            : Mathf.Clamp(realised.Dot(requested / requestedLength), 0f, requestedLength);
    }

    private static float SafeLimit(float value) => float.IsFinite(value) ? Math.Max(value, 0f) : 0f;
}
