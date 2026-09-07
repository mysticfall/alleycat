extends SkeletonModifier3D

const NO_ANCHOR := 0
const ADMITTED := 1
const MISSING := 2
const EMPTY := 3
const STALE := 4
const INVALID := 5
const DUPLICATE := 6
const UNPROVEN := 7

@export var left_target_path: NodePath
@export var right_target_path: NodePath
@export var evidence_state := NO_ANCHOR
@export var admitted_foot := 0
@export var admitted_anchor := Transform3D.IDENTITY
@export var residual := Vector3.ZERO

var last_target_published := false
var last_disposition := "FailedClosed"
var last_anchor := Transform3D.IDENTITY


func _process_modification_with_delta(delta: float) -> void:
	if evidence_state == ADMITTED and admitted_anchor.origin.is_finite():
		var target := get_node_or_null(left_target_path if admitted_foot == 0 else right_target_path) as Node3D
		if target != null:
			target.global_position = admitted_anchor.origin
			target.global_basis = admitted_anchor.basis.orthonormalized()
			target.force_update_transform()
			last_target_published = true
			last_disposition = "Applied"
			last_anchor = admitted_anchor
			return

	last_target_published = false
	last_anchor = Transform3D.IDENTITY
	last_disposition = "DeferredRecoveryRequired" if evidence_state == UNPROVEN else "FailedClosed"
