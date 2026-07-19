---
id: ANIM
title: Animation
---

# Animation

## Requirement

Provide a discoverable entry point for the proven Mixamo source pipeline and the concrete animation catalogues that
consume its reusable outputs.

## Goal

Contributors and implementation agents can reach the normative Mixamo pipeline contract and the standing-locomotion
catalogue contract directly.

## User Requirements

1. Contributors can find the approved animation-source workflow from the project specifications index.
2. Contributors can distinguish reusable Mixamo pipeline outputs from concrete animation selections, Godot packaging,
   and runtime behaviour.

## Technical Requirements

1. [ANIM-001: Animation Source Pipeline](001-animation-source-pipeline/index.md) is the normative contract for the
   working Mixamo acquisition, preview, retargeting, root-processing, batch-processing, metrics, and reusable output
   schemas.
2. [ANIM-003: Standing Locomotion Catalogue](003-standing-locomotion-catalogue/index.md) is the normative contract for
   the concrete standing-locomotion selection, `locomotion_standing.blend`, extracted per-clip `.res` resources,
   reusable Godot animation library, package metadata, and content-specific validation.

## In Scope

- Animation specification navigation.
- Ownership boundaries between the reusable Mixamo pipeline and content-specific specifications.

## Out Of Scope

- Runtime animation selection, transition behaviour, player locomotion, and navigation integration.
- Duplicating technical contracts defined by child specifications.

## Acceptance Criteria

### User Requirement Acceptance

1. The project specifications index links this page, and this page links ANIM-001 and ANIM-003.
2. The page identifies the ownership boundary between reusable pipeline schemas, concrete catalogue content, and
   runtime consumers.

### Technical Requirement Acceptance

1. ANIM-001 is identified as the normative source-pipeline contract.
2. ANIM-003 is identified as the normative standing-locomotion catalogue and Godot packaging contract.

## Specifications

- [ANIM-001: Animation Source Pipeline](001-animation-source-pipeline/index.md)
- [ANIM-003: Standing Locomotion Catalogue](003-standing-locomotion-catalogue/index.md)
