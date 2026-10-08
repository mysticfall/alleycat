# Pin Recording Fixtures

`Female-defaults/` contains thirteen frozen allocator recording inputs for
`NAV001PinFeasibilityIntegrationTests.Female_FrozenRecordingFixtureIntegrity`.
Keep their bytes, filenames and complete input set unchanged. The integrity test checks
the recording schema, tick contiguity, parseability, finite numeric fields and row counts;
it does not compare these inputs against current planner output.

The empty `.gdignore` prevents Godot importing these CSVs as translations. The test reads
them directly through native filesystem APIs in the source checkout; they are not exported assets.
