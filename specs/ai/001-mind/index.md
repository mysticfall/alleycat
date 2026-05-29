---
id: AI-001
title: Mind Component
---

# Mind Component

## Requirement

The system must provide a Mind component that lets an NPC hear player speech and respond through the NPC voice.

## Goal

Provide a minimal realtime conversation prototype where the player can speak to the mirror-room NPC and hear Alley
reply aloud as an embodied character.

## User Requirements

1. The player must be able to speak to an NPC in the mirror-room test scene.
2. The NPC must answer as Alley, a hard-coded prototype persona.
3. The NPC response must be spoken through the in-world voice component, not displayed as normal chat text.
4. The NPC must provide one reply to each player utterance, then wait for the player's next speech.
5. If the backend is unavailable or misconfigured, the scene must fail gracefully with logged errors.

## Technical Requirements

1. The Mind component must implement the voice-listener contract and listen only for the configured player voice ID.
2. The initial player voice ID must default to `player`.
3. The Mind component must call an exported NPC voice reference to speak responses.
4. Agent backend creation must be delegated to an exported, replaceable Godot Resource provider.
5. The Mind component must own Microsoft Agent Framework turn execution, session state, tools, and persona setup.
6. The initial provider must supply an OpenAI-compatible chat client to the Agent Framework adapter.
7. The OpenAI-compatible provider must expose an editor-selectable client kind for chat-completions or responses
   adapters.
8. The Mind component must hard-code the Alley persona and instruct the agent to use a `speak` tool for all replies.
9. The `speak` tool must invoke the Mind component's configured voice output rather than returning player-facing text.
10. Each player-speech turn must accept at most one `speak` tool call before waiting for more player speech.
11. Player listening must remain paused for a short cooldown after the NPC starts speaking.
12. OpenAI-compatible backend settings must load from the merged configuration API under an `[AI]` section.
13. The mirror-room test scene must contain the minimum player and NPC voice wiring needed for conversation testing.

## In Scope

- Mind node component for player-speech-triggered NPC responses.
- Replaceable Agent Framework backend provider Resource.
- Microsoft Agent Framework prototype backend.
- OpenAI-compatible chat configuration.
- Editor-selectable OpenAI chat-completions and responses client adapters.
- Mirror-room scene wiring for manual conversation testing.

## Out Of Scope

- Persistent memory or long-term relationship state.
- Multi-agent orchestration.
- Behaviour or animation planning beyond spoken response output.
- Streaming token or streaming speech playback.
- Final persona authoring tools or dynamic character prompts.

## Acceptance Criteria

1. A Mind node in the mirror room receives speech from a voice whose ID is `player`.
2. Player speech is passed to a Microsoft Agent Framework agent created from a replaceable provider Resource.
3. The Mind component constructs a hard-coded Alley persona and `speak` tool for the agent.
4. The OpenAI-compatible provider supplies the chat client used by the default Agent Framework adapter.
5. The provider loads `[AI]` Host, optional ApiKey, Model, and Timeout settings through the merged config API.
6. The provider can be switched between OpenAI chat-completions and responses client adapters in the editor.
7. The Mind ignores further `speak` tool calls and player voice input until the current reply turn completes.
8. Missing voice/backend configuration and backend failures are logged without crashing the scene.
9. Acceptance covers both player-visible conversation behaviour and the component/backend integration contract.

## References

### Implementation

- game/src/AI/Mind.cs
- game/src/AI/MindAgentProvider.cs
- game/src/AI/OpenAIMindAgentProvider.cs
- game/assets/testing/mirror_room/mirror_room.tscn
- game/AlleyCat.cfg

### Related Specs

- BODY-006: Voice Component
- SPCH-003: Transcriber Component
- SPCH-004: Speech Generator Component
- CORE-002: Configuration API

### External Dependencies

- Microsoft Agent Framework
