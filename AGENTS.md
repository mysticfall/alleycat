# Agent Guidelines

AlleyCat is a VR game made in Godot. It is an immersive sandbox where the player can interact with AI-driven
characters.

This project is developed using a spec-driven workflow. Feature and component work should be driven by the
specifications wiki, with each spec acting as the source of truth for planning and delivery:

- [Project Specifications](specs/index.md)

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

## Running the game and tests

- Run the game from the project root with `godot-mono --path game`.
- Run C# tests with `dotnet test tests/AlleyCat.Tests.csproj`.
- For pre-handoff verification, also run `dotnet format --verify-no-changes AlleyCat.sln` and
  `dotnet build AlleyCat.sln -warnaserror`.

## Language

Use British English, except when the instruction specifies otherwise.

## Tools

Always use **Context7** for API documentation, code generation, setup, or configuration tasks related to Godot and other
libraries/frameworks.

When you encounter an image that you need to analyse but your model does not support multi-modal input (e.g. you cannot
natively view images), delegate the task to a vision-capable MCP tool if available. Pass the image source and a clear
prompt describing what to extract or interpret.

## File References

When a file path starts with `@`, resolve it from the project root. For example, `@specs/index.md` maps to
`specs/index.md`.
