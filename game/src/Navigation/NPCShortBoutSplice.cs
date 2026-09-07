using AlleyCat.Control.Locomotion;
using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

/// <summary>Pure, package-driven terminal short-bout splice selection and correction envelope.</summary>
public static class NPCShortBoutSplice
{
    public const float EffectiveZeroDistance = 0.0001f;

    /// <summary>
    /// Whether a request expressed in the actor's semantic frame (X-right, Y-forward) matches the splice's ±Y
    /// forecast semantics. <see cref="TryCreate"/> compares only scalar
    /// length, so callers must apply this guard first: a mostly-lateral request is ineligible regardless of scalar
    /// length — a 90° scalar match is never valid — and belongs to the side-step or travel regimes instead.
    /// </summary>
    public static bool IsDirectionEligible(Vector2 actorSemanticOffset)
        => actorSemanticOffset.IsFinite()
            && Mathf.Abs(actorSemanticOffset.X) <= Mathf.Abs(actorSemanticOffset.Y);

    public static bool TryCreate(
        StandingLocomotionCatalogue catalogue,
        string entryRole,
        float directLength,
        NPCShortBoutSpliceConfiguration configuration,
        out NPCShortBoutSplicePlan plan)
    {
        plan = default;
        if (!float.IsFinite(directLength) || directLength <= EffectiveZeroDistance)
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(catalogue);
        if (
            !catalogue.TryGetShortBoutPair(entryRole, out StandingLocomotionShortBoutPair pair))
        {
            return false;
        }

        StandingLocomotionClip entry = catalogue[pair.EntryRole];
        StandingLocomotionClip exit = catalogue[pair.ExitRole];
        float completeDistance = PlanarLength(entry.EndpointRootDisplacement) + PlanarLength(exit.EndpointRootDisplacement);
        if (directLength >= completeDistance)
        {
            return false;
        }

        float protectedDuration = Mathf.Clamp(configuration.ProtectedEndpointDuration, 0f, 0.25f);
        float entryMaximum = Mathf.Clamp(
            (float)catalogue.GetEventTime(pair.EntryRole, pair.EntryReleaseEvent),
            protectedDuration,
            (float)entry.DurationSeconds - protectedDuration);
        float exitMinimum = Mathf.Clamp(
            (float)catalogue.GetEventTime(pair.ExitRole, pair.ExitReleaseEvent),
            protectedDuration,
            (float)exit.DurationSeconds - protectedDuration);
        if (!TrySelectCuts(entry, exit, directLength, protectedDuration, entryMaximum, exitMinimum, configuration.CrossfadeDuration, out float entryCut, out float exitCut, out float crossfade, out NPCShortBoutRootForecast forecast))
        {
            return false;
        }

        float correction = Mathf.Abs(directLength - forecast.LocalPlanar.Length());
        if (correction > configuration.MaximumCorrectionContribution)
        {
            return false;
        }
        plan = new(pair, entryCut, exitCut, crossfade, forecast.LocalPlanar, forecast.Yaw, completeDistance, correction)
        {
            EntryCapability = entry.Capability,
        };
        return true;
    }

    public static float CorrectionEnvelope(float elapsed, NPCShortBoutSplicePlan plan)
    {
        float crossfadeStart = plan.EntryCutTime - plan.CrossfadeDuration;
        if (elapsed <= crossfadeStart || elapsed >= plan.EntryCutTime - 0.000001f)
        {
            return 0f;
        }

        float t = (elapsed - crossfadeStart) / plan.CrossfadeDuration;
        return 4f * t * (1f - t);
    }

    public static bool CanRetainCrossfadeOverlap(
        float entryCut,
        float exitCut,
        float exitDuration,
        float crossfadeDuration)
        => float.IsFinite(entryCut)
            && float.IsFinite(exitCut)
            && float.IsFinite(exitDuration)
            && float.IsFinite(crossfadeDuration)
            && crossfadeDuration >= 0.01f
            && entryCut + 0.000001f >= crossfadeDuration
            && exitDuration - exitCut + 0.000001f >= crossfadeDuration;

    public static NPCShortBoutRootForecast Forecast(
        StandingLocomotionClip entry,
        StandingLocomotionClip exit,
        float entryCut,
        float exitCut,
        float crossfade)
    {
        NPCShortBoutRootComposition composition = Compose(
            time => PackageSample(entry, time),
            time => PackageSample(exit, time),
            entryCut,
            exitCut,
            (float)exit.DurationSeconds,
            crossfade);
        return new(new Vector2(composition.Position.X, composition.Position.Z), composition.Yaw);
    }

    public static NPCShortBoutRootComposition Compose(
        Func<float, NPCShortBoutRootCompositionSample> entrySampler,
        Func<float, NPCShortBoutRootCompositionSample> exitSampler,
        float entryCut,
        float exitCut,
        float exitDuration,
        float crossfade)
    {
        ArgumentNullException.ThrowIfNull(entrySampler);
        ArgumentNullException.ThrowIfNull(exitSampler);
        const int integrationSteps = 64;
        float crossfadeStart = Math.Max(entryCut - crossfade, 0f);
        NPCShortBoutRootCompositionSample entryStart = entrySampler(0f);
        NPCShortBoutRootCompositionSample previousEntry = entrySampler(crossfadeStart);
        Vector3 position = previousEntry.Position - entryStart.Position;
        float yaw = previousEntry.Yaw - entryStart.Yaw;
        NPCShortBoutRootCompositionSample previousExit = exitSampler(exitCut);
        for (int step = 1; step <= integrationSteps; step++)
        {
            float progress = step / (float)integrationSteps;
            float previousProgress = (step - 1f) / integrationSteps;
            NPCShortBoutRootCompositionSample currentEntry = entrySampler(
                Mathf.Lerp(crossfadeStart, entryCut, progress));
            NPCShortBoutRootCompositionSample currentExit = exitSampler(exitCut + (crossfade * progress));
            float entryWeight = 1f - ((previousProgress + progress) * 0.5f);
            float exitWeight = (previousProgress + progress) * 0.5f;
            position += ((currentEntry.Position - previousEntry.Position) * entryWeight)
                + ((currentExit.Position - previousExit.Position) * exitWeight);
            yaw += (Mathf.Wrap(currentEntry.Yaw - previousEntry.Yaw, -Mathf.Pi, Mathf.Pi) * entryWeight)
                + (Mathf.Wrap(currentExit.Yaw - previousExit.Yaw, -Mathf.Pi, Mathf.Pi) * exitWeight);
            previousEntry = currentEntry;
            previousExit = currentExit;
        }

        NPCShortBoutRootCompositionSample endpoint = exitSampler(exitDuration);
        position += endpoint.Position - previousExit.Position;
        yaw += endpoint.Yaw - previousExit.Yaw;
        return new(position, yaw);
    }

    private static NPCShortBoutRootCompositionSample PackageSample(StandingLocomotionClip clip, float time)
    {
        StandingLocomotionResponseSample sample = clip.SampleResponse(time);
        return new(new Vector3(sample.CumulativeLocalPlanar.X, 0f, sample.CumulativeLocalPlanar.Y), sample.Yaw);
    }

    private static bool TrySelectCuts(
        StandingLocomotionClip entry,
        StandingLocomotionClip exit,
        float directLength,
        float protectedDuration,
        float entryMaximum,
        float exitMinimum,
        float requestedCrossfade,
        out float selectedEntryCut,
        out float selectedExitCut,
        out float selectedCrossfade,
        out NPCShortBoutRootForecast selectedForecast)
    {
        const int candidateCount = 24;
        float bestError = float.PositiveInfinity;
        float bestRetainedDuration = float.NegativeInfinity;
        selectedEntryCut = 0f;
        selectedExitCut = 0f;
        selectedCrossfade = 0f;
        selectedForecast = default;
        for (int entryIndex = 0; entryIndex <= candidateCount; entryIndex++)
        {
            float entryCut = Mathf.Lerp(protectedDuration, entryMaximum, entryIndex / (float)candidateCount);
            for (int exitIndex = 0; exitIndex <= candidateCount; exitIndex++)
            {
                float exitCut = Mathf.Lerp(exitMinimum, (float)exit.DurationSeconds - protectedDuration, exitIndex / (float)candidateCount);
                if (!CanRetainCrossfadeOverlap(
                    entryCut,
                    exitCut,
                    (float)exit.DurationSeconds,
                    requestedCrossfade))
                {
                    continue;
                }
                float crossfade = requestedCrossfade;
                NPCShortBoutRootForecast forecast = Forecast(entry, exit, entryCut, exitCut, crossfade);
                float error = Mathf.Abs(directLength - forecast.LocalPlanar.Length());
                float retainedDuration = entryCut + ((float)exit.DurationSeconds - exitCut);
                if (error > bestError || (Mathf.IsEqualApprox(error, bestError) && retainedDuration <= bestRetainedDuration))
                {
                    continue;
                }

                bestError = error;
                bestRetainedDuration = retainedDuration;
                selectedEntryCut = entryCut;
                selectedExitCut = exitCut;
                selectedCrossfade = crossfade;
                selectedForecast = forecast;
            }
        }

        return float.IsFinite(bestError);
    }

    private static float PlanarLength(Vector3 displacement) => new Vector2(displacement.X, displacement.Z).Length();
}

public sealed record NPCShortBoutSpliceConfiguration
{
    public float ProtectedEndpointDuration { get; init; } = 0.08f;
    public float CrossfadeDuration { get; init; } = 0.12f;
    public float MaximumCorrectionContribution { get; init; } = 0.7f;
}

public readonly record struct NPCShortBoutSplicePlan(
    StandingLocomotionShortBoutPair Pair,
    float EntryCutTime,
    float ExitCutTime,
    float CrossfadeDuration,
    Vector2 ForecastLocalPlanar,
    float ForecastYaw,
    float CompletePairDisplacement,
    float RequiredCorrection)
{
    public StandingLocomotionCapability EntryCapability
    {
        get; init;
    }
}

public readonly record struct NPCShortBoutRootForecast(Vector2 LocalPlanar, float Yaw);
public readonly record struct NPCShortBoutRootComposition(Vector3 Position, float Yaw);
public readonly record struct NPCShortBoutRootCompositionSample(Vector3 Position, float Yaw);
