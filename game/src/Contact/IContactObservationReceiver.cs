namespace AlleyCat.Contact;

/// <summary>
/// Receiver-side raw-contact delivery seam (INTR-005 TR2, TR10, TR11): a receiver body that hosts contact
/// receiving capability receives the sweeper's coalesced observation directly and forwards it to its
/// classifier hub. Generated character body parts are the raw-event origin — each part is wired to its
/// character's <see cref="ContactMonitor" /> once, at generation time, so raw delivery never searches for
/// a hub at contact time. World receivers host the monitor on their own opted-in body instead; the
/// sweeper serves both hostings through this one receiver-targeted seam.
/// </summary>
public interface IContactObservationReceiver
{
    /// <summary>
    /// Receives one sweeper's coalesced observation of this receiver body for the current physics tick and
    /// forwards it to this receiver's classifier hub. Observations on a receiver whose hub is not wired are
    /// dropped, mirroring receiver-eligible bodies without a host monitor.
    /// </summary>
    /// <param name="source">The contacting counterpart party.</param>
    /// <param name="observation">The sweeper's coalesced observation for this tick.</param>
    void ObserveContact(ContactParty source, in ContactSweepObservation observation);
}
