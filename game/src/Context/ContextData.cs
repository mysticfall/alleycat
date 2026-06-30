namespace AlleyCat.Context;

/// <summary>
/// Authored contextual information item presented by a contextual subject or source.
/// </summary>
/// <param name="Title">Stable title for the context item.</param>
/// <param name="Content">Context body content.</param>
public sealed record ContextData(string Title, string Content);
