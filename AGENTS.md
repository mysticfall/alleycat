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
AlleyCat.sln                      # Root .NET solution wiring game and test projects
AGENTS.md                         # Agent operating rules and project-specific instructions
README.md                         # Repository overview and developer onboarding
```

## Running The Game And Tests

- Run the game from the project root with `godot-mono --path game`.
- Run C# tests with `dotnet test tests/AlleyCat.Tests.csproj`.
- For pre-handoff verification, also run `dotnet format --verify-no-changes AlleyCat.sln` and
  `dotnet build AlleyCat.sln -warnaserror`.

## Language

Use British English, except when the instruction specifies otherwise.

## Markdown Formatting

All Markdown headings must be written in Title Case, capitalising every word, except for articles, and conjunctions.

## Tools

Always use **Context7** for API documentation, code generation, setup, or configuration tasks related to Godot and other
libraries/frameworks. For Godot API, use `/godotengine/godot-docs` as `libraryId`.

When you encounter an image that you need to analyse, use the `read` tool to load the image file. The tool returns image
contents for direct inspection.

## File References

When a file path starts with `@`, resolve it from the project root. For example, `@specs/index.md` maps to
`specs/index.md`.
