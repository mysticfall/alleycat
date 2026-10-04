---
id: ANIM
title: Animation
---

# Animation

## Requirement

Provide discoverable ownership boundaries for locomotive animation content delivery and its runtime consumers.

## Goal

Contributors can reach the authoritative ANIM-002 delivery and per-foot planted-interval contract without treating
tooling or consumers as an alternative content source of truth.

## User Requirements

1. Contributors can find the locomotive content delivery workflow and planted-interval contract from the project
   index.
2. Contributors can distinguish animation content ownership from player, navigation, and IK policy ownership.

## Technical Requirements

1. [ANIM-002: Locomotive Animation Set](002-locomotive-animation-set/index.md) is normative for the locomotive
   catalogue, character-owned variant-scoped embedded-library package delivery, root-motion metadata, and per-foot
   planted intervals.
2. The [Kimodo pipeline specification](../tooling/kimodo-pipeline/locomotive-animation-set.md) owns the locomotion
   authoring bootstrap and produces the metadata ANIM-002 requires; it does not redefine that metadata contract.
3. ANIM-002 owns its package data; CTRL-001 and NAV-001 own their movement policies.

## In Scope

- Animation specification navigation and ownership boundaries.

## Out Of Scope

- Runtime locomotion policy, provider-specific production policy, navigation outcomes, and player movement arbitration.

## Acceptance Criteria

### User Requirement Acceptance

1. The project index reaches this page and this page reaches ANIM-002.
2. Navigation shows the content, tooling, and runtime-policy boundaries clearly.

### Technical Requirement Acceptance

1. ANIM-002 is identified as the owner of the per-foot planted-interval metadata contract, and the tooling page as the
   authoring-bootstrap owner and producer of that metadata.
2. No link from this page presents a runtime consumer as an alternative planted-interval contract.

## Specifications

- [ANIM-002: Locomotive Animation Set](002-locomotive-animation-set/index.md)
