namespace AlleyCat.IK;

/// <summary>
/// IK target provider whose internal interpolation advances on supplied simulation time instead of wall-clock
/// reads (IK-005 TR20). The owning <see cref="CharacterIK" /> advances it exactly once per authoritative
/// physics-actuation tick, at the canonical source-sampling epoch.
/// </summary>
public interface IIKSimulationAdvancable
{
    /// <summary>
    /// Advances the provider's interpolation state by one authoritative simulation tick. Observational getters
    /// such as <see cref="IKTargetIntentProvider.GetTargetIntent" /> must not advance interpolation.
    /// </summary>
    /// <param name="deltaSeconds">Simulation delta for this tick in seconds.</param>
    void AdvanceSimulation(double deltaSeconds);
}
