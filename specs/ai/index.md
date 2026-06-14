---
id: AI
title: AI System
---

# AI System

## Requirement

The AI system must support believable, real-time, non-dialogue driven
character interaction that is grounded in lore and context, while meeting
performance and extensibility goals.

## Goal

AlleyCat should feature AI-driven characters that perceive, remember, and
act in the world based on their lore and current context, enabling deep
roleplay without breaking immersion or performance.

## User Requirements

1. Realtime Interaction: AI characters must act autonomously without
   waiting for player input, be aware of time passage, time out unanswered
   prompts, and choose appropriate actions.
2. Non-Dialogue Interaction: AI characters must sense non-verbal inputs
   (e.g., player pose, proximity) and enact outputs in the world (e.g.,
   discipline, gestures) rather than representing them as chat emotes.
3. Lore and Backstory: Developers must be able to author complex settings
   easily; AI must retrieve relevant information by conceptual relationships
   (not only tags/keywords) with efficiency for latency and token count;
   authored content must be able to evolve as in-game events unfold.
4. Context: AI characters must be able to access scene information, short-
   and long-term memory, environmental and self sensory information
   (including emotion, pain, pleasure), and spatial relationships.
5. Performance: The system must provide low latency for real-time interaction,
   support parallel/multi-agent workflows, implement an AI budget with
   prioritisation/queueing/configurable settings, allow less important
   characters to use cheaper/slower/non-AI control, and default configurations
   must support multiple AI characters without backend spam or overloaded queues.
6. Extensibility: The system must support diverse roleplay scenarios beyond
   the base game, treat lore/backstory as external/sample data (not embedded
   in scenes/code), and make actions and context providers configurable/pluggable
   (e.g., as resources in the editor).

## Technical Requirements

1. Event/Time-Driven Agent Loop: Provide a mechanism for AI agents to act
   based on time passage and events, including timeout handling for unanswered
   prompts.
2. Sensing/Action Integration: Define contracts for sensing non-verbal inputs
   and enacting world-based outputs (not limited to dialogue).
3. Knowledge/Memory Model: Specify a model for storing and retrieving lore/backstory
   by conceptual relationships, optimised for latency and token count, with
   support for evolving content.
4. Context Provider Interface: Define an interface for AI agents to access
   scene info, memory, sensory data, and spatial relationships.
5. Budget/Scheduler: Establish a system for AI resource allocation with
   prioritisation, queueing, and configurable settings, allowing fallback to
   cheaper/slower/non-AI control for less important characters.
6. Parallel Workflow Expectation: Design for parallel/multi-agent workflows
   where appropriate, not strictly sequential.
7. Extensibility Contracts: Ensure lore/backstory can be loaded as external
   data, and actions/context providers can be plugged in via editor-configurable
   resources.

## In Scope

- Parent index for AI character behaviour specifications.
- Current entries for AI-001: Mind Component, AI-002: Agent Runtime, AI-003: Prompt API, and AI-004: Lore And
  Backstory Source Compilation.
- High-level contracts covering the six requirement themes above.
- Extensibility points for lore, actions, and context providers.

## Out Of Scope

- Exact model or provider choices for language or reasoning.
- Tuning constants for timeouts, budgets, or sensory thresholds.
- Final lore/backstory content or scene-specific assets.
- Detailed contracts for listed child components (these live in respective child specs).

## Acceptance Criteria

1. User Requirements are verified by:
   - Realtime Interaction: AI agents act without player input and handle timeouts.
   - Non-Dialogue Interaction: Outputs manifest as world changes, not chat.
   - Lore and Backstory: Retrieval uses conceptual relationships and remains efficient and updatable.
   - Context: Agents access scene, memory, sensory, spatial data.
   - Performance: System maintains low latency with multiple agents, supports priority queues, budget defaults, and
     cheaper fallback control.
   - Extensibility: New lore, actions, and context providers can be added externally without scene/code changes.
2. Technical Requirements are verified by:
   - Existence of event/time-driven loop contracts.
   - Sensing/action integration interfaces defined.
   - Knowledge/memory model specified.
   - Context provider interface defined.
   - Budget/scheduler mechanism described.
   - Parallel workflow expectation stated.
   - Extensibility contracts for external data and pluggable components.
3. AI-001: Mind Component, AI-002: Agent Runtime, AI-003: Prompt API, and AI-004: Lore And Backstory Source
   Compilation are identified as current normative child contracts for their respective scopes.

## Specifications

- [AI-001: Mind Component](001-mind/index.md)
- [AI-002: Agent Runtime](002-agent-runtime/index.md)
- [AI-003: Prompt API](003-prompt-api/index.md)
- [AI-004: Lore And Backstory Source Compilation](004-lore-backstory/index.md)

## References

- AI-001: Mind Component
- AI-002: Agent Runtime
- AI-003: Prompt API
- AI-004: Lore And Backstory Source Compilation
