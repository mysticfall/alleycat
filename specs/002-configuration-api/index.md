---
id: CORE-002
title: Configuration API
---

# Configuration API

## Requirement

Provide a configuration API that merges project defaults from `res://AlleyCat.cfg` with per-user overrides from `user://AlleyCat.cfg`, allowing individual settings to be overridden without duplicating the entire configuration file. The API must be accessible to any subsystem that requires configuration, including but not limited to speech services.

## Goal

Enable flexible runtime configuration for game subsystems by providing a merged view of project defaults and user overrides, while keeping the project defaults file as the source of truth for available configuration keys.

## User Requirements

1. Developers must be able to configure game subsystems using a simple key-value lookup API.
2. Users must be able to override specific configuration values without modifying the shipped defaults file.
3. The system must gracefully handle missing override files by falling back to project defaults.
4. Configuration must be accessible from any game subsystem, including speech, UI, and future components.

## Technical Requirements

1. A configuration helper class or module must be defined in `@game/src/Configuration/ConfigProvider.cs`.
2. The API must load the base configuration from `res://AlleyCat.cfg` as the project defaults.
3. The API must load override configuration from `user://AlleyCat.cfg` if it exists.
4. Merge semantics must apply per-section and per-key overlay: values from the override file replace matching section/key pairs in the base file.
5. Sections and keys present only in the base file must remain available in the merged result.
6. Sections and keys present only in the override file must be added to the merged result.
7. When the override file is absent, the API must return the base configuration unchanged.
8. When a required configuration value is missing after merge (both base and override), the API must raise an appropriate error or return a sentinel value that forces the caller to handle the missing value explicitly.
9. The API must expose a method such as `GetValue(section, key, defaultValue)` returning the merged value or the provided default.
10. The API must expose a method such as `GetSection(section)` returning all key-value pairs for a given section.
11. The API must not require the override file to duplicate the entire base configuration.
12. The API must be callable from any Node or script without requiring autoload registration.

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
| Override file absent | Return base configuration from `res://AlleyCat.cfg` unchanged. |
| Section and key match | Override value replaces base value. |
| Key exists only in base | Base value retained in merged result. |
| Key exists only in override | Override value added to merged result. |
| Section exists only in override | Entire new section added to merged result. |

### File Paths

| File | Purpose | Access |
|------|---------|-------|
| `res://AlleyCat.cfg` | Project defaults (shipped with game) | Read-only |
| `user://AlleyCat.cfg` | User overrides (persisted in user data directory) | Read-write |

### API Surface

The configuration provider must expose:

```csharp
// Get a single value with optional default
public string? GetValue(string section, string key, string? defaultValue = null)

// Get all key-value pairs for a section
public Dictionary<string, string> GetSection(string section)

// Check if a section exists
public bool HasSection(string section)

// Check if a key exists in a section
public bool HasKey(string section, string key)
```

### Missing Value Handling

- When `GetValue` is called with no default and the key does not exist, the method must return `null`.
- Callers must handle `null` returns explicitly for required values.
- The implementation must not throw unhandled exceptions for missing keys; instead, it must return the default (which may be `null`).

### Example Configuration

**Base file (`res://AlleyCat.cfg`):**

```ini
[STT]
Host=https://api.openai.com/v1
ApiKey=
Model=whisper-1

[TTS]
Host=https://api.openai.com/v1
ApiKey=
Model=tts-1
Voice=alloy
```

**Override file (`user://AlleyCat.cfg`):**

```ini
[STT]
ApiKey=sk-user-key-123
```

**Merged result for [STT] section:**

```ini
Host=https://api.openai.com/v1      # from base
ApiKey=sk-user-key-123              # from override (replaced)
Model=whisper-1                     # from base
```

## Acceptance Criteria

1. The spec defines both user-visible configuration flexibility outcomes and technical implementation contracts.
2. The merge semantics are explicitly defined (base path, override path, precedence, absent file behaviour, missing value behaviour).
3. The API is specified as a library/helper class, not as a global autoload service.
4. The spec explicitly allows usage by future subsystems beyond speech.
5. The spec does not exclude mandatory implementation contracts through `Out Of Scope`.
6. Acceptance criteria verify both the user requirement (configuration flexibility) and technical requirement (merge implementation) layers.

## References

### Implementation

- `@game/src/Configuration/ConfigProvider.cs` - Configuration helper class

### Related Specs

- [SPCH-003: Transcriber Component](../speech/003-transcription/index.md)
- [SPCH-004: Speech Generator Component](../speech/004-speech-generation/index.md)

### Configuration Files

- `@game/AlleyCat.cfg` - Project defaults (base configuration)
- `user://AlleyCat.cfg` - User override configuration