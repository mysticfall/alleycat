---
id: CORE-004
title: Global Service Resolution
---

# Global Service Resolution

## Requirement

The project must provide a global service resolution mechanism backed by
Microsoft.Extensions.DependencyInjection, accessible as a singleton from
any game subsystem without tight coupling to Godot autoloads or scene
placement.

## Goal

Establish `Game.Instance` as the central `IServiceProvider` backed by a
`ServiceCollection`, enabling dependency-injection patterns throughout the
game and replacing ad-hoc static accessors.

## User Requirements

1. Any game subsystem must be able to resolve registered services without
   hard-coded dependencies on specific scene nodes or autoloads.
2. Service resolution must be available for the entire game session from
   the first frame.
3. New services must be registerable at startup without modifying existing
   consumers.

## Technical Requirements

1. `Game` must implement `System.IServiceProvider` backed by
   Microsoft.Extensions.DependencyInjection or equivalent.
2. `Game` must expose a static `Instance` property that returns the singleton
   `Game` instance.
3. `Game` must maintain an internal `ServiceCollection` for registration.
4. `Game` must provide a `BuildServiceProvider()` method called once at
   startup to build the `IServiceProvider`.
5. `Game` must expose a `GetService<T>()` method for resolution.
6. `Game` must discover scene-owned service registrars before building the
   `IServiceProvider`. Registrars are child nodes implementing a service
   registrar interface, discovered recursively and deterministically
   (see Service Registrar Interface Contract).
7. Each discovered registrar must register its services into the
   `ServiceCollection` before `BuildServiceProvider()` is called.
8. `XRManager` must register itself as a service via a registrar that
   implements the service registrar interface (see Service Registrar
   Interface Contract).
9. After `BuildServiceProvider()` is called, `Game` must resolve required
   startup services from the built provider to perform post-construction
   initialisation.
10. `Game` must register core configuration and logging services before the
    provider is built; their normative contracts live in CORE-006 and CORE-007.

## In Scope

- `Game` singleton implementing `IServiceProvider`.
- `Game.Instance` static accessor.
- `ServiceCollection` backed service registration.
- `GetService<T>()` resolution method.
- Service registrar interface for scene-owned child node discovery.
- Recursive deterministic registrar discovery before `BuildServiceProvider()`.
- `XRManager` self-registration via the registrar interface.
- Post-construction resolution of required startup services from DI.
- Startup registration boundary for configuration and logging infrastructure.

## Service Registrar Interface Contract

The service registrar interface defines how scene-owned child nodes register
themselves as service providers. This enables `Game` to discover services without
hard-coded node paths or names.

**Technical Contract:**

1. A service registrar interface (for example `IServiceRegistrar`) defines a
   method to register services into a `ServiceCollection`.
2. `Game` performs a recursive depth-first traversal of its scene children,
   discovering nodes that implement the registrar interface.
3. Discovery is deterministic (for example alphabetical or insertion order) to
   ensure reproducible startup.
4. Each discovered registrar's registration method is called before
   `BuildServiceProvider()`.
5. `XRManager` (or any scene-owned service) implements the registrar interface
   and registers itself during the discovery phase.
6. The interface name and exact signature are implementation-defined; the
   contract is that discovery yields registrars that populate the
   `ServiceCollection` before the provider is built.

## Out Of Scope

- Details of configuration source ordering and typed option contracts (see
  [CORE-006: Microsoft Configuration Integration](../006-microsoft-configuration-integration/index.md)).
- Details of logging provider behaviour and call-site conventions (see
  [CORE-007: Microsoft Logging Integration](../007-microsoft-logging-integration/index.md)).
- Scoped service lifetimes.
- Service collections registered after `BuildServiceProvider()`.
- Autoload availability timing guarantees unrelated to service resolution.
- Resource-based service discovery (service discovery via exported resources
  or attributes on resource assets is future work).

## Acceptance Criteria

1. `Game` implements `IServiceProvider` and `Game.Instance` is accessible
   globally (Technical Requirements 1-2).
2. `ServiceCollection` is used for registration; `GetService<T>()` resolves
   registered services (Technical Requirements 3-5).
3. `Game` discovers service registrars via the registrar interface before
   `BuildServiceProvider()`; registration is not hard-coded to specific node
   paths or names (Technical Requirements 6-7).
4. `XRManager` registers itself via the registrar interface and is resolvable
   via `Game.Instance.GetService<XRManager>()`, enabling runtime-agnostic XR
   access without scene coupling (Technical Requirement 8).
5. `Game` resolves required startup services from DI after construction to
   perform post-construction initialisation (Technical Requirement 9).
6. Service resolution is available from the first game frame without
   requiring scene traversal (User Requirements 1-2).
7. New services can be registered at startup without modifying existing
   consumers (User Requirement 3).
8. `Out Of Scope` does not exclude mandatory service registration or
   resolution contracts needed for CORE-004 and dependent specs.
9. Configuration and logging registrations are present before provider build,
   with detailed behaviour delegated to CORE-006 and CORE-007.

## References

### Implementation

- @game/src/Game.cs
- @game/src/XR/XRManager.cs

### Related Specs

- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
- [CORE-001: Global Scene](../001-global-scene/index.md)
- [CORE-002: Configuration API](../002-configuration-api/index.md)
- [CORE-006: Microsoft Configuration Integration](../006-microsoft-configuration-integration/index.md)
- [CORE-007: Microsoft Logging Integration](../007-microsoft-logging-integration/index.md)

### External

- [Microsoft.Extensions.DependencyInjection][di-namespace]

[di-namespace]: https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.dependencyinjection
