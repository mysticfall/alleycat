---
id: UI-003
---

# UI Overlay

## Requirement

The UI overlay provides a unified widget hosting and discovery system for the global
UI layer, enabling typed widget resolution and ad-hoc debug output.

## Goal

Enable reusable UI widgets to be hosted and discovered by interface type, supporting
debug text display and temporary notification messages for players.

## User Requirements

1. Players must see a global UI overlay that hosts all reusable interface widgets.
2. Players must see debug text when set through the debug API, enabling developers
   to display ad-hoc diagnostic information.
3. Players must see temporary notification messages posted through the notification API.
4. Notifications must display in a vertical stack with the newest message appearing
   above older messages.
5. Notifications must auto-expire after a configurable timeout (default 3 seconds).
6. The overlay must preserve space at the bottom for debug and other priority widgets.

## Technical Requirements

1. **Global Hierarchy Placement**: The overlay must be a `Control` node positioned
   at path `/root/Global/XR/SubViewport/UIOverlay` in the global scene hierarchy.

2. **IUIWidget Marker Contract**: `IUIWidget` must be an empty marker interface. Any
   widget added to the overlay must implement this contract to be discoverable.

3. **Widget Discovery Model**: The overlay must resolve widgets by performing a
   depth-first search of its subtree, returning the first match found.

4. **FindWidget<T> Semantics**: `FindWidget<TWidget>` must return the first widget
   implementing `TWidget` in the subtree, or `null` when no match is available. This
   is an optional-resolution contract.

5. **GetWidget<T> Semantics**: `GetWidget<TWidget>` must return the resolved widget,
   or throw `InvalidOperationException` when the widget is not found. This is a
   required-resolution contract.

6. **IDebugWidget Contract**: `IDebugWidget` must extend `IUIWidget` and declare two
   methods: `SetDebugMessage(string message)` to set the current debug message and
   show the widget, and `ClearDebugMessage()` to clear the current message and hide
   the widget.

7. **TrySetDebugMessage Behaviour**: When `IDebugWidget` is available,
   `TrySetDebugMessage` must set or clear the message and return `true`. When
   unavailable, it must log a warning and return `false` without throwing.

8. **DebugUIExtensions Warning Semantics**: `DebugUIExtensions.TrySetDebugMessage`
   must log a warning and return `false` when the global overlay path is unavailable,
   without throwing.

9. **INotificationWidget Contract**: `INotificationWidget` must extend `IUIWidget`
   and declare: `int MaximumQueueSize` property for getting or setting the maximum
   number of queued notifications; `PostNotification(string message, double
   timeoutSeconds = 3.0)` for posting a notification for the provided duration;
   and `ClearNotifications()` for clearing all currently queued notifications.

10. **Notification Post Behaviour**: When called, `PostNotification` must: reject
    null, empty, or whitespace-only messages without notification; create a
    notification label and insert it at the top of the message stack (newest first);
    schedule expiry after `timeoutSeconds` with automatic removal; and trim the queue
    to `MaximumQueueSize` when exceeded, discarding the oldest.

11. **MaximumQueueSize Property**: `MaximumQueueSize` must default to 10 and have a
    minimum value of 1.

12. **Notification Expiry**: Expired notifications must be automatically removed from
    the queue and their labels freed.

13. **ClearNotifications Behaviour**: `ClearNotifications` must remove all queued
    notifications and free their labels.

14. **Notification Overlay Placement**: The notification widget must be positioned
    in the top-left region of the overlay, expanding downward while preserving the
    bottom region for debug and other widgets.

15. **Debug Widget Placement**: The debug widget must be positioned at the bottom
    of the overlay, horizontally centred.

16. **TryPostNotification Semantics**: `UIOverlay.TryPostNotification` must resolve
    `INotificationWidget` and call `PostNotification`, returning `true` when successful.
    When the widget is unavailable, it must log a warning and return `false` without
    throwing.

17. **TryClearNotifications Semantics**: `UIOverlay.TryClearNotifications` must
    resolve `INotificationWidget` and call `ClearNotifications`, returning `true` when
    successful. When the widget is unavailable, it must log a warning and return
    `false` without throwing.

18. **NotificationUIExtensions Semantics**: `NotificationUIExtensions.PostNotification`
    (node extension) must resolve the global overlay and call `TryPostNotification`,
    returning `false` when the overlay is unavailable. `TryClearNotifications` must
    resolve the global overlay and call `TryClearNotifications`, returning `false` when
    the overlay is unavailable.

## In Scope

- Widget discovery via `IUIWidget` marker contract.
- Debug display via `IDebugWidget` contract.
- Temporary notifications via `INotificationWidget` contract.
- Typed resolution (`FindWidget<T>` optional, `GetWidget<T>` required).
- Debug message set/clear through overlay and extension methods.
- Notification post/clear through overlay and extension methods.
- Widget placement (notification top-left, debug bottom-centred).

## Out Of Scope

- Widget-specific UI layout or styling.
- Persistent debug logging system.
- Widget lifecycle management beyond discovery.
- Multi-widget resolution (returns first match only).

## Acceptance Criteria

1. The overlay is present in the global scene hierarchy at
   `/root/Global/XR/SubViewport/UIOverlay`.
2. `FindWidget<T>` returns the first matching widget in the subtree, or `null` when
   no match exists.
3. `GetWidget<T>` returns the resolved widget or throws `InvalidOperationException`
   when not found.
4. `TrySetDebugMessage` sets or clears the debug widget text and returns `true` when
   `IDebugWidget` is available.
5. When `IDebugWidget` is unavailable, `TrySetDebugMessage` logs a warning and
   returns `false` without throwing.
6. `DebugUIExtensions.TrySetDebugMessage` warns and returns `false` when the global
   overlay is unavailable.
7. The notification widget is positioned in the top-left region of the overlay and
   expands downward.
8. The debug widget is positioned at the bottom of the overlay, horizontally centred.
9. `PostNotification` creates a notification label and inserts it at position 0 in the
   messages container (newest first).
10. When `timeoutSeconds > 0`, `PostNotification` schedules expiry and automatically
    removes the notification after the timeout.
11. The notification queue respects `MaximumQueueSize` and discards oldest entries
    when exceeded.
12. `MaximumQueueSize` defaults to 10 and has a minimum value of 1.
13. `TryPostNotification` on `UIOverlay` returns `true` when the notification widget
    is available, or logs a warning and returns `false` when unavailable.
14. `TryClearNotifications` on `UIOverlay` returns `true` when the notification
    widget is available, or logs a warning and returns `false` when unavailable.
15. `NotificationUIExtensions.PostNotification` returns `true` when the global overlay
    and notification widget are available, or logs a warning and returns `false` when
    unavailable.
16. `NotificationUIExtensions.TryClearNotifications` returns `true` when the global
    overlay and notification widget are available, or logs a warning and returns
    `false` when unavailable.
17. The specification defines both user-visible behaviour outcomes and technical
    implementation contracts.

## References

- Implementation: @game/src/UI/UIOverlay.cs
- Interfaces: @game/src/UI/IUIWidget.cs, @game/src/UI/IDebugWidget.cs,
  @game/src/UI/INotificationWidget.cs
- Widgets: @game/src/UI/DebugWidget.cs, @game/src/UI/NotificationWidget.cs
- Extensions: @game/src/UI/DebugUIExtensions.cs, @game/src/UI/NotificationUIExtensions.cs
- Tests: @integration-tests/src/UI/UIOverlayIntegrationTests.cs
- Scenes: @game/assets/ui/ui_overlay.tscn, @game/assets/ui/debug_overlay.tscn,
  @game/assets/ui/notification_overlay.tscn, @game/assets/scenes/global.tscn