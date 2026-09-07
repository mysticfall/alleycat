---
id: ANIM
title: Animation
---

# Animation

## Requirement

Provide discoverable ownership boundaries for locomotive animation content delivery and its runtime consumers.

## Goal

Contributors can reach the authoritative ANIM-002 delivery and contact contract without treating tooling or consumers
as an alternative content source of truth.

## User Requirements

1. Contributors can find the locomotive content delivery workflow and contact contract from the project index.
2. Contributors can distinguish animation content ownership from player, navigation, and IK policy ownership.

## Technical Requirements

1. [ANIM-002: Locomotive Animation Set](002-locomotive-animation-set/index.md) is normative for the locomotive
   catalogue, character-owned variant-scoped embedded-library package delivery, central contact-permission table,
   authored-track evidence, and side-specific contact-provider boundary.
2. The [Kimodo pipeline specification](../tooling/kimodo-pipeline/locomotive-animation-set.md) owns the locomotion
   authoring bootstrap and evidence production; it does not own or override contact permission.

## In Scope

- Animation specification navigation and ownership boundaries.

## Out Of Scope

- Runtime locomotion policy, provider-specific production policy, navigation outcomes, and player movement arbitration.

## Acceptance Criteria

### User Requirement Acceptance

1. The project index reaches this page and this page reaches ANIM-002.
2. Navigation shows the content, tooling, and runtime-policy boundaries clearly.

### Technical Requirement Acceptance

1. ANIM-002 is identified as the sole contact-permission authority and the tooling page as the authoring-bootstrap
   and evidence owner, not a permission authority.
2. No link from this page presents a runtime consumer as an alternative contact contract.

## Specifications

- [ANIM-002: Locomotive Animation Set](002-locomotive-animation-set/index.md)
