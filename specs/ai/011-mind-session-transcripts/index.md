---
id: AI-011
title: Mind Session Transcripts
---

# Mind Session Transcripts

## Requirement

When enabled in game configuration, every AgenticMind provider request cycle must be recorded as one self-contained,
human-readable Markdown transcript file under `user://logs/mind/`, with anomalous cycles recorded as visibly
annotated first-class files, without altering request or response behaviour and without any recording failure
reaching the session.

## Goal

Give developers a readable, per-turn debugging record of AgenticMind ↔ LLM traffic — instructions, request
transcript, response, and tool activity with real newlines and an intact heading hierarchy — replacing Trace-level
escaped-JSON dumps as the primary diagnostics surface, while keeping the agent session completely isolated from
recording.

## User Requirements

1. Opt-In Diagnostics: A developer enables session transcript logging with a single game-configuration toggle. It
   ships disabled, is independent of the existing request/response payload logging toggle, and stays disabled when
   game configuration is unavailable.
2. Complete Per-Cycle Coverage: Every provider request cycle of an enabled session produces exactly one transcript
   file — including anomalous cycles: transport retries, malformed-response recovery, and requests discarded by
   fresh-turn invalidation — with each anomaly visibly annotated and no cycle silently omitted.
3. Self-Contained Readable Files: Each file can be read on its own: a metadata header; the full instructions rendered
   with a proper Markdown heading hierarchy; the full role-labelled request transcript including the per-request
   timeline and scene-status prefix messages, with pseudo-XML sections rendered as headings and line structure kept
   visible; the response contents with reasoning content marked distinctly; and one block per tool invocation with
   pretty-printed JSON arguments and the result.
4. Stable Discoverable Layout: Files live under
   `user://logs/mind/<game-run-start-timestamp>/<character-id>/turn-NNNN.md` with a sortable run timestamp and
   per-character turn numbers that continue monotonically when the same character's mind node is re-created within
   one game run.
5. Harmless By Construction: Recording failures never disturb the running session — they surface as warnings only —
   and a disabled toggle means no file input or output and no change to request or response behaviour.

## Technical Requirements

### Configuration And Toggle

1. Add a `Diagnostics:AI:EnableSessionTranscriptLogging` boolean to `AIDiagnosticsOptions` and carry it through
   `AIDiagnosticsSettings`, following the existing `Diagnostics:AI` binding pattern. The option defaults to `false`,
   `game/AlleyCat.yaml` ships it explicitly `false`, and `LoadOrDefault` fails closed to `false` when game
   configuration is unavailable. The toggle is independent of `EnableRequestResponseLogging`; enabling either must
   not enable the other.

### Storage Layout And Run Lifetime

2. Transcript files live at
   `user://logs/mind/<game-run-start-timestamp>/<character-id>/turn-NNNN.md`. The run timestamp is the sortable
   process-start wall-clock time (for example `yyyy-MM-dd-HH-mm-ss`). The character folder is the owning character's
   validated local `Id` (CORE-009): lower `snake_case` and therefore filesystem-safe. The file name is `turn-` plus
   the cycle number, zero-padded to at least four digits, plus `.md`.
3. The run directory is created lazily once per process on the first recorded write; a process never creates a second
   run directory. No per-session subfolders exist: turn numbering is flat per character per game run.
4. Turn numbering is a per-character, process-wide monotonic counter that starts at one, advances by one per recorded
   request cycle, and survives mind node re-creation within the same game run — a re-created mind for the same
   character continues from the previous counter value.

### Runner-Level Recording

5. Recording is a runner-level sink injected by `AgenticMind`, which owns character identity and diagnostics
   settings. The sink follows the runner's optional-dependency injection pattern (`requestContextSource`); the runner
   stays character-agnostic — it never receives the character, its identity, or the file system. When the toggle is
   disabled, `AgenticMind` injects a null-object sink whose calls do nothing, perform no file input or output, and
   leave runner behaviour unchanged.
6. A transport-level `IChatClient` decorator cannot capture a full cycle: it never sees validation verdicts, tool
   invocations or their results, exchange disposal (AI-002 TR-17–TR-22), or invalidation arbitration, and it cannot
   group retries and recovery into one cycle record. The recorder must therefore observe the request-cycle loop
   directly and capture, per cycle: the materialised request context and its frozen request transcript — prefix
   timeline and scene-status messages, bootstrap input, and retained accepted exchanges — together with the request
   options; the response contents (text, reasoning content, and function calls) plus model and token usage when the
   provider supplies them; the validation outcome; transport retry attempts; invalid-response recovery events with
   their budget state; stale or discarded outcomes from fresh-turn invalidation (AI-002 TR-4/TR-5); the exchange
   disposal decision (AI-002 TR-17); per-tool invocations with the tool name, arguments, and delivered result
   including canonical cancellation results; and wall-clock latency measured across the cycle so retries stay
   attributable. Every cycle that reaches provider interaction produces its file once the cycle's outcome resolves.
7. Recording is fully contained: a recorder failure is logged as a warning and never propagates into the session
   loop, and recording never adds transcript entries nor changes validation, acceptance, disposal, watermark,
   scheduling, or cancellation semantics (AI-002 TR-4/TR-5, TR-17–TR-22).

### Markdown Rendering

8. Every rendered value comes from live .NET objects and strings held by the runner — never from reparsed JSON log
   text — so real newlines are preserved by construction. Message-body rendering keeps single-newline line structure
   visible: consecutive non-blank lines are emitted with explicit Markdown hard line breaks, and content inside
   fenced code blocks is never modified. Absent optional metadata fields (model, token usage) are omitted gracefully
   rather than rendered as placeholders.
9. The formatter parses the pseudo-XML section format produced by `PseudoXmlFormatter` —
   `<SectionName>\ncontent\n</SectionName>` blocks — and renders each section as a Markdown heading with its content
   nested beneath. This parsing applies to the instructions and to every transcript message body: request and
   response text contents render matched sections as headings with no raw pseudo-XML tags left visible.
   Section-name matching is lax: the parser must tolerate whitespace runs and tabs, mixed case, and the sanitised
   `<`, `>`, and `/` characters that `PseudoXmlFormatter` replaces with `_`; content outside recognised blocks
   passes through verbatim. The formatter is presentational only: rendering never changes what the model receives.
10. The formatter demotes any Markdown headings inside section content so the document hierarchy survives nesting:
    no section's internal headings may collide with, or outrank, the section heading itself.
11. Each file contains, in order: the metadata header (turn number, character Id, wall-clock time, model, latency,
    token usage when available); the rendered instructions; the full role-labelled request transcript including the
    per-request timeline and scene-status prefix messages; the response contents — text, distinctly marked reasoning
    content when present, and function calls — with any anomaly annotations; and one block per tool invocation
    showing the tool name, pretty-printed JSON arguments, and the result.

### Test Seams And Validation

12. Provide two internal-only test seams, neither exposed as public API nor game configuration: a recorder root-path
    override so tests redirect writes away from the real `user://` tree, and a counter reset so tests start from a
    known turn-numbering state.
13. Unit tests verify the formatter (lax section names — spaces, tabs, mixed case, and sanitised characters; heading
    demotion; message-body pseudo-XML sections; hard-line-break preservation with fenced code untouched;
    role-labelled transcript rendering; distinctly marked reasoning content; tool blocks with pretty-printed JSON
    arguments and results), the configuration binding including fail-closed defaults, the disabled null-object path
    performing no file input or output, and path and turn-number naming.
14. Integration tests using the existing scripted-client fixtures verify file creation per request cycle, anomaly
    annotations for transport retry, malformed-response recovery, and fresh-turn invalidation discards, and
    write-failure containment with the session continuing normally.

## In Scope

- The `Diagnostics:AI:EnableSessionTranscriptLogging` toggle with binding and fail-closed semantics.
- The `user://logs/mind/` storage layout, per-process run-directory lifetime, and per-character monotonic turn
  numbering that survives mind re-creation.
- The runner-level recorder sink, its per-cycle capture points, and the null-object disabled path.
- The Markdown rendering contract: the lax pseudo-XML section parser applied to instructions and message bodies,
  internal heading demotion, and hard-line-break preservation of message-body line structure.
- Containment of recording failures and independence from `EnableRequestResponseLogging`.
- Internal test seams and the unit and integration validation contracts.

## Out Of Scope

- Retention, rotation, and size caps for the transcript tree; every cycle file is kept.
- Streaming-response rendering: files are written from settled cycle data, not incrementally rendered partial
  responses.
- Capturing non-AgenticMind AI traffic, such as STT or TTS provider traffic.
- Changing `EnableRequestResponseLogging` defaults or behaviour.

## Acceptance Criteria

### User Requirements

1. With the toggle enabled in game configuration, playing a scene produces one readable, self-contained Markdown
   file per provider request cycle under the documented layout, each containing the metadata header, rendered
   instructions, full request transcript, response contents, and tool blocks, with message-body sections rendered as
   headings and line structure visibly preserved.
2. Anomalous cycles — transport retries, malformed-response recovery, and requests discarded by fresh-turn
   invalidation — each produce a file whose anomaly is visibly annotated; no enabled cycle is silently missing.
3. Turn numbers for one character remain flat and monotonically increasing across mind node re-creation within one
   game run, with no per-session subfolders.
4. With the toggle disabled — or absent because game configuration is unavailable — no transcript files are written
   and session behaviour is unchanged.
5. A recording failure during play surfaces only as a warning log, and the session continues normally.

### Technical Requirements

1. Tests verify the option binds from `Diagnostics:AI`, defaults to `false` in options and shipped configuration, and
   fails closed through `LoadOrDefault` without game configuration, independently of `EnableRequestResponseLogging`.
2. Tests verify the formatter accepts lax section names (spaces, tabs, mixed case, and sanitised characters), demotes
   internal Markdown headings below their section heading, renders pseudo-XML sections inside message bodies as
   headings with no raw tags — including the timeline message's `Established Event History` and
   `New Since Your Previous Response` sections — keeps single-newline line structure visible through hard line breaks
   while leaving fenced code blocks unmodified, renders role-labelled messages including the prefix timeline and
   scene-status messages, marks reasoning content distinctly, and renders tool blocks with pretty-printed JSON
   arguments and results (TR-8–TR-11).
3. Tests verify the disabled path injects a null-object sink, performs no file input or output, and leaves existing
   runner behaviour unchanged (TR-5).
4. Tests verify path and counter naming: `turn-` numbers zero-padded to at least four digits, one lazily created run
   directory per process, and per-character numbering that continues monotonically across a simulated mind
   re-creation, with the reset seam restoring a known initial state between tests (TR-2–TR-4, TR-12, TR-13).
5. Integration tests with the scripted-client fixtures verify one file per request cycle; annotations for transport
   retry, invalid-response recovery, fresh-turn invalidation discards, and exchange disposal; capture of the prefix
   timeline and scene-status messages; and that an injected write failure is contained while the session continues
   (TR-6, TR-7, TR-14).
6. Tests verify both test seams are internal-only: no public API and no game-configuration switch redirects the
   recorder root or resets counters (TR-12).

## References

- [AI-002: Agent Runtime](../002-agent-runtime/index.md) — request-cycle loop, fresh-turn invalidation
  (TR-4/TR-5), tool-exchange disposal (TR-17–TR-22)
- [AI-003: Prompt API](../003-prompt-api/index.md) — static instruction and per-request scene status whose rendered
  content the recorder captures
- [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md) — validated character local `Id`
- `game/src/Mind/AI/AgentSessionRunner.cs` — request-cycle loop and optional-dependency injection pattern
- `game/src/Mind/AI/AgenticMind.cs` — composition point owning character identity and diagnostics settings
- `game/src/Mind/AI/AIDiagnosticsOptions.cs` — `Diagnostics:AI` binding and fail-closed pattern
- `game/src/Common/PseudoXmlFormatter.cs` — pseudo-XML block format and tag-name sanitisation
- `game/AlleyCat.yaml` — shipped diagnostics toggles
