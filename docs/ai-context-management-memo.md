# AI Context Management Memo

## Purpose

This is a temporary design memo for future agents. It preserves brainstorming outcomes about AI context management without requiring access to the original chat log.

This memo is not a final specification and does not define a binding API contract. Authoritative implementation requirements must still be captured in specs before delivery work proceeds.

## Consumers

- `planner`: use this memo to seed future AI context-management planning.
- `coder`: use this memo only as design background until a spec defines implementation contracts.
- `reviewer`: use this memo to check whether future specs preserve the agreed conceptual boundaries.

## Related Specifications And Boundaries

- `specs/context/001-contextual-information-api/index.md` (`CTX-001`) defines the general `AlleyCat.Context` contextual-information API.
- CTX-001 provides `IContextual`, `ContextData` titled fragments, scene-aware requests through `ISceneContext`, and an optional observing `ICharacter`.
- CTX-001 intentionally excludes prompt placement, rendering, ranking, summarisation, AI retrieval, memory, lore, perception backends, and detailed context taxonomy.
- `specs/ai/005-context-provider-api/index.md` was discussed as a seed direction for presentation-neutral context providers, but no final API contract is established by this memo.
- Retrieval and presentation should remain separate axes. Presentation channels discussed include pinned context, transient turn context, and on-demand tool context.

## Agreed Direction

1. Do not treat Microsoft Agent Framework `AgentThread` as a character's authoritative memory.
2. Use the thread as one input or projection for LLM invocation context, not as the source of truth for game state, character memory, lore, or perception.
3. Keep these conceptual layers separate:
   - authoritative game and domain state;
   - durable conversational thread;
   - per-turn context frame;
   - audit and debug log.
4. Pinned context is not rebuilt every turn. It remains in the thread indefinitely unless explicitly replaced or removed.
5. Future chat reduction must preserve pinned items while culling old, stale, or unnecessary material.
6. Scene transient context should be a per-turn overlay and should be removable by the reducer, likely through an `IChatReducer`-style component.
7. Lore and memory snippets should generally be retrieved dynamically into a transient context frame, not persisted as normal thread messages.
8. Repeated retrieval should be handled through caching, freshness checks, or source versioning rather than by polluting chat history.
9. Tool invocation history should be retained only as needed within the same invocation or tool loop and in audit logs.
10. After a turn, meaningful tool effects should be converted into domain observations or self-observations. For example, a speak tool result becomes an event that the character said something.
11. Raw Agent Framework or OpenAI function call/result protocol should not be the primary long-term memory.

## Recommended Message Representation

- Use dedicated messages for events where temporal ordering matters:
  - player speech observations;
  - salient scene observations;
  - self-observations, including recent character speech or actions.
- A single observation event should usually be represented as a dedicated user-role, context-role, or event-style `ChatMessage`, rather than only one line inside a regenerated mega-message.
- Preserve temporal ordering for meaningful events because ordered recent history is the main reason the thread remains useful.
- Use batched or generated context blocks for current snapshots and retrieved overlays, such as:
  - current room participants;
  - current local scene state;
  - retrieved lore snippets;
  - retrieved memory snippets.
- Treat current snapshots and retrieved snippets as transient context blocks unless a later spec defines a reason to promote them into durable observations.

## Recommended Reducer Policy

The future reducer should:

1. Preserve pinned messages.
2. Preserve recent high-value discrete observations.
3. Compact older observations into summaries when they remain relevant.
4. Remove transient scene and retrieval messages after they are no longer current.
5. Remove stale raw tool protocol after useful effects have been converted into domain observations, self-observations, summaries, or audit records.
6. Keep the thread useful as an ordered short-to-medium-term conversational and event substrate, including:
   - pinned identity and instructions;
   - recent player utterances;
   - recent character speech and actions;
   - salient observations;
   - compacted summaries;
   - current in-progress tool protocol.

## Metadata And Attribution Direction

Future implementation should attach metadata or attribution to messages where the selected SDK allows it. Useful fields may include:

- `context_kind`: for example `pinned`, `observation`, `scene_transient`, `retrieved`, `self_observation`, or `summary`.
- `reducer_policy`: whether to preserve, compact, expire, or drop.
- `source`: the originating system, provider, sensor, tool, or domain object.
- `salience`: relative importance for reducer decisions.
- `freshness` or source version: whether the rendered context remains current.

These names are provisional and should not be treated as a final schema.

## Microsoft Agent Framework Findings

High-level documentation findings from the discussion:

- Agent Framework history providers store `ChatMessage` sequences.
- `InMemoryChatHistoryProvider` stores messages in `AgentSession.StateBag`.
- `ChatHistoryProvider` supplies prior messages for the current invocation.
- Documented patterns show messages carrying attribution or additional properties.
- Tool calls and results appear in history examples as assistant `functionCall` messages and tool `functionResult` messages.

These findings support the direction above but do not settle the final role mapping, metadata preservation strategy, or reducer implementation.

## Remaining Open Questions

1. How should observations map to Microsoft Agent Framework and OpenAI SDK roles and APIs?
2. Should observations be represented as user messages, developer/system messages, or source-attributed context messages?
3. How much metadata will the selected SDK preserve across history providers, model calls, reducers, and tool loops?
4. What exact reducer contract should be implemented, and should it use `IChatReducer` directly or a project-owned abstraction?
5. Which context kinds, reducer policies, and salience/freshness fields should become normative once AI-005 or a successor spec is written?

## Spec Follow-Up

Before implementation, convert the relevant decisions into an authoritative spec that separates:

- user-visible AI behaviour and outcomes;
- technical contracts for message construction, context providers, retrieval boundaries, reducer behaviour, metadata, validation, and integration with Microsoft Agent Framework.
