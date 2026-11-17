# Navigation Playtest Feedback

## Use

Launch the existing scene as usual; recording needs no additional switch:

```sh
godot-mono --path game --xr-mode off res://assets/testing/navigation/navigation_test.tscn -- --no-ai
```

Every launch creates a unique `game/temp/motionbricks/navigation-playtests/<UTC-and-GUID>/` folder. The overlay shows
the session folder's final eight characters (match this suffix in the folder above), numbered destination attempt,
acceptance/refusal, current request number, saved-through sequence and recording health. No absolute project path is
shown on screen. Passive overlay text does not block scene clicks; only the buttons and speed slider do. Attempt numbers include rejected destinations; inference tickets and
accepted navigation request IDs are separate identities in the data.

1. Play normally. Click **Mark issue** to bookmark the current attempt/time, including after a visually bad Completed
   request. Tell us, for example, “attempt3, second bookmark: the feet moved strangely”.
2. Click **Save feedback** to seal a checkpoint without closing the game. Wait until “saving…” disappears and the
   saved-through number advances. Check the overlay for failure or dropped records.
3. Copy/share the **whole session folder**. Avoid saving again or quitting during the copy, so its manifest stays
   fixed. Extra `.partial` files may be present while playing; they are explicitly incomplete and readers ignore them.
4. Both actions are on-screen buttons only, with no keyboard shortcuts. Continue playing, or close normally. Normal
   application exit drains the native worker before sealing recording; removing the scene also waits asynchronously
   for its worker. Force-kill/crash cannot finish an open segment.

Nothing is uploaded automatically. No video, credentials or environment dump is recorded. Recordings accumulate;
delete unwanted session folders manually. There is no background deletion of old feedback.

## What Is Preserved

- Full world destination basis/origin, ordered via coordinates before clearing, actual command-start actor and
  measured/anatomical facing, numbered attempts, accepted IDs, replacements/refusals, speed and cancellation.
- Every returned native chunk, including stale/unplayed results, copied before publication; submission context and
  targets, selected role/source phase/conditioning intent, inference tickets, native-handoff ages/disposition.
- At the production skin-ready boundary: collision-realised actor and skeleton world transforms, generated input
  pose, calibrated feedback, final skeleton-space global bone poses, available blendshape weights and camera world
  transform. Failed holds also retain final bones/camera, explicitly labelling feedback as the last committed sample.
- Camera orbit/pan/zoom controller state, projection settings and viewport size; physics ticks and monotonic clocks
  align these with inputs and poses. Outcomes include successful Completed requests, not only failures.
- One `navigation-refusal` schema3 snapshot per caught ordinary navigation failure, before terminal ownership clears.
  It includes full requested/actual transforms, anatomical heading, request/generation/revisions, owned native route
  points/cursor/next point, native-agent state, selected role/source phase and last submitted target intent. That
  intent is placement evidence, not a rejected geometric proof. Godot NavigationAgent3D is routing authority; targets
  sample distance ahead along its ordered polyline. No mesh decoder, topology/centre/curve containment admission,
  cached replacement guide, body envelope, floor proof or TestMove/shape preflight is recorded or performed.
  Paths are capped at64 with original counts/truncation flags. MoveAndCollide still owns actual collision response;
  realised poses/outcomes remain evidence of movement. These records do not establish arrival. External obstacles
  can still stop movement; no new stall-triggered rerouting is provided. Readers preserve sealed payloads by their
  recorded schema rather than rewriting them as current evidence.
- Engine version, loaded assembly MVID, effective policy, rig/canonical layout and once-per-session
  critical source/content hashes. Scene hash identifies the embedded map. Model name/size/mtime are recorded, **not a
  model-byte hash**. Assembly identity is not a claim that a dirty checkout equals git HEAD. No complete asset archive.

This supports recorded-motion inspection and focused reproduction tests **without new inference**. It is not a
replay editor, a guarantee of identical fresh CUDA output/live physics, proof of foot sole contact, or pixel-perfect
render/audio capture. Only sampled physics/skin states are retained, not every rendered frame. Camera interpretation
and skeletal reconstruction use the recorded coordinate frames; actual rig geometry remains project content.

## Bundle Format and Reader

`manifest.json` has schema `alleycat-navigation-session-v1`, transport state (`recording`, `completed`, `failed`),
integrity, dropped count/error, saved-through sequence and sealed segment pairs. **Transport completed does not mean
navigation succeeded.** Only outcome events establish gameplay results. A previous checkpoint survives continued
recording; `.partial`/`.tmp` files are never advertised as complete. Segment files are flushed to disk before atomic
manifest replacement; this is not a guarantee against filesystem/device power-loss behaviour.

Each `events-NNNN.jsonl` line contains `sequence`, monotonic `clock`, `kind`, `data`, and `bulkFloats`. Main-thread
events carry physics tick, navigation time, attempt/status and camera controller state. Worker-return events have
worker clocks/tickets, not invented scene ticks. Fast worker results can precede the submission observer in JSONL;
join by ticket, not assumed callback ordering. The `rig` event supplies names/parents/rest, blendshape order and
the pose bulk layout. Returned chunks and submission events supply their frame/bone shapes.

Each paired `motion-NNNN.bin.gz` decompresses to:

```text
8 bytes ASCII ACNAV001
int32 version = 1
repeated: int64 event sequence, int32 float count, float32[count]
```

Integers/floats are little-endian. Twelve floats encode each Transform3D: basis columns X,Y,Z, then origin, each
XYZ. Float32 payloads are lossless. Pose bulk order is actor-world, skeleton-world, camera-world, generated inputs,
calibrated feedback, final skeleton-space bone globals, then scalar blend weights. Generated/feedback canonical
poses are not final skin poses; multiply final bone globals by recorded skeleton-world for world-space inspection.

`NavigationSessionReader.ReadCheckpoint(path)` reads only the manifest's sealed prefix, validating schema, headers,
sequences, JSON/binary associations, counts and gzip completion. It yields event JSON and decoded float arrays for
focused integration reproducers; it never loads a model. It is an internal feature-local reader, not a generic
recording framework. A live copied folder is usable through its saved checkpoint, even with ignored partial files.

## Cost and Failure

Queue admission is bounded to4096 items /32MiB. IO/compression/hashing run off-thread. Admitted provider arrays are
copied synchronously before publication; scene frames copy at the skin boundary. Oversize/overflow is explicit loss,
and write/observer errors are explicit failure; navigation/provider outcomes are not changed to make recording work.
Normal recording allocates copies/metadata; this is not a zero-allocation or benchmarked VR-performance claim.

One native-free technical session produced about1.06MB for165 recorded samples spanning2.73 simulated seconds plus
two generated chunks. This is a synthetic composition observation, not a real-model size benchmark. Long recordings
can reach tens of MB/minute or more depending on poses/compressibility; disk size grows with duration, not queue size.
When loss/failure is flagged, share what remains but do not describe it as a complete exact recording. If storage
cannot even persist its failure manifest, the on-screen error is the authoritative warning.
