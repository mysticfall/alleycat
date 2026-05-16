# Character Physical Response Design Notes

This document preserves the detailed brainstorming, concerns, example scenarios, and
mental models from the design exploration. It informs implementation but is not the
authoritative specification—that role belongs to the umbrella `index.md`.

## Data Flow Architecture

```
Sensing Layer → Event Normalisation → Classification → Intent Generation → Application
```

- **Sensing Layer**: DynamicPhysicalRig proxies detect contact and impact events.
- **Event Normalisation**: Convert raw contacts into normalised physical interaction
  events.
- **Classification**: Classify events into response types.
- **Intent Generation**: Output pose, IK modifier, and locomotion intents.
- **Application**: Apply staged IK modifiers and pose recovery.

This architecture keeps sensing in the body proxies while placing response logic
elsewhere, preserving the existing separation of concerns.

## Source Contributors

Intent sources that drive physical responses:

- **XR Hand/Head**: Player tracked hand and head positions from XR subsystem.
- **Animation Pose**: Authored animation pose intent from animation playback.
- **AI Reach**: AI-driven reach targets for NPC interaction planning.
- **Procedural Foot Placement**: Procedural foot placement intent from locomotion.
- **Look-At**: Look-at target intent for head/neck orientation.

## Modifier Contributors

Transient modifications applied to source intent:

- **Impact Recoil**: Immediate displacement from collision impulse.
- **Stagger Drift**: Gradual positional drift during stagger state.
- **Muscle Weakness/Fatigue**: Reduced response magnitude from fatigue state.
- **Fear/Flinch**: Involuntary response to startling events.
- **Recovery Spring**: Spring-back force toward original pose.
- **Animation Noise**: Subtle procedural noise on poses.
- **Pain/Injury**: Modified response from injury states.
- **Contact Anticipation**: Pre-emptive adjustment for expected contact.

## Constraint Contributors

Legal-space and relationship constraints:

- **Chain Length**: Maximum distance from chain anchor point.
- **Grab Anchor**: Fixed relative position on grabbing character.
- **Joint/Anatomical Range**: Physical limits of joint rotation.
- **Leash/Soft Radius**: Maximum leash distance with soft falloff.
- **Surface/Ground**: Ground plane and surface penetration prevention.
- **Body Exclusion**: Collision exclusion between specific body regions.
- **Two-Hand Object Relation**: Fixed relationship when both hands grasp one object.

## Feedback Outputs

System feedback for debugging and further processing:

- **Collision**: Contact detected with penetration depth and normal.
- **Joint Limit**: Joint rotation at anatomical limit.
- **Chain Limit**: Chain at maximum extension.
- **Grab Constraint**: Grab at constraint boundary.
- **Motor/Solver Limit**: Physics solver or motor at limit.
- **External Override**: Higher-priority system overriding response.
- **Requested vs Realised Transform**: Delta between requested and achieved transforms.
- **Error Distribution**: How positional error distributes through kinetic chains.

## Impact Classification

Classify impacts into severity tiers:

| Tier | Response | Visual Feedback |
|------|----------|------------------|
| Tiny | Negligible | Minimal |
| Light | Subtle limb reaction | Minor pose adjustment |
| Medium | Visible torso recoil | Noticeable IK adjustment |
| Heavy | Significant displacement | Potential stagger step |
| Extreme | Full body reaction | Locomotion interruption |

## Character Impact Event Structure

```csharp
CharacterImpactEvent {
    SourceCharacter: Character reference
    TargetCharacter: Character reference
    BodyRegion: Body region identifier
    WorldPosition: Vector3
    SurfaceNormal: Vector3
    RelativeVelocity: Vector3
    ImpulseMagnitude: float
    ImpactChannel: enum (Friendly, Hostile, Environmental)
}
```

## Grab Constraint Structure

```csharp
CharacterGrabConstraint {
    GrabberCharacter: Character reference
    GrabbedCharacter: Character reference
    BodyRegion: Body region identifier
    LocalAttachPoint: Vector3
    AnchorPoint: Vector3
    Strength: float
    Compliance: float
    BreakForceThreshold: float
    ComfortFlags: ComfortFlags
}
```

### Error Distribution Through Kinetic Chains

When a grab holds under tension, distribute positional error through the kinetic chain:
hand → forearm → upper arm → shoulder → torso → hips. This prevents any single joint
from absorbing unrealistic displacement.

## Player Head Interaction

When an NPC grabs or pushes the player's head:

1. Apply comfort-safe body, neck, and shoulder reactions.
2. Provide haptic feedback through controllers.
3. Use audio/visual pressure cues.
4. Apply locomotion restrictions to prevent walking into the grabbing character.
5. Raw XR hardware and tracking input remain authoritative for player input; the virtual
   head/IK target may be displaced through the response pipeline as an intended effect.
6. XR origin compensation may move the world briefly as an intended effect.
7. **Required**: Comfort gates, risk caps, tuning, and explicit validation.

The old rule "never move the VR camera" is superseded. Movement is permitted when
controlled, gated, and validated for comfort.

## Use Cases for Real Physics Joints

Reserve the following for specific use cases, not as first implementation for live
VR player interaction:

- `Joint3D`, `ConeTwistJoint3D`, `Generic6DOFJoint3D`
- `PhysicalBoneSimulator3D`

Appropriate use cases:

- Ragdolled or incapacitated NPCs.
- Props and dynamic anchors.
- Later experimental work.

## Implementation Rules

1. Body proxies sense and provide collision data; response logic lives elsewhere.
2. Raw XR tracking hardware remains the authoritative source of player head/hand position.
   The virtual head/IK target may be displaced through the response pipeline and XR origin
   compensation as an intended effect, distinct from raw tracking authority.
3. Grabs are soft by default, using IK-space compliance rather than rigid physics.
4. Author anatomical limits to prevent unnatural poses.
5. Reactions are capped and decaying to prevent runaway feedback.
6. Contact events are throttled to avoid event storms.
7. Heavy physical simulation requires opt-in and is probably NPC-first.

## Scenarios and Concerns

### Hit Severity Tiers

Different tiers trigger different response intensities. Light impacts should not
produce heavy reactions. Implement severity mapping from impact force to response
tier.

### Character Impact Event Fields

The `CharacterImpactEvent` needs callback or delegation mechanism for game-specific
handling. Consider making it extensible for different game modes.

### Kinetic Chain Error Distribution

When a grabbed character pulls against a chain, error must flow through the chain
rather than snapping at the grab point. This requires modelling the chain as a
series of constraints with compliant error distribution.

### Real Joints and Ragdoll

Reserved for incapacitated characters and props. Live player interaction uses
soft IK-space constraints for comfort and stability.

### Open Questions

1. **Throttling Strategy**: How to throttle contact events—fixed interval, adaptive
   threshold, or event coalescing?
2. **Anatomical Limits**: What limits to author for limb reaction poses, and how to
   vary by character archetype?
3. **Player Opt-In**: Should heavy physical simulation be NPC-only, or player opt-in
   for certain game modes?
4. **Feedback Loop Validation**: How to validate that the response system does not
   create feedback loops between IK and body proxies?
5. **VR Comfort Testing**: What testing approach to verify comfort during hit
   reactions and grab interactions?
6. **Event Handling**: Should `CharacterImpactEvent` include callback/delegation for
   game-specific handling?
7. **Simultaneous Impacts**: How to handle edge cases where multiple impacts occur
   on different body regions simultaneously?

## Risks

- IK and body feedback loops causing instability.
- VR sickness from aggressive physical responses.
- Noisy contact detection leading to jitter.
- Coordinate frame mistakes in world-space calculations.
- Impossible-looking grabs breaking immersion.
- Physics solver instability with multiple interacting characters.
- Specification drift as new ideas emerge without proper review.

## Candidate Child Specifications

If this work proceeds, consider splitting into individual specifications.
Note: BODY-004 is already assigned to Eyes; these use unnumbered labels.

- **Future BODY Child**: Character Impact Event Sampling
- **Future BODY Child**: Partial Hit Reaction Overlay
- **Future BODY Child**: Stagger Response
- **Future BODY Child**: Soft Character Grab Constraints
- **Future BODY Child**: Ragdoll/Physical Constraint Bridge