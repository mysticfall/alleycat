---
id: AI
title: AI System
---

# AI System

## Requirement

The AI system must support characters that can speak and act naturally during realtime player interaction.

## Goal

AlleyCat should move toward AI-driven characters that feel like people in the room: they hear the player,
choose context-aware responses, speak through the character body, and eventually coordinate speech with action.

## User Requirements

1. Players must be able to interact with AI-driven characters through natural speech.
2. AI-driven characters must respond through in-world character output rather than detached chat text.

## Technical Requirements

1. Normative AI component contracts live in child specs under this index.
2. The first component is AI-001, the Mind component, which owns speech-driven NPC response decisions.

## In Scope

- A parent index for AI character behaviour specifications.
- A single current entry for AI-001: Mind Component.

## Out Of Scope

- Final behaviour planning, animation planning, memory, emotion, or multi-character coordination contracts.

## Acceptance Criteria

1. This index states the player-visible goal for realtime AI-driven character interaction.
2. This index identifies where the current technical Mind component contract is defined.

## Specifications

- [AI-001: Mind Component](001-mind/index.md)

## References

- AI-001: Mind Component
