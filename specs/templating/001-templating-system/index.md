---
id: TMPL-001
title: Templating System
domain: TMPL
status: draft
---

# Templating System

## Requirement

Provide a reusable templating system that compiles string sources, renders them with simple key/value context, and
supports pluggable template tools.

## Goal

Enable gameplay, AI, and content systems to produce dynamic text without hard-coding every variation in C#.

## User Requirements

1. Content authors can write reusable template strings that substitute values from game-provided context.
2. Authored templates can use reusable partials to share common fragments.
3. Authored templates can use built-in helper tools for simple arithmetic, comparison, formatting, and repetition.
4. Content authors can use `eqOrdinal` for exact, case-sensitive comparisons without changing the case-insensitive
   behaviour of `eq`.
5. Developers can add project-specific template tools without changing the compiler implementation.
6. Developers can configure the compiler via Godot resources/nodes for partial loading and tool registration.
7. Content authors can render human-readable relative-time labels for timestamps through a built-in `ago` tool.

## Technical Requirements

1. The system must expose plain C# contracts for compiling a string into a reusable template and rendering it with
    `IReadOnlyDictionary<string, object?>` context.
2. Render context must remain a simple key/value dictionary provided directly by the caller.
3. The initial compiler implementation must use [Fluid](https://github.com/sebastienros/fluid) with Liquid template
   syntax. Partial templates must be registered by name and rendered through the Liquid `{% include 'name' %}` syntax.
4. Pluggable tools must be exposed to templates as invocable functions or filters taking positional arguments, with
   parser function-call support enabled, while custom tools register by name behind the retained plain C#
   `ITemplateTool` contract.
5. Built-in tools must include:
    - `add`: adds the first two integer-like arguments and renders the sum.
    - `eq`: compares the first two arguments with ordinal, case-insensitive string equality and renders `true` only when
      they match.
    - `eqOrdinal`: converts the first two arguments to strings using invariant culture, compares them with
      `StringComparison.Ordinal` case-sensitive equality, and renders `true` only when equal and empty otherwise. Fewer
      than two arguments render empty.
    - `nf`: formats a numeric argument as fixed-point text using current culture, default precision `3`, and precision
      clamped to `0..99`.
    - `repeat`: renders the first argument repeated by the integer count in the second argument.
    - `ago`: renders the first argument, a `DateTimeOffset`, `DateTime`, or ISO-8601/round-trip string timestamp, as a
      relative-time phrase. The optional second argument is the reference timestamp, defaulting to UTC now at render
      time; the optional third argument is the just-now threshold in seconds, default `5`. Elapsed below the threshold
      renders `just now`; otherwise the largest whole unit, floored, renders `N second(s) ago`, `N minute(s) ago`,
      `N hour(s) ago`, `N day(s) ago`, or `N week(s) ago` with correct singular/plural forms. Future timestamps render
      `just now`; null or unparseable timestamps render empty.
6. The implementation must not depend on the archived Language-Ext effect/map style or its Godot `ResourceFactory`
    service construction pattern.
7. The Fluid compiler implementation must be available as a Godot-authored `Resource` or `Node` and registered globally
    as `ITemplateCompiler` during game startup via the global service resolution system.
8. The compiler must load partial templates from a configured Godot path/directory, using file names (without extension)
    as partial names, in a deterministic manner.
9. Pluggable tools must be configurable through Godot-authored `Resource` or `Node` authoring while retaining the plain
    C# `ITemplateTool` contract for tool implementation.
10. Rendering must be asynchronous — equivalent to a `ValueTask<string> RenderAsync(IReadOnlyDictionary<string,
    object?>)` contract — while template compilation remains synchronous.
11. Context values that are plain C# objects must resolve members through permissive (unsafe) member access, except
    where the value's type carries curated template registrations under Technical Requirement 14; dictionary values
    must be adapted when the template context is constructed. Rendered output must not be HTML-encoded.
12. Conditional evaluation must follow Liquid truthiness: only nil and false are falsy, so empty strings and collections
    are truthy — unlike engines that treat empty values as false.
13. Template sources must originate from authored content supplied by callers or loaded from configured paths; the
    system must not generate template syntax at runtime.
14. The Fluid compiler engine must enforce a curated member-access policy for annotated interfaces, applying to every
    template the engine renders:
    - Interface properties marked `TemplateExposedAttribute` are curated members readable by templates, registered
      against their declaring interface so every concrete implementer resolves them through Fluid's interface walk
      without per-type registration. `IIdentifiable.FullId` is curated; `Id` and `Type` deliberately are not.
    - Interfaces marked `TemplateSealedAttribute` expose exactly their curated members; every other member name on an
      implementer renders nil. `ICharacter` is sealed, so live component state — such as voice configuration — and
      framework surfaces, including the Godot `Node` surface and `IComponentHolder.Components`, are unreachable from
      any template, including visual-cue description contexts (`observer`/`subject`), not only AI prompt contexts.
    - Curated registrations and seals are discovered once per process through reflection over the game assembly into
      a thread-safe, owned strategy instance shared by every compiler engine; the strategy must never mutate Fluid's
      shared unsafe member-access singleton.
    - Curating the same member name on more than one interface must fail loudly at discovery (the single-level rule),
      because Fluid enumerates a type's interfaces in unspecified order and duplicate names would resolve ambiguously.

## In Scope

- Plain C# template, compiler, render-context, and tool contracts.
- Fluid-based (Liquid syntax) template compilation and rendering.
- Curated member-access policy for annotated interfaces: curated template members, sealed template surfaces, and the
  owned strategy instance.
- Programmatic partial registration.
- Programmatic custom tool registration.
- Built-in `add`, `eq`, `eqOrdinal`, `nf`, and `repeat` tools.
- Built-in `ago` relative-time tool.
- Asynchronous rendering with synchronous compilation.
- Unit tests covering the public contracts and built-in behaviours.
- Godot-authored configuration of the template compiler service (as Resource or Node) for global service registration.
- Loading partials from a configured Godot path/directory using filenames (without extension) as names.
- Configuring pluggable tools via Godot resources/nodes while retaining the plain C# `ITemplateTool` contract.

## Out Of Scope

- Localisation workflow integration.
- Caching policies, profiling, or performance budgets.
- Advanced template inheritance beyond Liquid includes.
- Curating or sealing further interfaces or members beyond the currently annotated `IIdentifiable.FullId` and
  `ICharacter`.

## Acceptance Criteria

1. A template such as `Hello {{name}}` compiles once and renders with supplied context values.
2. Registered partials render through Liquid `{% include 'name' %}` syntax.
3. A custom registered tool can be invoked from a template — as a function call or filter with positional arguments —
   without modifying the compiler.
4. The built-in `add`, `eq`, `eqOrdinal`, `nf`, and `repeat` tools produce the behaviours defined in Technical
   Requirement 5.
5. `eqOrdinal` uses invariant string conversion and `StringComparison.Ordinal`: equal values render `true`, while case
   mismatches and other unequal values render empty. Calls with fewer than two arguments also render empty.
6. Existing `eq` comparisons remain ordinal and case-insensitive.
7. Templates render from caller-supplied key/value dictionaries without requiring renderable-object APIs.
8. Unit tests verify the compiler, rendering, partials, custom tools, and built-in tools.
9. The implementation uses plain C# contracts and contains no dependency on Language-Ext or the archived
   `ResourceFactory` pattern.
10. The Fluid-based compiler is registered globally as `ITemplateCompiler` via global service resolution.
11. The compiler loads partials from a configured Godot path using filenames (no extension) as names.
12. Pluggable tools are configurable via Godot resources/nodes while retaining the plain C# `ITemplateTool` contract.
13. The built-in `ago` tool renders the defined phrases for representative elapsed durations, including the just-now
    threshold boundary and singular forms, with a deterministic explicit reference timestamp, and renders empty for
    null or unparseable input.
14. Rendering is asynchronous — equivalent to a `ValueTask<string> RenderAsync(IReadOnlyDictionary<string, object?>)`
    contract — while compilation remains synchronous.
15. No component of the system generates template syntax at runtime; every compiled template originates from authored
    content supplied by callers or loaded from configured paths.
16. All six built-in tools — `add`, `eq`, `eqOrdinal`, `nf`, `repeat`, and `ago` — satisfy their Technical Requirement 5
    definitions with no semantic drift.
17. The templating implementation introduces no Handlebars.Net package or assembly dependency.
18. Plain C# context values without curated registrations resolve through permissive member access, dictionary values
    adapt at template-context construction, conditionals treat only nil and false as falsy, and rendered output
    receives no HTML encoding.
19. The curated member-access policy satisfies its Technical Requirement 14 contract: curated members resolve on every
    concrete implementer through interface composition, sealed interfaces render every non-curated member nil
    independently of Fluid's interface enumeration order, curating the same member name on two interfaces fails
    loudly at discovery, and types without curated registrations keep permissive member access with deterministic
    rendering across repeat engine instantiation.
20. Rendering a real authored scene character through the running engine exposes exactly the canonical `FullId`, while
    `Id`, `Components`, and the Godot `Name` render empty.

## References

- [Global Service Resolution](../../core/004-global-service-resolution/index.md)
- [Component/Trait System](../../core/003-component-system/index.md)
- [Configuration API](../../core/002-configuration-api/index.md)
- `game/src/Templating/`
- `tests/src/Templating/TemplatingTests.cs`
- `tests/src/Templating/CuratedTemplateMemberAccessStrategyTests.cs`
