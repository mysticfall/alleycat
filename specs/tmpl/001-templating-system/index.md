---
id: TMPL-001
title: Templating System
domain: TMPL
status: draft
---

# TMPL-001: Templating System

## Requirement

Provide a reusable templating system that compiles string sources, renders them with simple key/value context, and
supports pluggable Handlebars tools.

## Goal

Enable gameplay, AI, and content systems to produce dynamic text without hard-coding every variation in C#.

## User Requirements

1. Content authors can write reusable template strings that substitute values from game-provided context.
2. Authored templates can use reusable partials to share common fragments.
3. Authored templates can use built-in helper tools for simple arithmetic, comparison, formatting, and repetition.
4. Developers can add project-specific Handlebars tools without changing the compiler implementation.
5. Developers can configure the compiler via Godot resources/nodes for partial loading and tool registration.

## Technical Requirements

1. The system must expose plain C# contracts for compiling a string into a reusable template and rendering it with
    `IReadOnlyDictionary<string, object?>` context.
2. Render context must remain a simple key/value dictionary provided directly by the caller.
3. The initial compiler implementation must use Handlebars.Net and support registering partial templates by name.
4. The Handlebars implementation must expose a pluggable tool contract that registers custom helpers by name.
5. Built-in tools must include:
    - `add`: adds the first two integer-like arguments and renders the sum.
    - `eq`: compares the first two arguments with ordinal, case-insensitive string equality and renders `true` only when
      they match.
    - `nf`: formats a numeric argument as fixed-point text using current culture, default precision `3`, and precision
      clamped to `0..99`.
    - `repeat`: renders the first argument repeated by the integer count in the second argument.
6. The implementation must not depend on the archived Language-Ext effect/map style or its Godot `ResourceFactory`
    service construction pattern.
7. The Handlebars compiler implementation must be available as a Godot-authored `Resource` or `Node` and registered
   globally as `ITemplateCompiler` during game startup via the global service resolution system.
8. The compiler must load partial templates from a configured Godot path/directory, using file names (without extension)
   as partial names, in a deterministic manner.
9. Pluggable tools must be configurable through Godot-authored `Resource` or `Node` authoring while retaining the plain
   C# `ITemplateTool` contract for tool implementation.

## In Scope

- Plain C# template, compiler, render-context, and tool contracts.
- Handlebars.Net-backed template compilation and rendering.
- Programmatic partial registration.
- Programmatic custom tool registration.
- Built-in `add`, `eq`, `nf`, and `repeat` tools.
- Unit tests covering the public contracts and built-in behaviours.
- Godot-authored configuration of the template compiler service (as Resource or Node) for global service registration.
- Loading partials from a configured Godot path/directory using filenames (without extension) as names.
- Configuring pluggable tools via Godot resources/nodes while retaining the plain C# `ITemplateTool` contract.

## Out Of Scope

- Localisation workflow integration.
- Asynchronous compilation, caching policies, profiling, or performance budgets.
- Advanced template inheritance beyond Handlebars partials.

## Acceptance Criteria

1. A template such as `Hello {{name}}` compiles once and renders with supplied context values.
2. Registered partials render through Handlebars partial syntax.
3. A custom registered tool can be invoked from a template without modifying the compiler.
4. The built-in `add`, `eq`, `nf`, and `repeat` tools produce the behaviours defined in Technical Requirement 5.
5. Templates render from caller-supplied key/value dictionaries without requiring renderable-object APIs.
6. Unit tests verify the compiler, rendering, partials, custom tools, and built-in tools.
7. The implementation uses plain C# contracts and contains no dependency on Language-Ext or the archived
    `ResourceFactory` pattern.
8. Handlebars compiler registered globally as `ITemplateCompiler` via global service resolution.
9. Compiler loads partials from configured Godot path using filenames (no extension) as names.
10. Pluggable tools configurable via Godot resources/nodes retaining plain C# `ITemplateTool` contract.

## References

- [Global Service Resolution](../../core/004-global-service-resolution/index.md)
- [Component/Trait System](../../core/003-component-system/index.md)
- [Configuration API](../../core/002-configuration-api/index.md)
- `game/src/Templating/`
- `tests/src/Templating/TemplatingTests.cs`
