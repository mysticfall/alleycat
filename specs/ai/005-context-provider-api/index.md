# AI Context Provider API

## Requirement

AI-driven characters need rich contextual grounding beyond a name so they can behave like situated, persistent people.

## Goal

Define the high-level purpose, structure, and design concepts for an AI context provider abstraction that can support
early live-LLM playtesting now and connect to fuller identity, memory, lore, and scene backends later.

This is a high-level spec seed and agenda. It establishes the design direction for future work, not a final API
contract.

## Background

Character behaviour depends on more than the immediate user prompt. A believable AI character needs stable identity,
past experiences, current scene awareness, and access to relevant authored lore or backstory details.

Several source systems for that grounding are not final yet. Character memory, perception, lore retrieval, and other
backend services may evolve independently, but live LLM behaviour still needs to be playtested early. The context
provider abstraction lets simple providers supply useful context now while preserving a path to replace or extend those
providers with real backend integrations later.

## User Requirements

1. AI characters present as situated, persistent people rather than stateless names attached to prompts.
2. Character responses can reflect stable identity, accumulated memory, current scene context, and relevant lore.
3. Early playtests can exercise live LLM behaviour before final memory, perception, or retrieval backends exist.
4. Players are not exposed to provider wiring, retrieval mechanics, or prompt-placement details.

## Technical Requirements

1. Provide an AI context provider abstraction for retrieving contextual grounding from source or domain backends.
2. Keep providers presentation-neutral: providers describe or retrieve context, but do not decide prompt placement.
3. Separate retrieval from presentation so the same provider can feed different prompt channels when configured.
4. Treat placement as external orchestration or configuration owned outside individual providers.
5. Defer the initial API contract until result shape, request shape, implementation form, budgets, diagnostics, and
   backend boundaries are discussed.
6. Do not define a detailed context item taxonomy in this seed spec.

## High-Level Design Concepts

### Context Categories

Context providers may eventually cover several kinds of grounding. These categories describe intent, not a final data
model:

1. **Immutable Character Identity And Backstory**: stable facts such as name, role, personality anchors, origin, and
   authored backstory that should remain consistent across sessions.
2. **Incrementally Built Character Memory**: remembered events, interactions, preferences, relationships, and learned
   facts accumulated through play.
3. **Scene-Specific Or Current-Turn Information**: where the character is, who is present, what is happening now, and
   other immediate situational facts needed for the next response.
4. **Retrievable Lore Or Backstory Detail**: larger authored or generated knowledge that should be fetched only when
   relevant, such as world lore, faction history, or detailed personal history.
5. **Specialised Or Expensive Contextual Information**: context that is costly, rare, or domain-specific, such as deep
   perception summaries, inventory reasoning, relationship analysis, or planner state.

### Retrieval Axis

The retrieval axis answers: **where does this context come from?**

Context providers represent source or domain boundaries. A provider might read static authored identity, query a memory
store, summarise current scene state, retrieve lore, or compute a specialised contextual summary. The provider boundary
should make backend replacement possible without changing the high-level AI flow.

### Presentation Axis

The presentation axis answers: **where is this context placed for the AI?**

Presentation is controlled outside providers. External orchestration decides whether context is placed near the
beginning of the prompt, supplied only for the current turn, exposed through tools, omitted, summarised, or budgeted.

### Presentation Channels

1. **Pinned Context**: stable context placed near the beginning or system side of the prompt so it can be reused across
   turns and benefit from cache-friendly prompt structure.
2. **Transient Turn Context**: refreshed context for the current turn that informs the next response but is not retained
   as permanent conversation history.
3. **On-Demand Tool Context**: context retrieved only when the model or tool flow asks for it, suitable for large,
   specialised, uncertain, or expensive information.

The same provider may be used in more than one channel. For example, a character-identity provider could supply a
short pinned summary and a richer on-demand backstory detail if orchestration configures both uses.

## Design Decisions So Far

1. Providers are source or domain abstractions, not prompt-placement policies.
2. Providers remain presentation-neutral.
3. Prompt placement is external orchestration or configuration.
4. Retrieval and presentation are separate axes.
5. The same provider may be wired into pinned, transient, or on-demand channels.
6. No detailed context item taxonomy is defined yet.
7. The first concrete API contract is deferred until the open questions below are discussed.

## Open-Question Agenda

These topics guide future discussion before the first concrete API contract is written.

### Provider Result Shape

- Should providers return rendered text, structured data, or both?
- What structure is needed for ranking, diagnostics, budgeting, or later rendering?
- Which layer turns returned context into prompt-ready text?

### Provider Request Shape

- Should callers use free-text queries, typed request kinds, structured filters, or a combination?
- How should callers request identity summaries, current location, relevant memories, or lore details?
- Should retrieval-style requests remain natural-language based?
- Do perception or scene-state requests need typed contracts from the start?

### Provider Implementation Form

- Should providers be Godot resources, C# services, scene or node components, or a hybrid?
- How should authored static providers fit beside runtime providers?
- How should memory, perception, inventory, and lore providers access runtime state?
- What belongs in scene configuration versus global runtime services?

### Presentation Policy Boundary

- What minimal configuration is needed to wire providers to presentation channels?
- How can the same provider feed multiple channels without provider-owned placement logic?
- Which layer owns token budgets, priority, summarisation, and omission decisions?

### Context Category Priorities

- Which categories are required for the first playtestable AI flow?
- Which categories can remain mocked, simple, or unavailable until their backend systems mature?
- Which categories require explicit contracts with other subsystems?

### Diagnostics And Evaluation

- Should results include source metadata, relevance, confidence, or freshness values?
- What playtest logs are needed to diagnose context issues?
- How should tests distinguish bad retrieval, bad formatting, bad placement, and model behaviour?

## In Scope

- High-level purpose and rationale for an AI context provider abstraction.
- Context categories that future providers may support.
- Separation of retrieval and presentation concerns.
- Plain-language definitions of pinned, transient turn, and on-demand tool context.
- Design decisions already agreed for provider neutrality and external placement.
- Future discussion topics required before defining the first concrete API contract.

## Out Of Scope

- Final provider interface signatures, request objects, result objects, and data schemas.
- Final memory, perception, lore retrieval, inventory, relationship, or planner architectures.
- Specific vector database, RAG pipeline, summarisation system, or storage backend choices.
- Full prompt renderer implementation and final prompt template structure.
- Detailed context item taxonomy beyond the high-level categories in this seed spec.
- Replacing independent scene-context specifications, including current scene membership contracts.

## Acceptance Criteria

### User Requirements

1. The spec explains why AI characters need contextual grounding beyond a name.
2. The spec explains how context supports identity, memory, current scene awareness, and relevant lore.
3. The spec supports early live-LLM playtesting by allowing simple providers before final backends exist.
4. The spec keeps provider mechanics and prompt-placement details invisible to players.

### Technical Requirements

1. The spec defines providers as source or domain abstractions for retrieving context.
2. The spec keeps retrieval and presentation as separate axes.
3. The spec states that providers are presentation-neutral and placement is external orchestration or configuration.
4. The spec states that one provider may be used in pinned, transient turn, or on-demand tool channels.
5. The spec defers detailed API contracts and context item taxonomy until the open-question agenda is resolved.
6. Out-of-scope items defer implementation architectures without excluding the high-level concepts needed for future
   delivery.

## References

- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
