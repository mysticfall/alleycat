# Project Specifications

## Specification Authoring

- [Feature Specification Template](templates/feature-spec-template.md)

## Core Systems

- [CORE-001: Global Scene](core/001-global-scene/index.md)
- [CORE-002: Configuration API](core/002-configuration-api/index.md)
- [CORE-003: Component/Trait System](core/003-component-system/index.md)
- [CORE-004: Global Service Resolution](core/004-global-service-resolution/index.md)
- [CORE-005: Scene Installer System](core/005-scene-installer-system/index.md)

## AI

- [AI System](ai/index.md)
- [AI-001: Mind Component](ai/001-mind/index.md)
- [AI-002: Agent Runtime](ai/002-agent-runtime/index.md)
- [AI-003: Prompt API](ai/003-prompt-api/index.md)
- [AI-004: Lore And Backstory Source Compilation](ai/004-lore-backstory/index.md)

## TMPL

- [TMPL-001: Templating System](tmpl/001-templating-system/index.md)

## XR

- [XR-001: XRManager](xr/001-xr-manager/index.md)

## Body

- [BODY-001: Hands](body/001-hands/index.md)
- [BODY-002: Character Physical Response System](body/002-character-physical-response/index.md)
  - [BODY-002 Design Notes](body/002-character-physical-response/design-notes.md)
  - [BODY-005: IK Target Pipeline Foundation](body/005-ik-target-pipeline-foundation/index.md)
  - [BODY-008: Character Physical Interaction API](body/008-character-stimulus-detection-and-routing/index.md)
  - [BODY-003: Full-Body Collision Decision](body/003-full-body-collision-decision/index.md)
- [BODY-004: Eyes](body/004-eyes/index.md)
- [BODY-006: Voice Component](body/006-voice/index.md)

## Characters

- [CHAR-000: Character Skeleton Profile](characters/000-character-skeleton/index.md)
- [CTRL: Player Character Control System](characters/ctrl/index.md)
  - [CTRL-001: Locomotion](characters/ctrl/001-locomotion/index.md)
  - [CTRL-002: Hand Grab Input](characters/ctrl/002-hand-grab-input/index.md)
- [IK: VRIK System](characters/ik/index.md)
  - [IK Implementation Notes](characters/ik/implementation-notes.md)
  - [IK-001: Reusable Neck-Spine CCDIK Setup](characters/ik/001-neck-spine-ik/index.md)
  - [IK-002: Arm And Shoulder IK System](characters/ik/002-arm-shoulder-ik/index.md)
    - [Arm IK Contract](characters/ik/002-arm-shoulder-ik/arm-ik-contract.md)
    - [Shoulder Correction Contract](characters/ik/002-arm-shoulder-ik/shoulder-adjustment-contract.md)
    - [Hand-Rotation Elbow Correction Contract](characters/ik/002-arm-shoulder-ik/hand-rotation-correction-contract.md)
  - [IK-003: Leg And Feet IK System](characters/ik/003-leg-feet-ik/index.md)
    - [Leg-Feet IK Contract](characters/ik/003-leg-feet-ik/leg-feet-ik-contract.md)
    - [Leg-Feet IK Test Setup Contract](characters/ik/003-leg-feet-ik/test-setup-contract.md)
  - [IK-004: VRIK Pose State Machine And Hip Reconciliation](characters/ik/004-vrik-pose-state-machine/index.md)
    - [Pose State Machine Contract](characters/ik/004-vrik-pose-state-machine/pose-state-machine-contract.md)
    - [Hip Reconciliation Contract](characters/ik/004-vrik-pose-state-machine/hip-reconciliation-contract.md)

## UI

- [UI-001: Splash Screen](ui/001-splash-screen/index.md)
- [UI-002: Loading Screen](ui/002-loading-screen/index.md)
- [UI-003: UI Overlay](ui/003-ui-overlay/index.md)

## Items

- [ITEM-001: Physics Chain Asset](items/001-physics-chain/index.md)

## Interaction

- [INTR-001: Grabbable Interface](interaction/001-grabbable/index.md)
  - [INTR-001-A: Spherical Grab Point](interaction/001-grabbable/spherical-grab-point.md)
- [INTR-002: Hand Grab Execution](interaction/002-hand-grab-execution/index.md)

## Speech

- [SPCH-001: Wav2Arkit LipSync Player](speech/001-wav2arkit-lipsync-player/index.md)
- [SPCH-002: Audio2Face LipSync Player](speech/002-audio2face-lipsync-player/index.md)
- [SPCH-003: Transcriber Component](speech/003-transcription/index.md)
- [SPCH-004: Speech Generator Component](speech/004-speech-generation/index.md)

## Testing

- [TEST-001: Integration Test Framework](testing/001-test-framework/index.md)
- [TEST-002: Visual Verification Scope](testing/002-visual-verification-scope/index.md)

## Tooling

- [GDScript API Documentation Generator](tooling/gdscript-api-doc-generator/index.md)
- [Character Generator](tooling/character-generator/index.md)
