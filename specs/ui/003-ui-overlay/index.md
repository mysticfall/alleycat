---
id: UI-003
---

# UI Overlay

## Requirement

The UI overlay must host reusable UI widgets and resolve them by interface type.

- The overlay must host widgets implementing `IUIWidget` and its derived contracts.
- The overlay must provide typed widget resolution through `FindWidget<TWidget>` (optional) and `GetWidget<TWidget>` (
  required).
- The overlay must provide a convenience debug API through `TrySetDebugMessage` that uses `IDebugWidget` when available.

## Goal

Provide a unified widget hosting and discovery system for the global UI layer, enabling typed widget resolution and
ad-hoc debug output.

## User Requirements

1. Players must see a global UI overlay that hosts all reusable interface widgets.
2. Players must see debug text when set through the debug API, enabling developers to display ad-hoc diagnostic
   information.

## Technical Requirements

1. **Global Hierarchy Placement**: The overlay must be a `Control` node positioned at path
   `/root/Global/XR/SubViewport/UIOverlay` in the global scene hierarchy.

2. **IUIWidget Marker Contract**: `IUIWidget` must be an empty marker interface. Any widget added to the overlay must
   implement this contract to be discoverable.

3. **Widget Discovery Model**: The overlay must resolve widgets by performing a depth-first search of its subtree,
   returning the first match found.

4. **FindWidget<T> Semantics**: `FindWidget<TWidget>` must return the first widget implementing `TWidget` in the
   subtree, or `null` when no match is available. This is an optional-resolution contract.

5. **GetWidget<T> Semantics**: `GetWidget<TWidget>` must return the resolved widget, or throw
   `InvalidOperationException` when the widget is not found. This is a required-resolution contract.

6. **IDebugWidget Contract**: `IDebugWidget` must extend `IUIWidget` and declare two methods:
    - `SetDebugMessage(string message)`: Sets the current debug message and shows the widget.
    - `ClearDebugMessage()`: Clears the current message and hides the widget.

7. **TrySetDebugMessage Behaviour**: When `IDebugWidget` is available, `TrySetDebugMessage` must set or clear the
   message and return `true`. When unavailable, it must log a warning and return `false` without throwing.

8. **DebugUIExtensions Warning Semantics**: `DebugUIExtensions.TrySetDebugMessage` must log a warning and return `false`
   when the global overlay path is unavailable, without throwing.

## In Scope

- `IUIWidget` marker contract for overlay-hosted widgets.
- `IDebugWidget` contract extending `IUIWidget` for debug display capability.
- `UIOverlay` presence and path in global hierarchy.
- Typed widget discovery model (`FindWidget<T>` optional and `GetWidget<T>` required semantics).
- Debug message set/clear behaviour through the overlay.
- Convenience debug API via `DebugUIExtensions` with warning/no-throw semantics.

## Out Of Scope

- Widget-specific UI layout or styling.
- Persistent debug logging system.
- Widget lifecycle management beyond discovery.
- Multi-widget resolution (returns first match only).

## Acceptance Criteria

1. The overlay is present in the global scene hierarchy at `/root/Global/XR/SubViewport/UIOverlay`.
2. `FindWidget<T>` returns the first matching widget in the subtree, or `null` when no match exists.
3. `GetWidget<T>` returns the resolved widget or throws `InvalidOperationException` when not found.
4. `TrySetDebugMessage` sets or clears the debug widget text and returns `true` when `IDebugWidget` is available.
5. When `IDebugWidget` is unavailable, `TrySetDebugMessage` logs a warning and returns `false` without throwing.
6. `DebugUIExtensions.TrySetDebugMessage` warns and returns `false` when the global overlay is unavailable.
7. The specification defines both user-visible behaviour outcomes and technical implementation contracts.

## References

### Implementation

- @game/src/UI/UIOverlay.cs
- @game/src/UI/IUIWidget.cs
- @game/src/UI/IDebugWidget.cs
- @game/src/UI/DebugWidget.cs
- @game/src/UI/DebugUIExtensions.cs

### Tests

- @integration-tests/src/UI/UIOverlayIntegrationTests.cs

### Godot

Scenes:

- @game/assets/ui/ui_overlay.tscn
- @game/assets/ui/debug_overlay.tscn
- @game/assets/scenes/global.tscn
