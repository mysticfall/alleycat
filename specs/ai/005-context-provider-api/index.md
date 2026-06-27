# AI Context Provider API Agenda

## Purpose

Capture brainstorming decisions and open questions before drafting the first AI Context Provider API spec.

This is a spec-preparation note, not the final API contract.

## Immediate Goal

Discuss the open questions in separate focused sessions before deciding the initial API contract and drafting the spec.

## User-Facing Goals And Outcomes

- AI characters should receive relevant identity, memory, scene, and lore context without exposing retrieval details to
  players.
- Prompt context should stay coherent across pinned, transient turn, and on-demand tool channels.
- The first API should support playtesting with simple providers before final memory, perception, or RAG systems exist.

## Technical And API Concerns

- Retrieval and presentation are separate axes.
- Context providers are source/domain abstractions.
- Presentation placement is external orchestration or configuration.
- The same provider may be used for pinned, transient turn, or on-demand tool channels.
- Providers should be presentation-neutral and should not own placement decisions.
- No detailed context item concept should be included yet.
- Final API contract scope is deferred until open-question discussions conclude.

## Open-Question Agenda

### Provider Result Shape

- Should providers return rendered text, structured data, or both?
- Should the first playtest API be text-first?
- What structure, if any, must be preserved for later ranking, diagnostics, or rendering?
- Which layer is responsible for formatting returned context into prompt-ready text?

### Provider Request Shape

- Should callers use free-text queries, typed request kinds, structured filters, or a combination?
- How should callers request current location, relevant memories, or an identity summary?
- Should RAG-style requests remain natural-language based?
- Do scene and perception requests need typed contracts from the start?

### Provider Implementation Form

- Should providers be Godot Resources, C# services, scene/node components, or a hybrid?
- How should authored/static context providers fit beside runtime providers?
- How should memory, perception, and inventory providers access runtime state?
- What belongs in scene configuration versus global runtime services?
- How should runtime service access be exposed without coupling providers to final backend architecture?

### Presentation Policy Boundary

- Should the initial spec only define the conceptual boundary?
- Does it need minimal configuration requirements for wiring providers to presentation channels?
- How can the same provider be wired into multiple channels without provider-owned placement logic?

### Pinned, Turn, And Tool Channels

- Are pinned, transient turn, and on-demand tool channels sufficient for the initial API?
- Should pinned context live in the system prompt, initial messages, or developer messages?
- Should transient turn context be assembled into one message?
- Should tool-returned context remain ephemeral, or can it feed memory later?

### Backend Independence

- What minimum abstraction supports playtesting before memory, RAG, or perception systems are complete?
- How should dummy/static providers be represented?
- Which choices avoid a dead-end architecture when real backends arrive?

### Context Categories

- Which categories are mandatory for the first API, and which are future work?
- Candidate categories:
  - Immutable identity.
  - Compact backstory.
  - Detailed lore or backstory retrieval.
  - Memory.
  - Current scene or location.
  - Visible entities.
  - Inventory or items.
  - Relationship state.
  - Current goals or intentions.
- Should compact identity be split from retrievable biography?

### Token Budget And Prioritisation

- Should requests include a token or character budget?
- Should providers trim and rank their own results?
- Should callers or assemblers assign priority across providers?
- How should multiple provider results be combined under limited prompt space?

### Diagnostics And Evaluation

- Should results include source metadata?
- Should results include relevance or confidence values?
- What playtest logs are needed to diagnose context issues?
- How should tests distinguish bad retrieval, bad formatting, and model behaviour?

### Final Spec Scope Boundary

- Which proposed exclusions are safe to keep out of the first spec?
- Do any excluded areas need a minimal placeholder contract to avoid blocking implementation?

## Proposed Exclusions

- Detailed context item taxonomy.
- Provider-owned placement decisions.
- Final memory architecture.
- Final perception architecture.
- Final autonomous goal or planning system.
- Specific vector DB or RAG implementation.
- Full prompt rendering implementation.

## Preparation Checks

- Requirement layers: separate user-facing outcomes from technical/API concerns in the future spec.
- Out-of-scope audit: exclusions must not remove minimum API contracts needed for playtesting.
- Acceptance traceability: the future spec should verify both player-facing context quality and API boundaries.
