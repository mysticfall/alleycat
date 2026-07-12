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
3. Developers can compile an authored prompt stack through registered prompt writer and template compiler services, then
   render it with normal template context.
4. Generated prompt source uses readable section demarcation so later AI systems can identify section boundaries.
5. The prompt formatting convention can change later without replacing the public prompt-stack authoring model.

## Technical Requirements

1. Prompt API types must live under the `AlleyCat.Mind.AI.Prompting` namespace.
2. The API must define Godot `Resource` types for `PromptStack` and prompt sections.
3. `PromptSection` must be abstract, expose a `Name`, and define an abstract `GetContent` method.
4. `TextPromptSection` must return the value of its `Text` property from `GetContent`.
5. `FilePromptSection` must return the text content loaded from its configured Godot resource path.
6. `PromptStack` must expose an ordered array of `PromptSection` resources.
7. `IPromptWriter` must serialise `IReadOnlyCollection<PromptSection>` values into one prompt source string.
8. `PseudoXmlPromptWriter` is the default `IPromptWriter` implementation and must be a Godot-authorable service
   registrar that registers itself as the singleton `IPromptWriter`.
9. `PromptStack.Compile(IServiceProvider serviceProvider)` must resolve both `IPromptWriter` and `ITemplateCompiler`
   through the supplied provider, write `Sections ?? []`, trim the complete source, and return the compiler's
   `ITemplate` result.
10. Prompt source generation in `PseudoXmlPromptWriter` must concatenate each section as:
     - opening tag using the authored section name as the tag name;
     - section content;
     - closing tag using the same generated tag name;
     - one empty line after the closing tag.
11. Section tag generation must preserve authored casing, whitespace, underscores, hyphens, and punctuation, replacing
    only `<`, `>`, and `/` with `_` in the tag name.
12. `PseudoXmlPromptWriter` must not escape or otherwise alter prompt content, including `/` characters in content.
13. `PseudoXmlPromptWriter` must reject null section collections, null section entries, and empty or whitespace-only
    section names with clear authoring errors.
14. Prompt formatting rules, including pseudo-XML tag generation, must not live in `PromptStack`; formatting is
    delegated entirely to the resolved `IPromptWriter`.
15. The API must reuse `AlleyCat.Templating.ITemplate` and `AlleyCat.Templating.ITemplateCompiler`; it must not define a
     competing template abstraction.

## In Scope

- Prompt composition resources in `AlleyCat.Mind.AI.Prompting`.
- `PromptStack`, `PromptSection`, `TextPromptSection`, `FilePromptSection`, and `IPromptWriter` contracts.
- `PseudoXmlPromptWriter` as the default prompt writer and startup-registered Godot resource.
- Prompt-writer delegation, source trimming, and delegation to the existing template compiler.
- Unit or integration coverage for source assembly and compiler delegation.

## Out Of Scope

- Alternative prompt writer implementations beyond the default pseudo-XML writer.
- Prompt caching, asynchronous file loading, localisation workflow, or editor preview tooling.
- Runtime agent integration beyond producing an `ITemplate` for callers.
- New templating compiler implementations or template-render context changes.

## Acceptance Criteria

1. A content author can create a `PromptStack` resource with ordered named sections.
2. `TextPromptSection` contributes its configured inline text to the compiled source.
3. `FilePromptSection` contributes text loaded from a configured `res://` prompt file.
4. `PromptStack.Compile` resolves `IPromptWriter` and `ITemplateCompiler` from the supplied service provider, passes one
   trimmed source string to the compiler, and returns its `ITemplate` result.
5. `PseudoXmlPromptWriter` demarcates section output with matching pseudo-XML opening and closing tags, preserving lax
   authored tag names such as `Test Fixture` and punctuation-only names such as `---`.
6. `PseudoXmlPromptWriter` replaces only `<`, `>`, and `/` in tag names, for example `Faction/Rank <Elite>` becomes
   `Faction_Rank _Elite_`.
7. `PseudoXmlPromptWriter` preserves prompt content exactly, including slash characters in content.
8. Tests or code inspection verify `PromptStack` has no local pseudo-XML tag-building implementation.
9. Startup registration includes `HandlebarsTemplateCompiler` and `PseudoXmlPromptWriter` as service registrars.
10. Tests verify ordered section writing, inline text sections, file-backed sections, trimming, tag formatting, service
   resolution, writer registration, and compiler delegation.
11. Acceptance covers both author-facing prompt composition and the technical contracts with prompt writing, dependency
   injection, and the templating system.

## References

- [TMPL-001: Templating System](../../templating/001-templating-system/index.md)
- [AI System](../index.md)
- `game/src/Templating/ITemplate.cs`
- `game/src/Templating/ITemplateCompiler.cs`
