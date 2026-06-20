---
id: AI-001
title: Mind Component
---

# Mind Component

## Requirement

The system must provide a Mind component family that lets an NPC hear player speech and respond through the NPC voice.

## Goal

Provide a minimal realtime conversation prototype where the player can speak to the mirror-room NPC and hear Alley
reply aloud as an embodied character.

## User Requirements

1. The player must be able to speak to an NPC in the mirror-room test scene.
2. The NPC must answer as Alley in the mirror-room test scene.
3. The NPC response must be spoken through the in-world voice component, not displayed as normal chat text.
4. The NPC must provide one reply to each player utterance, then wait for the player's next speech.
5. If the backend is unavailable or misconfigured, the scene must fail gracefully with logged errors.

## Technical Requirements

1. The abstract Mind base must implement the voice-listener contract and listen only for the configured player voice ID.
2. The initial player voice ID must default to `player`.
3. Mind must own generic observation queueing, cumulative weight triggering, maximum-wait scheduling, and processing
   guards for derived minds.
4. Queued observations must wait no more than 10 seconds by default before Mind processing.
5. Disabling Mind must stop observation scheduling immediately while preserving pending observations for later
   re-enable.
6. The concrete AgenticMind component must call an exported NPC voice reference to speak responses.
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
13. AgenticMind must compile and render `SystemInstruction` into Agent Framework instructions for each agent turn.
14. The mirror-room AgenticMind scene configuration must provide the Alley prompt through `SystemInstruction` as one
    `TextPromptSection` named `Instructions`.
15. The `speak` tool must invoke AgenticMind's configured `IVoice` output rather than returning visible text.
16. Tool invocation services must include the calling AgenticMind and its configured `IVoice` so Resource tools can
    execute against that instance.
17. Each player-speech turn must accept at most one `speak` tool call before waiting for more player speech.
18. Player listening must remain paused for a short cooldown after the NPC starts speaking.
19. OpenAI-compatible backend settings must bind/read subsystem-owned AI options from CORE-006 `IConfiguration`, or
    build a local custom-path JSON configuration when an explicit path is supplied.
20. Observation prompt rendering must be polymorphic on the observation contract, not hard-coded by provider type
    checks.
21. The mirror-room test scene must contain the minimum player and NPC voice wiring needed for conversation testing.

### AI-002 Runtime Sync Note

The AgenticMind speech path now fulfils the AI-001 contract through the AI-002 runtime: player voice input is queued as
a speech observation by the base Mind cycle, AgenticMind executes the agent turn, and backend `speak` tool calls receive
execution services through `IServiceProvider` at invocation time. This preserves the one-spoken-reply turn boundary
while keeping backend failures contained to logged errors.

## In Scope

- Abstract Mind base node for mind-like voice listeners and generic observation-cycle scheduling.
- AgenticMind node component for player-speech-triggered NPC responses.
- AgenticMind-owned prompt-stack system instructions, exported tool selection, client-provider wiring, and Agent
  Framework turn orchestration.
- Mirror-room Alley prompt assignment through an AI-003 `PromptStack` with one `TextPromptSection`.
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
- Final persona authoring tools, prompt previews, or dynamic character prompts beyond the mandatory exported
  `SystemInstruction` prompt-stack integration.

## Acceptance Criteria

1. An AgenticMind node in the mirror room receives speech from a voice whose ID is `player`.
2. Player speech is queued as an observation by Mind and orchestrated by AgenticMind into an Agent Framework turn.
3. The mirror-room NPC answers as Alley through spoken in-world voice output.
4. The OpenAI-compatible client provider supplies the chat client used by the default Agent Framework adapter.
5. Agent Framework turn execution and session state caching are owned by `AgenticMind`.
6. Exported tool resources are delivered per turn through `ChatOptions` under the AI-002 runtime contract.
7. The client provider owns binding/loading for Host, optional ApiKey, Model, and Timeout settings.
8. The client provider can be switched between OpenAI chat-completions and responses client adapters in the editor.
9. AgenticMind ignores further `speak` tool calls and player voice input until the current reply turn completes.
10. Tool invocation uses an `IServiceProvider` context that contains the calling AgenticMind and configured `IVoice`
    for that turn.
11. Observation prompt formatting is verified through the observation contract without concrete-type switches in
   AgenticMind or provider code.
12. `AgenticMind.SystemInstruction` is an exported `PromptStack` compiled and rendered into Agent Framework
    instructions instead of hard-coded production persona text.
13. The mirror-room AgenticMind node assigns the Alley prompt as one `TextPromptSection` named `Instructions` on
    `SystemInstruction`.
14. Disabled Mind instances do not process queued or newly received voice observations until re-enabled.
15. Missing voice/backend configuration and backend failures are logged without crashing the scene.
16. Acceptance covers both player-visible conversation behaviour and the component/backend integration contract.

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

### External Dependencies

- Microsoft Agent Framework
