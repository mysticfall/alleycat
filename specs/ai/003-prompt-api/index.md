---
id: AI-003
title: Prompt API
domain: AI
status: draft
---

# AI-003: Prompt API

## Requirement

Provide an authorable prompt-composition API that assembles named prompt sections and compiles the result through the
existing templating system.

## Goal

Let AI prompts be built from inline and file-backed Godot resources without coupling prompt authorship to a final prompt
syntax decision.

## User Requirements

1. Content authors can compose a prompt from ordered, named sections in the Godot editor.
2. Content authors can provide section content either inline or from a Godot resource path such as
   `res://prompts/my_prompt.md`.
3. Developers can compile an authored prompt stack asynchronously for its owning character through registered prompt
   writer and template compiler services, then render it separately with normal template context.
4. Generated prompt source uses readable section demarcation so later AI systems can identify section boundaries.
5. The prompt formatting convention can change later without replacing the public prompt-stack authoring model.

## Technical Requirements

1. Prompt API types must live under the `AlleyCat.Mind.AI.Prompting` namespace.
2. The API must define Godot `Resource` types for `PromptStack` and prompt sections.
3. `PromptSection` must be abstract, expose a `Name`, and define one public asynchronous content method equivalent to
   `GetContentAsync(PromptSectionBuildContext buildContext, CancellationToken cancellationToken)`.
4. `PromptSection` must not keep parallel public synchronous and asynchronous content APIs.
5. `PromptSectionBuildContext` must contain required `Services`, the current `ISceneContext Scene`, and the owning
   `ICharacter Character` as typed, first-class build context.
6. Every prompt stack build must have an owning character, including stacks without lore sections.
7. `PromptSectionBuildContext` must not contain lore-specific query state such as `LoreQuery`, lore-query factories, or
   lore-query resolver methods.
8. `PromptSectionBuildContext` must not expose or wrap template render context. It must not obtain its character from an
   arbitrary template render-context key. Prompt construction and template rendering remain separate phases.
9. General prompt construction must not require a non-empty lore identity; lore-specific validation belongs to the lore
   section that requires it.
10. `TextPromptSection` must return the value of its `Text` property through the asynchronous content contract without
   changing the authored text.
11. `FilePromptSection` must return the text content loaded from its configured Godot resource path through the
   asynchronous content contract.
12. `PromptStack` must expose an ordered array of `PromptSection` resources.
13. `IPromptWriter` must asynchronously serialise `IReadOnlyCollection<PromptSection>` values into one prompt source
     string using the supplied `PromptSectionBuildContext` and `CancellationToken`.
14. `PseudoXmlPromptWriter` is the default `IPromptWriter` implementation and must be a Godot-authorable service
     registrar that registers itself as the singleton `IPromptWriter`.
15. `PromptStack` must expose asynchronous compilation equivalent to
     `CompileAsync(PromptSectionBuildContext buildContext, CancellationToken cancellationToken)`.
16. Stack compilation must resolve both `IPromptWriter` and `ITemplateCompiler` through services available from the
     build context, write `Sections ?? []`, trim the complete source, and return the compiler's `ITemplate` result.
17. Prompt source generation in `PseudoXmlPromptWriter` must await each section in order and concatenate it as:
    - opening tag using the authored section name as the tag name;
    - section content;
    - closing tag using the same generated tag name;
    - one empty line after the closing tag.
18. Section tag generation must preserve authored casing, whitespace, underscores, hyphens, and punctuation, replacing
     only `<`, `>`, and `/` with `_` in the tag name.
19. `PseudoXmlPromptWriter` must not escape or otherwise alter prompt content, including `/` characters in content.
20. `PseudoXmlPromptWriter` must reject null section collections, null section entries, and empty or whitespace-only
     section names with clear authoring errors.
21. Prompt formatting rules, including pseudo-XML tag generation, must not live in `PromptStack`; formatting is
     delegated entirely to the resolved `IPromptWriter`.
22. The API must reuse `AlleyCat.Templating.ITemplate` and `AlleyCat.Templating.ITemplateCompiler`; it must not define a
     competing template abstraction.
23. Runtime-backed sections such as `EssentialLorePromptSection` are in scope for asynchronous build support, but lore
     query details belong to AI-004.

## In Scope

- Prompt composition resources in `AlleyCat.Mind.AI.Prompting`.
- `PromptStack`, `PromptSection`, typed character-aware `PromptSectionBuildContext`, `TextPromptSection`,
  `FilePromptSection`, and `IPromptWriter` contracts.
- `PseudoXmlPromptWriter` as the default prompt writer and startup-registered Godot resource.
- Prompt-writer delegation, source trimming, and delegation to the existing template compiler.
- Async source assembly, runtime-backed prompt sections, and compiler delegation.
- Unit or integration coverage for asynchronous source assembly and compiler delegation.

## Out Of Scope

- Alternative prompt writer implementations beyond the default pseudo-XML writer.
- Prompt caching, localisation workflow, or editor preview tooling.
- Runtime agent integration beyond producing an `ITemplate` for callers.
- Detailed lore querying, filtering, formatting, and retrieval behaviour, which is specified by AI-004.
- New templating compiler implementations or template-render context changes.
- Exposing the owning character through future template render-context syntax.

## Acceptance Criteria

1. A content author can create a `PromptStack` resource with ordered named sections.
2. `TextPromptSection` contributes its configured inline text to the compiled source.
3. `FilePromptSection` contributes text loaded from a configured `res://` prompt file.
4. `PromptSection` exposes one public asynchronous content contract and no parallel public synchronous content method.
5. `PromptSectionBuildContext` requires `Services`, `Scene`, and a typed owning `ICharacter Character` for every stack.
6. Prompt stacks without lore sections accept an owning character with no non-empty lore identity requirement.
7. `PromptSectionBuildContext` has no `LoreQuery`, lore-query factory, lore-query resolver, template render context, or
   arbitrary render-context lookup for obtaining the character.
8. `PromptStack` asynchronous compilation resolves `IPromptWriter` and `ITemplateCompiler` from build-context services,
   passes one trimmed source string to the compiler, and returns its `ITemplate` result.
9. `PseudoXmlPromptWriter` awaits sections in authored order and preserves existing text and file-backed section
   behaviours while supporting runtime-backed sections such as `EssentialLorePromptSection`.
10. `PseudoXmlPromptWriter` demarcates section output with matching pseudo-XML opening and closing tags, preserving lax
   authored tag names such as `Test Fixture` and punctuation-only names such as `---`.
11. `PseudoXmlPromptWriter` replaces only `<`, `>`, and `/` in tag names, for example `Faction/Rank <Elite>` becomes
   `Faction_Rank _Elite_`.
12. `PseudoXmlPromptWriter` preserves prompt content exactly, including slash characters in content.
13. Tests or code inspection verify `PromptStack` has no local pseudo-XML tag-building implementation.
14. Startup registration includes `HandlebarsTemplateCompiler` and `PseudoXmlPromptWriter` as service registrars.
15. Tests verify ordered async section writing, inline text sections, file-backed sections, trimming, tag formatting,
     service resolution, writer registration, and compiler delegation.
16. Acceptance covers both author-facing prompt composition and the technical contracts with prompt writing, dependency
     injection, and the templating system.

## References

- [TMPL-001: Templating System](../../templating/001-templating-system/index.md)
- [AI System](../index.md)
- [AI-004: Lore And Backstory Source Compilation](../004-lore-backstory/index.md)
- `game/src/Templating/ITemplate.cs`
- `game/src/Templating/ITemplateCompiler.cs`
