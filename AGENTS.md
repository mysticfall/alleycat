# Agent Guidelines

AlleyCat is a VR game made in Godot. It is an immersive sandbox where the player can interact with AI-driven
characters.

This project is developed using a spec-driven workflow. Feature and component work should be driven by the
specifications wiki, with each spec acting as the source of truth for planning and delivery:

- [Project Specifications](specs/index.md)

## Specification Authoring Standard

- Specifications in `specs/` are authoritative for both:
  - **User Requirements** (player/user-visible behaviour and outcomes), and
  - **Technical Requirements** (implementation contracts needed to deliver those outcomes).
- For new or updated feature specs, separate user and technical requirements explicitly with clear headings.
- `Out Of Scope` may defer optional expansion work, but must not exclude core implementation requirements that are
  necessary for delivery, validation, or integration.
- Keep tuning values flexible where appropriate (for example thresholds and curves), while still defining implementation
  structure, boundaries, and required validation contracts.
- Acceptance criteria must verify both requirement layers.

## Project Structure

```text
game/
├── src/                          # C# gameplay and systems code
├── assets/                       # Scenes, models, textures, and audio assets
├── data/                         # Gameplay data and content definitions
├── project.godot                 # Godot project configuration
└── AlleyCat.csproj               # Game C# project file
specs/
└── index.md                      # Specifications index and navigation
tests/
├── src/                          # C# unit tests without Godot API dependencies
└── AlleyCat.Tests.csproj         # Unit test project file
integration-tests/
├── src/                          # Godot-running unit tests for game components
└── AlleyCat.IntegrationTests.csproj # Integration test project file
test-framework/
├── src/                          # Shared integration test framework implementation
├── test/                         # Unit tests for the test framework
├── AlleyCat.TestFramework.csproj
└── AlleyCat.TestFramework.Tests.csproj
tools/                            # Blender and documentation generation utilities
AlleyCat.sln                      # Root .NET solution wiring game and test projects
AGENTS.md                         # Agent operating rules and project-specific instructions
README.md                         # Repository overview and developer onboarding
```

## Running The Game And Verification

- Run the game from the project root with `godot-mono --path game`.
- For pre-handoff verification, also run `dotnet format --verify-no-changes AlleyCat.sln` and
  `dotnet build AlleyCat.sln -warnaserror`.

## Integration Testing

- Load the `godot-integration-testing` skill before authoring, running, triaging, or reporting integration tests.
- Keep execution and fixture-authoring rules in that dedicated skill so this global entrypoint stays lean.
- When `xvfb-run` is available, run windowed integration tests under a virtual framebuffer to avoid disruptive
  windows; see the `godot-integration-testing` skill for the exact wrapper and the software-rendering caveat.

## Language

Use British English, except when the instruction specifies otherwise.

## Markdown Formatting

All Markdown headings must be written in Title Case, capitalising every word, except for articles, and conjunctions.

## Tools

Always use **Context7** for API documentation, code generation, setup, or configuration tasks related to Godot and other
libraries/frameworks. For Godot API, use `/godotengine/godot-docs` as `libraryId`.

When you encounter an image that you need to analyse, use the `read` tool to load the image file. The tool returns image
contents for direct inspection.

<!-- graft:start -->
## Graft — repo context graph

This repo is indexed in `graft/`: small linked markdown nodes that explain each
system and carry exact file:line spans, kept in sync with the code through git.

For ANY task here — understanding how something works, finding where code lives,
or scoping a change — get context from the graph before grepping or opening
source files. Re-ask freely (it's cheap) and reuse literal identifiers you
already have (symbol, error string, file name) as the query. New to this repo?
Run `graft map` first — a token-budgeted orientation (dir clusters, hubs,
hotspots), no LLM, no key.

- Run `graft ask "<your question>" --source` → ranked nodes with the relevant
  code spans inlined (each hit's ≤8-line crux by default; `--full` for whole
  definitions when the crux isn't enough). Match the tool to the task shape:
  for understanding or editing, the top node IS the answer — cite its
  `covers:` file:line spans and edit straight from `--source`. For
  exhaustive tasks ("every occurrence / every caller of this pattern"), ranked
  results are top-N, not complete — run `graft grep "<literal>"` instead
  (exhaustive over indexed files, grouped by enclosing symbol), falling back
  to raw `grep -rn` only for unindexed files.
- `graft skeleton <file>` → every definition's signature + span, ~10× cheaper
  than reading the file; use it to skim an API surface.
- `graft callers <symbol>` gives precomputed, exact edges — who calls this.
  Add `--direction out` for what it calls, or `--depth N` to walk
  transitively for the full blast radius. For structural questions, skip
  ranking and use this directly.
- Or browse: `graft/INDEX.md` lists every node; follow the links.
- Monorepos and folders of multiple repos rank fairly across sub-projects —
  hits carry `[scope/]` labels naming which one they're from. Narrow with
  `graft ask "<task>" --in <scope>/` once you know where you're working.

If a returned span is truncated ("+N more lines"), open the file at that exact
range before finalising. Only open source files when a node genuinely lacks a
needed detail, and then at the exact file:line the node points to — never
re-read whole files.

After big code changes, refresh the graph with `graft build` (deterministic,
no API key, $0).
<!-- graft:end -->
