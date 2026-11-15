namespace AlleyCat.Contact;

/// <summary>
/// One side of a contact pair: either the contacting counterpart — whose hand, foot, or object caused the
/// contact — or the receiving body part (INTR-005 TR8, TR11). Owner attribution rides in this payload record
/// so character receivers and world receivers share one delivery path: a footstep is a ground-receiver event
/// whose foot attribution is simply the source party.
/// </summary>
public sealed record ContactParty
{
    /// <summary>
    /// Creates a party descriptor.
    /// </summary>
    /// <param name="key">Stable key identifying this party within its host, for contact-pair bookkeeping.</param>
    /// <param name="displayName">Human-readable part or object name, such as "Right Hand" or "Floor".</param>
    /// <param name="ownerName">
    /// Owning character's name when the party belongs to a character; <see langword="null" /> for unowned
    /// world objects and surfaces. Blank is rejected — absence is <see langword="null" />.
    /// </param>
    public ContactParty(string key, string displayName, string? ownerName = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("The party key must be a non-blank string.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("The party display name must be a non-blank string.", nameof(displayName));
        }

        if (ownerName is not null && string.IsNullOrWhiteSpace(ownerName))
        {
            throw new ArgumentException(
                "The party owner name must be a non-blank string or null; absence is null.", nameof(ownerName));
        }

        Key = key;
        DisplayName = displayName;
        OwnerName = ownerName;
    }

    /// <summary>Stable key identifying this party within its host, for contact-pair bookkeeping.</summary>
    public string Key
    {
        get;
    }

    /// <summary>Human-readable part or object name, such as "Right Hand" or "Floor".</summary>
    public string DisplayName
    {
        get;
    }

    /// <summary>Owning character's name when the party belongs to a character; otherwise <see langword="null" />.</summary>
    public string? OwnerName
    {
        get;
    }

    /// <summary>
    /// Attribution wording for event payloads: "owner's part" when the party belongs to a character, otherwise
    /// the bare part or object name (INTR-005 User Requirement 4).
    /// </summary>
    public string Descriptor => OwnerName is null ? DisplayName : $"{OwnerName}'s {DisplayName}";
}
