# AI Context Provider API Agenda

## Requirement

Capture the remaining decisions for an AI-specific context provider API without redefining independent scene character
membership, which is specified by [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md).

## Goal

Prepare a future AI context provider contract for identity, memory, lore, sensory, and prompt-facing context while using
SCN-001 as the normative dependency for current scene characters.

## User Requirements

1. AI characters receive relevant identity, memory, scene, and lore context without exposing retrieval details to
   players.
2. Prompt context stays coherent across pinned, transient turn, and on-demand tool channels.
3. Early playtests can use simple providers before final memory, perception, or RAG systems exist.
4. Scene character membership remains consistent with non-AI systems through SCN-001.

## Technical Requirements

1. AI context providers are source or domain abstractions, not scene membership providers.
2. Scene character membership must be consumed through SCN-001 rather than an `AlleyCat.AI` scene-context API.
3. Retrieval and presentation are separate axes.
4. Presentation placement is external orchestration or configuration.
5. The same provider may be used for pinned, transient turn, or on-demand tool channels.
6. Providers should be presentation-neutral and should not own placement decisions.
7. The future API must decide provider result shape, request shape, implementation form, token budgets, diagnostics, and
   backend independence before becoming a final contract.
8. No detailed context item taxonomy is included in this preparation note.

## In Scope

- Open questions for AI-specific provider result and request shapes.
- Provider implementation options, including Godot Resources, C# services, scene nodes, or hybrids.
- Presentation policy boundaries for pinned, transient turn, and on-demand tool channels.
- Context categories beyond SCN-001 character membership, such as memory, lore, sensory state, and relationships.
- Token budget, prioritisation, diagnostics, and evaluation questions.
- Dependency on SCN-001 for current scene character membership.

## Out Of Scope

- Defining `ISceneContextProvider`, `ISceneContext`, or `Actors` group discovery; these live in SCN-001.
- Detailed context item taxonomy.
- Provider-owned placement decisions.
- Final memory architecture.
- Final perception architecture.
- Final autonomous goal or planning system.
- Specific vector DB or RAG implementation.
- Full prompt rendering implementation.

## Open-Question Agenda

### Provider Result Shape

- Should providers return rendered text, structured data, or both?
- What structure must be preserved for later ranking, diagnostics, or rendering?
- Which layer formats returned context into prompt-ready text?

### Provider Request Shape

- Should callers use free-text queries, typed request kinds, structured filters, or a combination?
- How should callers request current location, relevant memories, or an identity summary?
- Should RAG-style requests remain natural-language based?
- Do perception requests need typed contracts from the start, distinct from SCN-001 scene membership?

### Provider Implementation Form

- Should providers be Godot Resources, C# services, scene/node components, or a hybrid?
- How should authored/static context providers fit beside runtime providers?
- How should memory, perception, and inventory providers access runtime state?
- What belongs in scene configuration versus global runtime services?

### Presentation Policy Boundary

- Does the first AI spec need minimal configuration for wiring providers to presentation channels?
- How can the same provider be wired into multiple channels without provider-owned placement logic?

### Context Categories

- Which categories are mandatory for the first AI API, and which are future work?
- Candidate categories include identity, backstory, lore retrieval, memory, perception, inventory, relationship state,
  goals, and scene information beyond SCN-001 character membership.

### Diagnostics And Evaluation

- Should results include source metadata, relevance, or confidence values?
- What playtest logs are needed to diagnose context issues?
- How should tests distinguish bad retrieval, bad formatting, and model behaviour?

## Acceptance Criteria

### User Requirements

1. The future AI API agenda preserves context goals for identity, memory, lore, sensory, and scene-aware behaviour.
2. Playtest-friendly provider decisions remain explicit open questions rather than implicit implementation guesses.
3. Scene character membership is discoverable through the SCN-001 dependency.

### Technical Requirements

1. The agenda does not define scene-context interfaces, `Actors` scanning, or `AlleyCat.Scene` implementation details.
2. SCN-001 is the only normative source for current scene character membership.
3. Remaining AI provider decisions cover result shape, request shape, implementation form, presentation boundary,
   budget, diagnostics, and backend independence.
4. Out-of-scope items do not exclude the future minimum API contracts needed for AI playtesting.

## References

- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
