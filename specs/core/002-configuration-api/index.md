---
id: CORE-002
title: Configuration API
---

# Configuration API

## Requirement

Provide a configuration API that merges project defaults from `res://AlleyCat.cfg`
with per-user overrides from `user://AlleyCat.cfg`.
Individual settings must be overrideable without duplicating the entire configuration file.
The API must be accessible to any game subsystem.

## Goal

Enable flexible runtime configuration by providing a merged view of project defaults and user overrides.
The project defaults file must remain the source of truth for available configuration keys.

## User Requirements

1. Developers must be able to configure game subsystems using a simple key-value lookup API.
2. Users must be able to override specific configuration values without modifying the shipped defaults.
3. The system must gracefully handle missing override files by falling back to project defaults.
4. Configuration must be accessible from any game subsystem.

## Technical Requirements

1. A configuration helper class must be defined in `@game/src/Core/ConfigProvider.cs`.
2. The API must load base configuration from `res://AlleyCat.cfg`.
3. The API must load override configuration from `user://AlleyCat.cfg` if it exists.
4. Merge semantics must apply per-section and per-key overlay.
5. Sections and keys present only in the base file must remain in the merged result.
6. Sections and keys present only in the override file must be added to the merged result.
7. When the override file is absent, the API must return the base configuration unchanged.
8. When a required configuration value is missing after merge, the API must return a sentinel value.
The caller must handle the missing value explicitly.
9. The API must expose a method to retrieve a single value with an optional default.
10. The API must expose a method to retrieve all key-value pairs for a given section.
11. The API must not require the override file to duplicate the entire base configuration.
12. The API must be callable from any Node or script without autoload registration.

## In Scope

- Configuration helper class with merge semantics.
- Base configuration file path: `res://AlleyCat.cfg`.
- Override configuration file path: `user://AlleyCat.cfg`.
- Per-section and per-key value overlay merge strategy.
- Fallback behaviour when override file is absent.
- Error behaviour when required values are missing after merge.
- API surface for key lookup and section enumeration.
- Support for future subsystems beyond speech.

## Out Of Scope

- Configuration validation schemas or automatic type conversion beyond basic string parsing.
- Hot-reloading of configuration at runtime without explicit re-fetch.
- Configuration UI for editing user overrides.
- Remote configuration or cloud-based settings.
- Encryption or obfuscation of configuration values.

## Contract

### Merge Semantics

The configuration API must implement the following merge strategy:

| Scenario | Behaviour |
|----------|-----------|
| Override file absent | Return base configuration unchanged. |
| Section and key match | Override value replaces base value. |
| Key exists only in base | Base value retained. |
| Key exists only in override | Override value added. |
| Section exists only in override | New section added. |

### File Paths

| File | Purpose | Access |
|------|---------|-------|
| `res://AlleyCat.cfg` | Project defaults (shipped with game) | Read-only |
| `user://AlleyCat.cfg` | User overrides (persisted in user data directory) | Read-write |

### API Surface

The configuration provider must expose methods to:

- Retrieve a single value with an optional default.
- Retrieve all key-value pairs for a given section.
- Check if a section or key exists.

Exact method signatures are defined in the implementation.

### Missing Value Handling

- When a value is requested with no default and the key does not exist, the method must return `null`.
- Callers must handle `null` returns explicitly for required values.
- The implementation must not throw unhandled exceptions for missing keys.

### Example Configuration

Base file defines defaults; override file specifies user changes.
For example, if the base contains `[STT]` with an empty `ApiKey`.
The override contains `[STT]` with `ApiKey=sk-user-key-123`, so the merged result uses the override value.
Other base keys like `Model` and `Host` are preserved in the merged result.

## Acceptance Criteria

1. The spec defines both user-visible configuration flexibility outcomes and technical implementation contracts.
2. The merge semantics are explicitly defined.
This includes base path, override path, precedence, absent file behaviour, and missing value behaviour.
3. The API is specified as a library/helper class, not as a global autoload service.
4. The spec explicitly allows usage by future subsystems beyond speech.
5. The spec does not exclude mandatory implementation contracts through `Out Of Scope`.
6. Acceptance criteria verify both the user requirement (configuration flexibility)
and technical requirement (merge implementation) layers.

## References

### Implementation

- `@game/src/Core/ConfigProvider.cs` - Configuration helper class

### Related Specs

- [SPCH-003: Transcriber Component](../speech/003-transcription/index.md)
- [SPCH-004: Speech Generator Component](../speech/004-speech-generation/index.md)

### Configuration Files

- `@game/AlleyCat.cfg` - Project defaults (base configuration)
- `user://AlleyCat.cfg` - User override configuration