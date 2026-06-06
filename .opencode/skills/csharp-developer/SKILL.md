---
name: csharp-developer
description: Use for all tasks related to C# source code, including implementation, refactoring, and code review.
---

# C# Developer Conventions

Use this skill when writing or updating C# code in the AlleyCat repository.

## Language and Style

- Use `var` for local variable declarations only when the type is immediately clear.
- Use expression-bodied members for methods when it improves readability and the method body is a single expression.
- When using **primary constructors**, avoid creating unnecessary backing fields that merely duplicate the
  constructor parameter. Instead of storing the parameter in a field and then returning that field from a
  property, return the parameter directly:

```csharp
// Avoid this pattern (redundant field):
internal sealed partial class MockXrManagerCamera(Camera3D cameraNode) : XRManagerCamera
{
    private readonly Camera3D _cameraNode = cameraNode;

    public override Camera3D Camera3D => _cameraNode;
}

// Prefer this pattern (direct use of parameter):
internal sealed partial class MockXrManagerCamera(Camera3D cameraNode) : XRManagerCamera
{
    public override Camera3D Camera3D => cameraNode;
}
```

## Naming Conventions
- Write abbreviations in uppercase when they appear as standalone words in identifiers. For example, `UIButton`,
  `XRInterface`, or `HTTPClient` — not `UiButton`, `XrInterface`, or `HttpClient`.
- Apply this rule consistently to acronyms (two or more letters) such as UI, XR, HTTP, API, ID, and URL.


## Node Access

- Use `RequireNode<T>` (defined in `AlleyCat.Common.NodeExtensions`) instead of `GetNode<T>` or `GetNodeOrNull<T>` when a component
  **requires** a child node to function — a missing node indicates a scene-authoring bug.
- Bring the extension method into scope with `using AlleyCat.Common;`.
- After resolving a required node in `_Ready()`, use the **null-forgiving operator (`!`)** when accessing members that are guaranteed to be non-null:

```csharp
using AlleyCat.Common;

// In _Ready():
Sprite2D logo = this.RequireNode<Sprite2D>("Logo/Image");

// Later in the class, no null-check needed:
Texture2D texture = logo!.Texture;
```

- This approach removes defensive null-checks for nodes resolved via `RequireNode`. The assertion is appropriate because:
  - `RequireNode` throws if the node is missing → a scene-authoring bug is caught at runtime.
  - After `_Ready()`, the node is guaranteed to exist for the lifetime of the component.

- Apply this null-forgiving pattern only to dependencies resolved in the same lifecycle phase and guaranteed by scene
  authoring. For dependencies bound later (for example in `TryBind`), use nullable fields plus explicit guard methods
  instead of `= null!` initialisers.

```csharp
using AlleyCat.Common;

// Inside _Ready or similar lifecycle method:
Label3D label = this.RequireNode<Label3D>("Label3D");
```

## Component and Trait Pattern

For reusable gameplay behaviour, follow [CORE-003: Component/Trait System](@specs/core/003-component-system/index.md):

- **Components** implementing reusable capability should implement `IComponent` (defined in `AlleyCat.Core`).
- **Holders** (entities owning components) should implement `IComponentHolder` with an explicit/cached list of components,
  providing deterministic iteration order.
- Query components using extension methods on `IComponentHolder` (bring into scope with `using AlleyCat.Core;`):
  - `RequireComponent<T>` — returns the single matching component or throws `InvalidOperationException` with clear holder/type information.
  - `TryGetComponent<T>(out T? component)` — returns `true` when exactly one match is found.
  - `GetComponents<T>()` — returns all matching components in deterministic order.

These follow the same fail-fast pattern as `RequireNode`:

```csharp
using AlleyCat.Core;

// Fail-fast: throws if missing or multiple matches
ILocomotion locomotion = holder.RequireComponent<ILocomotion>();

// Nullable fall-back: safe when absence is valid
if (holder.TryGetComponent<ILocomotion>(out var locomotion))
{
    locomotion.MoveTo(targetPosition);
}

// Multiple retrieval:
IReadOnlyList<ILocomotion> locomotionComponents = holder.GetComponents<ILocomotion>();
```

- **Interface naming**:
  - **Capability interfaces** (what a component provides) name the behaviour: `ILocomotion`, `ISpeechGenerator`.
  - **Trait interfaces** (what a holder can do) use adjective/noun naming: `ILocomotive`, `IAnimatable`, `ISeeing`, `ISpeaking`.
  - Use `IHasX` naming when adjective/noun is unclear (for example `IHasInventory`).
- **Default interface members** are permitted only for typed consumption — do not use them as true mixins to inject concrete implementation.
- **Do not force interfaces** on every Godot node or where Godot serialisation is brittle. Prefer composition through holder pattern when appropriate.

## Late-Bound Dependency Convention

- For dependencies that are resolved after `_Ready()` (for example runtime services or XR abstractions), prefer:
  - private nullable fields (for example `IXRCamera? _camera`),
  - explicit guard/resolve helpers (for example `EnsureResolvedNodes`, `GetResolvedSkeleton`),
  - fail-fast exceptions only at true misuse boundaries.
- Avoid `= null!` for late-bound dependencies when nullable + guard flow is practical.
- Prefer simple private fields for cached nodes/services over private getter/setter cache properties used only for
  indirection.

## Helper Scope and Locality

- Keep feature-specific helpers local when they have a single consumer:
  - use private static methods in the owning type, or
  - use a nested private type if lifecycle/state coupling is tight.
- Extract a top-level helper type only when it is reused by multiple consumers or needs independent test boundaries.

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

## Workaround For Godot Issue #85459

- Apply the `[Tool]` attribute to any **resource class** that is referenced by a node or resource class
  decorated with `[Tool]`.
- This works around Godot issue #85459, where referencing an undecorated resource type from a `[Tool]` class causes
  the editor to fail to load the scene or resource.

```csharp
[Tool]
public partial class MyToolNode : Node3D
{
    [Export]
    public MyResource? LinkedResource { get; set; } // MyResource must also have [Tool]
}

[Tool]
public partial class MyResource : Resource { }
```

## Godot Runtime Architecture Conventions

- Prefer plain C# arrays (for example `PoseNodeResource[]`) for exported collections in resources and nodes unless a
  Godot-specific collection API is explicitly required.
- Prefer exporting direct node references (for example `AnimationTree? AnimationTree`) over `NodePath` when the target
  node is a required, stable scene dependency.
- Use `NodePath` only when deferred/dynamic resolution is necessary. If you resolve a `NodePath`, cache the result in
  `_Ready()` and avoid repeated lookups in per-frame methods.
- In per-frame hot paths (such as `_ProcessModificationWithDelta`), avoid per-frame allocations and repeated node-path
  resolution. Reuse buffers and cached references.

## Editor Property Grouping

When a node class has many exported properties, use the `[ExportGroup]` attribute to group related properties
into collapsible sections in the Godot inspector. This improves discoverability and reduces scroll distance for
complex components.

Place each `[ExportGroup]` attribute on its own line immediately above the first `[Export]` property of that group:

```csharp
[ExportGroup("Targets")]
[Export] public Node3D? TargetNode { get; set; }

[ExportGroup("Settings")]
[Export] public float Speed { get; set; }
```

Use short, descriptive group names that reflect the logical role of the properties inside (for example `"Targets"`,
`"Settings"`, `"Debug"`). A class with fewer than five exported properties generally does not need grouping.

## Namespaces and Project Mapping

- Use `AlleyCat` as the root namespace for `AlleyCat.csproj`, mapped to the `src` folder (for example, types in
  `AlleyCat.UI` belong in `src/UI`).
- Use `AlleyCat.Tests` as the root namespace for `AlleyCat.Tests.csproj`.
- Use `AlleyCat.IntegrationTests` as the root namespace for `AlleyCat.IntegrationTests.csproj`.

## Integration Tests (Godot Runtime)

- Add Godot-dependent integration tests under `integration-tests/src/`.
- Use the `AlleyCat.IntegrationTests` namespace for integration test code.
- Author integration tests as parameterless `[Fact]` methods so they are discovered reliably.
- Load the `godot-integration-testing` skill before running, triaging, or reporting integration test results.

## Pre-Handoff Formatting Check

Format changed files before handoff:

```bash
dotnet format --verify-no-changes AlleyCat.sln 2>&1
```
