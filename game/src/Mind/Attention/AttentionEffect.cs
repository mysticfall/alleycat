namespace AlleyCat.Mind.Attention;

/// <summary>One ordered identity-only attention adjustment.</summary>
public sealed record AttentionEffect(string SubjectFullId, float Contribution);
