using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>
/// Capture-time context threaded from the renderer's single capture pass (SCN-002 TR-11/TR-16): the scope root every
/// diagnostic node path is relative to, the editor-tool-context discriminator (TR-7), and the per-request marker
/// style overrides (TR-19). The renderer scopes paths to its discovery scope — the edited scene root for the editor
/// preview — so diagnostics never traverse host UI hierarchies such as the editor's own Control tree; the live-node
/// authoring helpers use <see cref="Authoring" />, which has no scope, no overrides and falls back to full node paths.
/// </summary>
/// <param name="ScopeRoot">Node all diagnostic node paths are relative to; <c>null</c> falls back to full paths.</param>
/// <param name="EditorToolContext">True in editor-tool contexts, discriminated by the renderer from
/// <c>Engine.IsEditorHint()</c>.</param>
/// <param name="MarkerOverrides">Per-request marker style overrides keyed by exact resolved <c>FullId</c> (TR-19);
/// <c>null</c> or empty means no overrides and skips every override lookup.</param>
internal sealed record MiniMapCaptureContext(
    Node? ScopeRoot,
    bool EditorToolContext,
    IReadOnlyDictionary<string, MiniMapMarkerOverride>? MarkerOverrides = null)
{
    /// <summary>Authoring-helper context with no renderer scope: full node paths, live editor detection, no overrides.</summary>
    internal static MiniMapCaptureContext Authoring => new(ScopeRoot: null, Engine.IsEditorHint());

    /// <summary>Node path relative to the scope root (TR-16); the node's full path when no scope is known.</summary>
    /// <param name="node">Node to name.</param>
    /// <returns>Scope-relative node path, or the scope root's own name for the scope root itself.</returns>
    internal string PathFor(Node node)
    {
        if (ScopeRoot is not { } scope)
        {
            return node.GetPath().ToString();
        }

        string relative = scope.GetPathTo(node).ToString();
        return relative == "." ? node.Name : relative;
    }

    /// <summary>
    /// Resolves the TR-19 override applying to a marker with the given resolved identity: an exact, case-sensitive
    /// <c>FullId</c> key match. Markers without identity never match; an absent or empty map short-circuits so the
    /// no-override capture path stays untouched.
    /// </summary>
    /// <param name="fullId">Resolved <c>FullId</c> of the marker being captured, or <c>null</c>.</param>
    /// <returns>The matching override record, or <c>null</c> when the marker does not match.</returns>
    internal MiniMapMarkerOverride? ResolveOverride(string? fullId) =>
        MarkerOverrides is { Count: > 0 } && fullId is { } id && MarkerOverrides.TryGetValue(id, out MiniMapMarkerOverride? markerOverride)
            ? markerOverride
            : null;
}

/// <summary>
/// Identity resolved at capture time from the marker's ancestry (SCN-002 TR-7): the canonical <c>FullId</c> text, or —
/// in an editor-tool context that found no instantiated <c>IIdentifiable</c> ancestor — the nearest scripted-ancestor
/// description backing the informational <c>IdentityResolvesAtRuntime</c> preview notice. Identity is never fabricated
/// from authored properties: a <c>null</c> <see cref="FullId" /> stays <c>null</c> and the label stays omitted.
/// </summary>
/// <param name="FullId">Canonical identity text resolved from an instantiated ancestor, or <c>null</c>.</param>
/// <param name="EditorToolContext">True when captured in an editor-tool context (TR-7): an unresolved identity label
/// reports the informational preview notice instead of the runtime error.</param>
/// <param name="ScriptedAncestor">Nearest ancestor node bearing an attached script, named by the preview notice;
/// <c>null</c> outside editor-tool contexts or when no ancestor carries a script.</param>
public sealed record MiniMapIdentityCapture(
    string? FullId,
    bool EditorToolContext,
    MiniMapScriptedAncestor? ScriptedAncestor)
{
    /// <summary>Capture of resolved identity text; carries no preview-notice data.</summary>
    /// <param name="fullId">Resolved identity text, or <c>null</c>.</param>
    /// <returns>Capture for a render whose identity resolved.</returns>
    public static MiniMapIdentityCapture Resolved(string? fullId) =>
        new(fullId, EditorToolContext: false, ScriptedAncestor: null);
}
