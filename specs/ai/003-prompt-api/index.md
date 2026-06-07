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
3. Developers can compile an authored prompt stack through the existing template compiler and render it with normal
   template context.
4. Generated prompt source uses readable section demarcation so later AI systems can identify section boundaries.
5. The prompt formatting convention can change later without replacing the public prompt-stack authoring model.

## Technical Requirements

1. Prompt API types must live under the `AlleyCat.AI.Prompting` namespace.
2. The API must define Godot `Resource` types for `PromptStack` and prompt sections.
3. `PromptSection` must be abstract, expose a `Name`, and define an abstract `GetContent` method.
4. `TextPromptSection` must return the value of its `Text` property from `GetContent`.
5. `FilePromptSection` must return the text content loaded from its configured Godot resource path.
6. `PromptStack` must expose an ordered array of `PromptSection` resources.
7. `PromptStack.Compile(ITemplateCompiler compiler)` must build the complete prompt source and return the resulting
   `ITemplate` from the supplied compiler.
8. Prompt source generation must concatenate each section as:
   - opening tag `<snake_case_section_name_replacing_blank_characters_with_underscores>`;
   - section content;
   - closing tag `</snake_case_section_name_replacing_blank_characters_with_underscores>`;
   - one empty line after the closing tag.
9. The complete concatenated source must be trimmed before it is passed to `ITemplateCompiler.Compile`.
10. The formatting implementation must be kept isolated enough to replace later, but does not need a virtual extension
    point in this version.
11. The API must reuse `AlleyCat.Templating.ITemplate` and `AlleyCat.Templating.ITemplateCompiler`; it must not define a
    competing template abstraction.

## In Scope

- Prompt composition resources in `AlleyCat.AI.Prompting`.
- `PromptStack`, `PromptSection`, `TextPromptSection`, and `FilePromptSection` contracts.
- Concatenation, section demarcation, source trimming, and delegation to the existing template compiler.
- Unit or integration coverage for source assembly and compiler delegation.

## Out Of Scope

- Final prompt syntax selection beyond the temporary section-tag convention.
- Prompt caching, asynchronous file loading, localisation workflow, or editor preview tooling.
- Runtime agent integration beyond producing an `ITemplate` for callers.
- New templating compiler implementations or template-render context changes.

## Acceptance Criteria

1. A content author can create a `PromptStack` resource with ordered named sections.
2. `TextPromptSection` contributes its configured inline text to the compiled source.
3. `FilePromptSection` contributes text loaded from a configured `res://` prompt file.
4. `PromptStack.Compile` passes one trimmed source string to the supplied `ITemplateCompiler` and returns its
   `ITemplate` result.
5. Section output is demarcated with matching snake-case opening and closing tags, with blanks replaced by underscores
   and one empty line between sections before final trimming.
6. The implementation keeps prompt-source formatting localised so the temporary convention can be changed without
   changing how authors assemble prompt stacks.
7. Tests verify ordered section assembly, inline text sections, file-backed sections, trimming, tag formatting, and
   compiler delegation.
8. Acceptance covers both author-facing prompt composition and the technical contract with the templating system.

## References

- [TMPL-001: Templating System](../../tmpl/001-templating-system/index.md)
- [AI System](../index.md)
- `game/src/Templating/ITemplate.cs`
- `game/src/Templating/ITemplateCompiler.cs`
