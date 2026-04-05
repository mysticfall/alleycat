---
name: csharp-developer
description: Use for all tasks related to C# source code, including implementation, refactoring, and code review.
---

# C# Developer Conventions

Use this skill when writing or updating C# code in the AlleyCat repository.

## Language and Style

- Use `var` for local variable declarations only when the type is immediately clear.
- Use expression-bodied members for methods when it improves readability and the method body is a single expression.

## Node Access

- Use `RequireNode<T>` (defined in `AlleyCat.Common.NodeExtensions`) instead of `GetNode<T>` when a component
  **requires** a child node to function — a missing node indicates a scene-authoring bug.
- Bring the extension method into scope with `using AlleyCat.Common;`.

```csharp
using AlleyCat.Common;

// Inside _Ready or similar lifecycle method:
Label3D label = this.RequireNode<Label3D>("Label3D");
```

## Godot Node Classes

- Decorate any C# class that will be used as a **node type in Godot scenes** with the `[GlobalClass]` attribute.
  This registers the type in Godot's class database, making it selectable in the editor's "Create Node" dialogue and
  allowing it to appear correctly as a node type in `.tscn` files.

```csharp
[GlobalClass]
public partial class MyCustomNode : Node3D { }
```

- This applies to classes extending any Godot node type (`Node3D`, `SkeletonModifier3D`, `Control`, etc.) that will
  be instantiated in scenes. It is **not** needed for classes that are only used from C# code and never placed in
  scenes.

## Namespaces and Project Mapping

- Use `AlleyCat` as the root namespace for `AlleyCat.csproj`, mapped to the `src` folder (for example, types in
  `AlleyCat.UI` belong in `src/UI`).
- Use `AlleyCat.Tests` as the root namespace for `AlleyCat.Tests.csproj`.
- Use `AlleyCat.IntegrationTests` as the root namespace for `AlleyCat.IntegrationTests.csproj`.

## Integration Tests (Godot Runtime)

- Add Godot-dependent integration tests under `integration-tests/src/`.
- Use the `AlleyCat.IntegrationTests` namespace for integration test code.
- Author integration tests as parameterless `[Fact]` methods so they are discovered reliably.

Run the full integration suite:

```bash
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj
```

Run a subset when iterating on a feature:

```bash
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- --test-class Fully.Qualified.TypeName
dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- --test-method Fully.Qualified.TypeName.MethodName
```

- If both filters are provided, `--test-method` takes precedence over `--test-class`.
- Trait/category filters are not currently supported for integration tests.
- Treat framework/runtime errors as distinct from assertion failures when triaging results.

## Pre-Handoff Formatting Check

Format changed files before handoff:

```bash
dotnet format --verify-no-changes AlleyCat.sln 2>&1
```
