# Agent Guidelines

AlleyCat is a VR game made in Godot. It is an immersive sandbox where the player can interact with AI-driven
characters.

This project is developed using a spec-driven workflow. Feature and component work should be driven by the
specifications wiki, with each spec acting as the source of truth for planning and delivery:

- [Project Specifications](specs/index.md)

## Running the game and tests

- Run the game from the project root with `godot-mono --path game`.
- Run C# tests with `dotnet test tests/AlleyCat.test/AlleyCat.test.csproj`.
- For pre-handoff verification, also run `dotnet format --verify-no-changes AlleyCat.sln` and
  `dotnet build AlleyCat.sln -warnaserror`.

## Language

Use British English, except when the instruction specifies otherwise.

## Tools

Always use **Context7** for API documentation, code generation, setup, or configuration tasks related to Godot and other
libraries/frameworks.

## File References

When a file path starts with `@`, resolve it from the project root. For example, `@specs/index.md` maps to
`specs/index.md`.
