# Content Packs

A content pack is an optional directory under `game/content/` that replaces the game's start scene and declares the
spoken language of its content. Selection happens once at game startup; the game then follows the selected pack's
spoken language for player speech recognition (STT) and NPC speech synthesis (TTS) automatically.

This document covers authoring and operation. The authoritative contracts live in
[CORE-008: Content Pack Resolution](../specs/core/008-content-pack-resolution/index.md),
[SPCH-003: Transcriber Component](../specs/speech/003-transcription/index.md), and
[SPCH-007: Supertonic Speech Generator](../specs/speech/007-supertonic-speech-generator/index.md).

## Selecting a Pack at Startup

- **Default pack**: `game/content/manifest.tres` (a `ContentManifest` resource) names the default pack through its
  `DefaultPackId`. With no launch argument, the game starts that pack when its `start.tscn` exists.
- **Explicit request**: `godot-mono --path game --content-pack <pack-id>` (or `--content-pack=<pack-id>`) overrides
  the manifest default. A requested pack missing its `start.tscn` fails startup instead of falling back.
- **Bypass**: `godot-mono --path game -- --no-content-pack` skips configured and requested packs and starts the
  built-in default content, which speaks English.
- Missing selection manifest, missing pack, and built-in fallback content are all normal English startup paths.

## Declaring the Spoken Language

Each pack may declare exactly one spoken language through an optional per-pack manifest resource at
`game/content/<pack-id>/manifest.tres`. The minimal Korean pack manifest:

```gdscript
[gd_resource type="Resource" script_class="ContentPackManifest" format=3]

[ext_resource type="Script" path="res://src/Core/Content/ContentPackManifest.cs" id="1_packmanifest"]

[resource]
script = ExtResource("1_packmanifest")
Language = "ko"
```

Authoring notes:

- Create the resource in the Godot editor as a `ContentPackManifest` resource so the script reference and UID are
  filled in automatically; the snippet above shows the resulting text shape.
- `Language` is optional. A missing per-pack manifest, an omitted value, or a blank value resolves to English —
  existing packs need no change.
- Declared values are trimmed, matched case-insensitively, and regional tags map to their primary language
  (`en-GB` → `en`, `ko-KR` → `ko`). The supported set is `en` and `ko`.
- A manifest that cannot be loaded as `ContentPackManifest`, or a declared language outside the supported set,
  fails startup with an error naming the pack, the manifest path, the declared value, and the supported set. The
  game never silently runs in another spoken language.
- Language metadata declares content language only. Packs never configure speech models, endpoints, or
  credentials; those belong to `game/AlleyCat.yaml` and the Docker deployment.

## What the Language Selects

When a pack declares `ko` (for example), the game automatically:

- sends `language=ko` with the configured multilingual model (`small` by default) on **every** player transcription
  request — both manual push-to-talk and automatic voice input — regardless of the `STT` `Language` value in
  `game/AlleyCat.yaml`, and
- synthesises NPC dialogue in Korean through Supertonic, with no per-scene editor language configuration.

Language applies to the pack selected at startup; there is no in-process language switching, per-character
language, or automatic translation.

## Author Responsibility: Prompts and Content

Declaring a language configures the speech pipelines only. **Authors own the prompts, lore, and instructions that
make the LLM actually roleplay in the declared language.** The game does not translate content: a Korean pack
should provide Korean-authored prompts and lore so characters converse in Korean, which the Korean STT/TTS
pipelines then carry end to end.

## Operation and Live Checks

After the [one-time multilingual backend migration](../docker/README.md#one-time-multilingual-migration), switching
between English and Korean packs requires no Docker configuration change, container restart, or container
recreation. To verify a deployment by hand:

1. Select an English pack at launch and hold a voice conversation: transcripts should be English and NPC speech
   audible English; the game log's content-resolution line should name the selected pack.
2. Relaunch with a Korean pack (`--content-pack <korean-pack-id>` or a changed `DefaultPackId`) against the same
   unchanged containers, and confirm Korean speech transcribes as Korean (not translated to English) and NPC
   dialogue synthesises Korean.
3. To isolate content from the backend, relaunch either pack with `-- --no-content-pack`: the game falls back to
   built-in English content.
