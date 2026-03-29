---
name: csharp-developer
description: Use for all tasks related to C# source code, including implementation, refactoring, and code review.
---

# C# Developer Conventions

Use this skill when writing or updating C# code in the AlleyCat repository.

## Language and style

- Use `var` for local variable declarations only when the type is immediately clear.
- Use expression-bodied members for methods when it improves readability and the method body is a single expression.

## Namespaces and project mapping

- Use `AlleyCat` as the root namespace for `AlleyCat.csproj`, mapped to the `src` folder (for example, types in
  `AlleyCat.UI` belong in `src/UI`).
- Use `AlleyCat.Tests` as the root namespace for `AlleyCat.Tests.csproj`.
- Use `AlleyCat.IntegrationTests` as the root namespace for `AlleyCat.IntegrationTests.csproj`.

## Integration tests (Godot runtime)

- Add Godot-dependent integration tests under `integration-tests/src/`.
- Use the `AlleyCat.IntegrationTests` namespace for integration test code.
- Author integration tests as parameterless `[Fact]` methods so they are discovered reliably.

Run the full integration suite:

```bash
dotnet test integration-tests/AlleyCat.IntegrationTests.csproj
```

Run a subset when iterating on a feature:

```bash
dotnet test integration-tests/AlleyCat.IntegrationTests.csproj -- --test-class Fully.Qualified.TypeName
dotnet test integration-tests/AlleyCat.IntegrationTests.csproj -- --test-method Fully.Qualified.TypeName.MethodName
```

- If both filters are provided, `--test-method` takes precedence over `--test-class`.
- Trait/category filters are not currently supported for integration tests.
- Treat framework/runtime errors as distinct from assertion failures when triaging results.

## Pre-handoff formatting check

Format changed files before handoff:

```bash
dotnet format --verify-no-changes AlleyCat.sln 2>&1
```
