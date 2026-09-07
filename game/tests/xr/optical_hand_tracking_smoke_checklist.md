# Optical Hand Tracking Hardware Smoke Checklist

Manual verification checklist for XR-002 optical hand tracking on Meta Quest 3 with WiVRn v26.6.2. Work through each
section in order and tick the checkboxes. For failures or unexpected behaviour, collect the items in
[What To Collect When Reporting](#what-to-collect-when-reporting).

## Preconditions

- [ ] WiVRn headset app settings: hand tracking is enabled in the WiVRn streaming settings on the headset.
- [ ] Quest system settings: **Settings → Movement Tracking → Hand and Body Tracking** is enabled (this gates the
      runtime's hand-tracker availability).
- [ ] Controllers are charged and paired (needed for the controller-mode halves of the checks).
- [ ] The game is launched with the headset connected and streaming:

      godot-mono --path game

- [ ] The player avatar appears and follows head movement before starting (baseline sanity).

## Mode Switching

- [ ] Put both controllers down where the headset cannot see them and present both hands to the cameras; within a
      few seconds the avatar's hands should switch from controller-driven to optically tracked.
- [ ] Pick both controllers up again and confirm controller driving resumes.
- [ ] While only one hand is optically tracked (the other holds a controller), no mode switch happens: the
      committed mode requires both hands to agree.
- [ ] Repeatedly covering and uncovering one hand does not cause rapid flip-flopping between modes.

## Wrist Behaviour

Check these while both hands are optically tracked:

- [ ] The avatar's wrists follow the real hands' positions and orientations, including rotation of the forearms.
- [ ] Occlude one hand completely (for example behind the back or under a table): that hand's wrist and fingers
      freeze at their last pose, while the other hand keeps tracking.
- [ ] While one hand is frozen, walk around so the XR origin moves: the frozen hand stays put in the world and
      does not teleport with the origin.
- [ ] After the occluded hand becomes visible again, tracking resumes on that hand without a mode transition.

## Finger Behaviour

- [ ] In optical mode, the avatar's fingers visibly follow real finger articulation — curl each finger
      individually and check the matching avatar finger responds.
- [ ] In optical mode, on hands with no committed grab, authored hand poses are overridden by the tracked fingers
      (the authored rest shape should not fight the tracked shape). While a hand holds a grabbed item, that hand
      instead shows the fixed authored grab pose.
- [ ] Switch to controller mode (pick the controllers up) and perform a grab on a grabbable object with the grab
      button: the authored grab pose plays exactly as before optical tracking existed.
- [ ] Return to controller mode after optical tracking: the authored hand pose becomes visible again immediately,
      with no stuck tracked pose and no pose clearing.

## Optical Grab Behaviour

Verify these while both hands are optically tracked, using a grabbable test ball (and a cylindrical stick where
noted). The grab lifecycle contract is in
[INTR-002: Hand Grab Execution](../../../specs/interaction/002-hand-grab-execution/index.md); the input contract is
in [CTRL-002: Hand Grab Input](../../../specs/ctrl/002-hand-grab-input/index.md).

- [ ] **Grab by closing the hand:** move an open hand near the ball and close it around the ball; the approach
      starts, the item stays still until the hand settles, then the grab commits and the ball follows the hand.
      Closing the hand with no grabbable in range must do nothing.
- [ ] **Seamless commit:** during the approach the fingers stay live tracked; on commit they blend smoothly into
      the fixed authored grab pose with no visible pop or snap.
- [ ] **Pending cancel:** close the hand to start an approach, then open the hand before the grab commits; the
      approach cancels and the hand returns to idle.
- [ ] **Hidden-open release while held:** while holding, open the real hand (the avatar hand keeps the fixed
      authored pose); after a stable opening the item releases and the avatar fingers blend back to the current
      tracked pose.
- [ ] **Over-clench and finger tolerance while held:** squeeze harder or extend one unrelated finger while
      holding; the item must stay held. Only a stable opening of the whole hand releases.
- [ ] **Held tracking loss preserves the item:** while holding, occlude the grabbing hand completely; the held
      item and the fixed pose are preserved, no release happens, and after the hand becomes visible again an open
      hand releases normally.
- [ ] **Mode switch releases:** while holding (or mid-approach), pick both controllers up so the committed mode
      switches to `Controller`; the pending grab cancels or the held item releases, and the controller grab button
      — not the optical hand — owns grabbing afterwards.
- [ ] **Opposite hand live:** hold an item with one hand (fixed authored pose) and check the other hand's fingers
      still follow real tracked articulation freely.
- [ ] **Cylindrical stick:** repeat the grab, hidden-open release, and held-loss checks on a cylindrical stick
      grabbed away from its centre; recognition must behave identically to the ball.
- [ ] **Pause suppression:** open the game menu while in optical mode; closing the hand must not start a grab and
      opening must not release a held item until the menu closes.

## Thumb Behaviour (Stage 1)

The shipped authored-reference contract is `reference-female-quest3-wivrn-authored-thumb-axes-v1`. Automated fixture
and replay coverage (XR-002 H8) verifies that opposition maps palmward and returns the calibrated neutral. It does
not establish live mesh clearance or visual acceptance.

- [ ] Treat an open thumb web/V in a fist as the accepted Stage 1 limitation (XR-002 H10), not a defect requiring
      corrective work.
- [ ] Non-thumb behaviour is unchanged from the previous validation — re-check one fist/open cycle and one
      deliberate spread per hand.

## Known Flags To Observe

These are expected behaviours and limitations from the current implementation. Report only the explicitly accepted
limitations as observations, not as defects — for example estimated or fused-tracking artefacts that OpenXR hand
tracking legitimately permits, brief occlusion recovery while tracking resumes, and similar non-contractual
tolerances:

- [ ] **Residual per-finger gain mismatch.** Finger correspondences are baselined on the rig rest pose and the
    bend axes are frame-mapped per hand, but each tracked joint's delta transfers one-to-one, so a finger
    whose physical range differs from the rig's expected range may under- or over-curl slightly at the
      extremes. Record which hand, which finger, and which direction the mismatch goes; this is an accepted
      observation, not a defect.
- [ ] **Palm-frame world-scale caveat.** The calibrated wrist composition applies the non-unit world scale
      exactly once, but this is only observable when the `XROrigin3D` is not anchored to the scene origin and the
      world scale differs from 1. With the default rig this is not visible; if you run a scaled scene and the
      wrists drift or double-scale, that is a defect — collect the world scale and origin setup for the report.
- [ ] **Hand-tracking source may report Unknown through WiVRn.** WiVRn v26.6.2 does not forward the OpenXR
      data-source extension, so the per-side observation may stay `Unknown` (ambiguous). Mode arbitration handles
      this conservatively by retaining the committed mode; you may see fewer automatic mode switches than with a
      runtime that reports gesture data sources. Note any case where a switch that should happen never does.

Findings that violate the calibration or behaviour contract in
[XR-002: Optical Hand Tracking](../../../specs/xr/002-optical-hand-tracking/index.md) are defects, not
observations — notably wrist calibration drift, double-applied world scaling, unexpected committed-mode changes
(for example the mode flipping during transient occlusion), or finger retargeting writing to non-finger bones.
Report these through [What To Collect When Reporting](#what-to-collect-when-reporting).

## What To Collect When Reporting

- [ ] WiVRn version (expected v26.6.2) and whether hand tracking was enabled in the headset app settings.
- [ ] Quest firmware version.
- [ ] For misalignment feedback: which hand, which finger joints, the direction and approximate magnitude of the
      offset, and a description or diagram of the real hand pose versus the avatar's rendered pose.
- [ ] For any freeze or mode anomaly: what both hands were doing at the time (occluded, holding controllers,
      mid-motion) and roughly when it happened.
