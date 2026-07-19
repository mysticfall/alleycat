---
id: AI-001
title: Mind Component
---

# Mind Component

## Requirement

The system must provide a Mind component family that lets an NPC hear external speech and respond through its own
character-owned voice.

## Goal

Provide a minimal realtime conversation prototype where the player can speak to the mirror-room NPC and hear that NPC
reply aloud using its asset-owned character identity.

## User Requirements

1. Any externally voiced character must be able to speak to an NPC in the mirror-room test scene.
2. The NPC must answer as its authored character identity in the mirror-room test scene.
3. The NPC response must be spoken through the in-world voice component, not displayed as normal chat text.
4. The NPC must provide one reply to each accepted utterance, then wait for further speech.
5. NPC replies must be generated from system instructions rendered with the NPC's current character context.
6. If the backend is unavailable or misconfigured, the scene must fail gracefully with logged errors.

## Technical Requirements

1. The abstract Mind base must implement the voice-listener contract and accept nonblank speech from every external
   voice except its configured output voice.
2. Mind must not expose `PlayerVoiceId` or apply player-only input filtering.
3. Mind must own generic observation queueing, cumulative weight triggering, maximum-wait scheduling, and processing
   guards for derived minds.
4. Queued observations must wait no more than 10 seconds by default before Mind processing.
5. Disabling Mind must stop observation scheduling immediately while preserving pending observations for later
   re-enable.
6. The concrete AgenticMind component must call its exported character-owned voice reference to speak responses.
7. Chat-client backend creation must be delegated to an exported, replaceable Godot Resource client provider.
8. AgenticMind must own system-instruction rendering, exported tool-resource selection, client-provider wiring, Agent
   Framework turn execution, and session state caching.
9. Exported tool resources must follow the dynamic Resource and per-turn `ChatOptions` contract in
   [AI-002](../002-agent-runtime/index.md).
10. The initial client provider must supply an OpenAI-compatible chat client to the Agent Framework adapter.
11. The OpenAI-compatible provider must expose an editor-selectable client kind for chat-completions or responses
    adapters.
12. AgenticMind must export `SystemInstruction` as a `PromptStack` compatible with
    [AI-003](../003-prompt-api/index.md).
13. AgenticMind must compile its authorable Handlebars-backed `SystemInstruction` `PromptStack` once for its node
    lifetime. The resource is an authoring template and must not cache rendered output.
14. AgenticMind must snapshot prompt, lore, and system context on the first eligible speech observation and render the
    compiled template once from that snapshot.
15. The Agent Framework agent and session created from that first snapshot must remain stable for the AgenticMind node
    lifetime; later turns must not rebuild identity or instructions from mutable scene context.
16. AgenticMind must render with the owning character's SCN-001 prompt context defined by CTX-001, including `character`
    and `characters`.
17. AgenticMind must consume CTX-001 dictionaries without adding any dependency from `AlleyCat.Context` to AI,
     prompt, or templating APIs.
18. Male and female NPC role templates must use one shared generic prompt-stack resource with context-driven
    `{{ character.Id }}`, generic tool and speech instructions, and essential then character lore in authored order.
19. The `speak` tool must invoke AgenticMind's configured `IVoice` output rather than returning visible text.
20. Tool invocation services must include the calling AgenticMind and its configured `IVoice` so Resource tools can
     execute against that instance.
21. Each speech turn must accept at most one `speak` tool call before waiting for more speech.
22. Listening must remain paused for a short cooldown after the NPC starts speaking.
23. OpenAI-compatible backend settings must bind/read subsystem-owned AI options from CORE-006 `IConfiguration`, or
     build a local custom-path JSON configuration when an explicit path is supplied.
24. Observation prompt rendering must be polymorphic on the observation contract, not hard-coded by provider type
     checks.
25. The mirror-room test scene must contain the minimum player and NPC voice wiring needed for conversation testing.
26. `SpeechObservation` must retain `VoiceId`, nullable recognised `CharacterId`, `Content`, and `Weight`.
27. AgenticMind must resolve recognition through a mind-relative resolver from `VoiceId` to nullable `CharacterId`;
    the initial resolver uses identity mapping.
28. Observation prompt text must identify a recognised speaker by `CharacterId` or use an unknown-voice label. It must
    never present raw `VoiceId` as proof of character recognition.
29. Agent Framework technical agent names derive from the owning `Character.Id`; framework descriptions remain generic.
    Prompt-visible identity comes from character context and lore.

### AI-002 Runtime Sync Note

The AgenticMind speech path fulfils the AI-001 contract through the AI-002 runtime: accepted external voice input is
queued as a speech observation by the base Mind cycle, AgenticMind executes the agent turn, and `speak` tool calls
receive execution services through `IServiceProvider` at invocation time. This preserves the one-spoken-reply boundary
while keeping backend failures contained to logged errors.

## In Scope

- Abstract Mind base node for mind-like voice listeners and generic observation-cycle scheduling.
- AgenticMind node component for external-speech-triggered NPC responses.
- AgenticMind-owned prompt-stack system instructions, exported tool selection, client-provider wiring, and Agent
  Framework turn orchestration.
- One-time AgenticMind compilation and first-eligible-speech rendering of the assigned `SystemInstruction` prompt stack.
- Shared generic NPC prompt authoring for male and female role templates.
- Mind-relative voice recognition and recognised-character speech observations.
- Replaceable Agent Framework client provider Resource for chat-client creation.
- Microsoft Agent Framework prototype backend.
- OpenAI-compatible chat configuration from subsystem-owned AI options.
- Editor-selectable OpenAI chat-completions and responses client adapters.
- Mirror-room scene wiring for manual conversation testing.

## Out Of Scope

- Persistent memory or long-term relationship state.
- Multi-agent orchestration.
- Behaviour or animation planning beyond spoken response output.
- Streaming token or streaming speech playback.
- Persona authoring tools and prompt previews beyond the required generic, context-driven prompt stack.
- NPC-to-NPC attention loops and participant filtering.
- Session reset, scenario or conversation lifecycle, and post-session memory ingestion.

## Acceptance Criteria

1. An AgenticMind node receives nonblank speech from any external voice and ignores only its own output voice.
2. Accepted speech is queued as an observation by Mind and orchestrated by AgenticMind into an Agent Framework turn.
3. The mirror-room NPC answers as its authored identity through spoken in-world voice output.
4. Character context values are available to the rendered system instructions used for the NPC reply.
5. The OpenAI-compatible client provider supplies the chat client used by the default Agent Framework adapter.
6. Agent Framework turn execution and session state caching are owned by `AgenticMind`.
7. Exported tool resources are delivered per turn through `ChatOptions` under the AI-002 runtime contract.
8. The client provider owns binding/loading for Host, optional ApiKey, Model, and Timeout settings.
9. The client provider can be switched between OpenAI chat-completions and responses client adapters in the editor.
10. AgenticMind ignores further `speak` tool calls and voice input until the current reply turn completes.
11. Tool invocation uses an `IServiceProvider` context that contains the calling AgenticMind and configured `IVoice`
    for that turn.
12. Observation prompt formatting is verified through the observation contract without concrete-type switches in
    AgenticMind or provider code.
13. `AgenticMind.SystemInstruction` is an exported `PromptStack` compiled once into a reusable `ITemplate` instead of
    hard-coded production persona text.
14. First eligible speech snapshots prompt, lore, and system context; the resulting agent, rendered instructions, and
    session remain stable for the AgenticMind node lifetime.
15. Rendering receives CTX-001 `character` and `characters` context for the owning character.
16. CTX-001 remains independent from AI, prompt, and templating APIs, and no `ContextData` type is reintroduced.
17. Male and female NPC templates share one generic prompt stack containing context-driven identity, generic tool and
    speech instructions, `EssentialLorePromptSection`, then `CharacterLorePromptSection`.
18. Disabled Mind instances do not process queued or newly received voice observations until re-enabled.
19. Missing voice/backend configuration and backend failures are logged without crashing the scene.
20. Acceptance covers both player-visible conversation behaviour and the component/backend integration contract.
21. Speech observations preserve raw voice provenance separately from nullable character recognition, and prompt text
    never treats raw `VoiceId` as recognised identity.
22. Agent Framework technical names derive from exact owning `Character.Id`, while descriptions remain generic.

## References

### Implementation

- game/src/Mind/Mind.cs
- game/src/Mind/AI/AgenticMind.cs
- game/src/Mind/AI/Tool/AgentTool.cs
- game/src/Mind/AI/Tool/SpeechTool.cs
- game/src/Mind/AI/Provider/ClientProvider.cs
- game/src/Mind/AI/Provider/OpenAIClientProvider.cs
- game/assets/testing/mirror_room/mirror_room.tscn
- game/AlleyCat.json

### Related Specs

- BODY-006: Voice Component
- SPCH-003: Transcriber Component
- SPCH-004: Speech Generator Component
- CORE-006: Microsoft Configuration Integration
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-004: Lore And Backstory Source Compilation](../004-lore-backstory/index.md)
- [CTX-001: Contextual Information API](../../context/001-contextual-information-api/index.md)
- [TMPL-001: Templating System](../../templating/001-templating-system/index.md)

### External Dependencies

- Microsoft Agent Framework
