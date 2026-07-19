---
id: ANIM
title: Animation
---

# Animation

## Requirement

Provide a discoverable entry point for the proven Mixamo source pipeline and the future content specifications that
consume its reusable outputs.

## Goal

Contributors and implementation agents can reach the normative Mixamo pipeline contract directly and identify the
future specification that owns concrete standing-locomotion content and Godot packaging.

## User Requirements

1. Contributors can find the approved animation-source workflow from the project specifications index.
2. Contributors can distinguish reusable Mixamo pipeline outputs from concrete animation selections, Godot packaging,
   and runtime behaviour.

## Technical Requirements

1. [ANIM-001: Animation Source Pipeline](001-animation-source-pipeline/index.md) is the normative contract for the
   working Mixamo acquisition, preview, retargeting, root-processing, batch-processing, metrics, and reusable output
   schemas.
2. Future ANIM-003 will own the concrete standing-locomotion selection, `locomotion_standing.blend`, extracted per-clip
   `.res` resources, and the corresponding Godot animation library.

## In Scope

- Animation specification navigation.
- Ownership boundaries between the reusable Mixamo pipeline and content-specific specifications.

## Out Of Scope

- Concrete animation selections, generated standing-locomotion content, Godot libraries, and runtime behaviour.
- Duplicating technical contracts defined by child specifications.

## Acceptance Criteria

### User Requirement Acceptance

1. The project specifications index links this page, and this page links ANIM-001.
2. The page identifies the ownership boundary between reusable pipeline outputs and ANIM-003 content.

### Technical Requirement Acceptance

1. ANIM-001 is identified as the normative source-pipeline contract.
2. Future ANIM-003 ownership is stated without introducing a dependency on an absent specification page.

## Specifications

- [ANIM-001: Animation Source Pipeline](001-animation-source-pipeline/index.md)
