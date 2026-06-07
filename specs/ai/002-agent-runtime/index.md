---
id: AI-002
title: Agent Runtime
---

# Agent Runtime

## Requirement

Refactor Mind into an observation-driven runtime slice: observation contracts, async batching loop with weighted
triggering, dynamic Agent Framework tool resources, and AgenticMind-owned invocation context for in-world actions.

## Goal

Establish Agent Runtime with observation-driven batching, event-driven consciousness scheduling, and extensible tool
invocation context.

## User Requirements

1. Players experience responsive NPC interactions: speech as sensory input, not direct agent triggers.
2. NPCs maintain immediate responsiveness to player input while leaving room for future background context processing.
3. The system preserves existing mirror-room behaviour: player voice -> NPC hears -> NPC speaks through voice component.
4. Agents can process observations asynchronously without blocking primary interaction flows.
5. Observation significance influences when the consciousness loop runs, reducing unnecessary processing.

## Technical Requirements

1. Define an abstract `Observation` contract under `AlleyCat.AI.Observation`:
    - Observations encapsulate sensory data (e.g., voice heard -> speech observation with speaker ID and content).
2. Implement the main interaction track for the agent workflow:
    - Main interaction track handles immediate observations for low-latency response.
    - Guidance/support track remains a future dual-track design concern; do not add context/guidance placeholder APIs
      until its final shape is specified.
    - **Passive sensory inputs produce observations as events arrive** (e.g., voice listener emits voice observation).
3. Implement asynchronous consciousness loop in the abstract Mind base:
    - Loop runs when observation weight threshold is reached or when queued observations reach a configurable maximum
      wait.
    - Each observation has a weight; cumulative weight triggers immediate loop execution.
    - Loop does not run on every tick without notable observations.
    - Disabling Mind stops timer scheduling immediately and preserves pending observations for later re-enable.
    - **The sensory processing model maps into observation creation, cumulative observation weight calculation, and
      loop triggering**.
4. Preserve mirror-room behaviour migration:
    - Player speech becomes a voice/speech observation processed by the agent.
    - Speaking is exposed as an Agent Framework `speak` tool.
    - Visible mirror-room behaviour (player speech -> NPC speech) is maintained.
5. Define concrete tool invocation context:
    - AI tools are authored as top-level Godot `Resource` classes under `AlleyCat.AI.Tool` so scenes can configure
      the current tool set without nested serialisation types.
    - AgenticMind exports the active tool resources and supplies them through per-turn Agent Framework chat options
      instead of registering static tools on agent construction.
    - Agent Framework tools receive AgenticMind itself as the `IServiceProvider` execution context for the turn.
    - The initial `speak` tool queries `IVoice` from that context and uses the configured voice component.
    - Future action tools must be creatable without adding a central action-layer switch.
6. Maintain compatibility with existing Mind component contracts where applicable:
    - Voice-listener contract adapted to produce voice observations.
    - NPC voice output invoked by the `speak` tool through invocation services.
    - Provider resources remain limited to creating `IChatClient` instances.

## In Scope

- Observation abstract data contract.
- Main interaction workflow, with dual-track guidance/support noted only as a future contract.
- Mind-owned asynchronous consciousness loop with observation-weighted triggering.
- Migration path from AI-001: player speech → observation, speaking → Agent Framework tool.
- Dynamic Godot Resource tool API, including the concrete `speak` tool invocation context using `IServiceProvider`.
- Configuration contracts for maximum observation wait and observation weight threshold.
- Preservation of mirror-room test scene behaviour.
- Updated AI-001 AgenticMind component to emit observations, execute Agent Framework turns, and own session state;
  configured client providers only supply `IChatClient` instances.

## Out Of Scope

- Full context providers (scene info, memory, sensory data beyond voice observation).
- Persistent memory or long-term relationship storage.
- Final multi-agent orchestration beyond dual-track contract.
- Complete action catalogue beyond the initial `speak` tool.
- Final budget/scheduler implementation (configurable contracts only).
- Guidance/support APIs, context providers, and guidance agent implementation.
- Tuning constants for observation weights, maximum waits, or thresholds beyond sensible defaults.
- AI resource prioritisation/queueing beyond basic loop triggering.

## Acceptance Criteria

1. User Requirements are verified by:
    - Realtime Interaction: NPC responds to player speech with low perceived latency.
    - Non-Dialogue Interaction: System designed to handle sensory observations beyond speech (contract level).
    - Context: Future guidance/support work is not blocked by malformed placeholder APIs.
    - Performance: Consciousness loop does not run continuously without observation weight threshold.
    - Extensibility: observation contracts and invocation-time tool services support future extensions.
2. Technical Requirements are verified by:
    - Observation contract defined with sensory data encapsulation.
    - Main interaction workflow established, without context/guidance placeholder APIs.
    - Mind-owned asynchronous consciousness loop contract with configurable maximum wait and weight-based triggering.
    - Agent Framework `speak` tool receives Mind execution services through `IServiceProvider` at invocation time.
    - The `IServiceProvider` execution context is the calling AgenticMind and resolves the current turn's `IVoice`.
    - Tool resources are selected through per-turn `ChatOptions`, so changes to `AgenticMind` tool configuration apply
      to the next agent turn without rebuilding the agent/session.
    - Mirror-room behaviour preserved via observation/tool migration.
    - AI-001 AgenticMind component emits voice observations, owns Agent Framework session state, executes Agent
      Framework turns, and uses its client provider only to obtain an `IChatClient`.
    - **Passive sensory processing verified: voice input converts to speech observation upon event arrival**.
    - **Weighted triggering verified: observation weight accumulation triggers immediate loop execution when threshold
      reached**.
    - **Maximum-wait triggering verified: queued observations below threshold process after the configured maximum
      wait**.
    - **Disabled-loop behaviour verified: timer processing pauses while disabled and resumes after re-enable**.
    - **Tool invocation verified: `speak` tool routes through invocation services to voice output**.
3. Acceptance covers behavioural and technical contracts.

## References

### Implementation

- game/src/AI/Observation/Observation.cs
- game/src/AI/Tool/AgentTool.cs
- game/src/AI/Tool/SpeechTool.cs
- game/src/AI/Mind.cs (abstract base)
- game/src/AI/AgenticMind.cs
- game/src/AI/Provider/ClientProvider.cs
- game/assets/testing/mirror_room/mirror_room.tscn

### Related Specs

- AI-001: Mind Component
- BODY-006: Voice Component
- SPCH-003: Transcriber Component
- SPCH-004: Speech Generator Component
- CORE-002: Configuration API

### External Dependencies

- Microsoft Agent Framework
