# External GDScript Seam Spike

This disposable spike proves the external-script command required by the Kimodo
migration seam, without moving any existing game tooling. From the repository
root, the expected command is:

```sh
godot-mono --xr-mode off --path game --script "$(pwd)/tools/kimodo-pipeline/godot/external-gdscript-seam-spike/run_external_gdscript_seam.gd" -- --probe-id=external-path-proof
```

The automated test runs the CWD-independent equivalent from a temporary,
arbitrary working directory, using absolute paths for both `--path` and
`--script`. Neither the project namespace nor external helper loading depends
on the current directory.

`run_external_gdscript_seam.gd` derives the helper's absolute filesystem path
from its own loaded script path and calls `load()` on that path. It does not use
`res://tools/...` to find the helper. It then read-loads the existing
`res://default_bus_layout.tres` resource and emits
`KIMODO_EXTERNAL_GDSCRIPT_SEAM=ok` only when both seams resolve correctly.

No `.gd.uid` files are committed for this disposable spike. Godot executes the
external scripts by absolute path and loads their helper successfully without
them; the test records that engine behaviour.

Run only this proof with:

```sh
python3 tools/kimodo-pipeline/godot/external-gdscript-seam-spike/test_external_gdscript_seam.py
```
