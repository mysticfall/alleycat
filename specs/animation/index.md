---
id: ANIM
title: Animation
---

# Animation

## Requirement

Provide discoverable ownership boundaries for idle and locomotive animation content delivery and their runtime
consumers.

## Goal

Contributors can reach the authoritative ANIM-002 delivery and per-foot planted-interval contract and the calibrated
idle and locomotion authoring workflow without treating tooling or consumers as an alternative metadata source of truth.

## User Requirements

1. Contributors can find the idle and locomotive content delivery workflows and planted-interval contract from the
   project index.
2. Contributors can distinguish animation content ownership from player, navigation, and IK policy ownership.

## Technical Requirements

1. [ANIM-002: Locomotive Animation Set](002-locomotive-animation-set/index.md) is normative for the locomotive
   catalogue, character-owned variant-scoped embedded-library package delivery, root-motion metadata, and per-foot
   planted intervals.
2. The [Kimodo pipeline specification](../tooling/kimodo-pipeline/locomotive-animation-set.md) owns the locomotion
   authoring bootstrap and independent idle package flow through per-character calibrated retarget setups, including
   character-generic processing and validated cross-character reuse. It produces the metadata ANIM-002 requires
   without redefining that metadata contract.
3. ANIM-002 owns its package data; CTRL-001 and NAV-001 own their movement policies.

## In Scope

- Animation specification navigation and ownership boundaries.

## Out Of Scope

- Runtime locomotion policy, provider-specific production policy, navigation outcomes, and player movement arbitration.

## Acceptance Criteria

### User Requirement Acceptance

1. The project index reaches this page and this page reaches ANIM-002 and the Kimodo idle and locomotion workflow.
2. Navigation shows the content, tooling, and runtime-policy boundaries clearly.

### Technical Requirement Acceptance

1. ANIM-002 is identified as the owner of the per-foot planted-interval metadata contract, and the tooling page as the
   calibrated idle and locomotion authoring owner and producer of that metadata.
2. No link from this page presents a runtime consumer as an alternative planted-interval contract.

## Specifications

- [ANIM-002: Locomotive Animation Set](002-locomotive-animation-set/index.md)
