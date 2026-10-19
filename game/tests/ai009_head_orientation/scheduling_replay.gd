extends "res://tests/ai009_head_orientation/ai009_orienting_photobooth.gd"

## Synthetic driver, real photobooth event path. No pose or visual correctness claim.
class UncentredDriver extends Node:
	var assigned := false
	var returned_usec := 0
	var residual_polls := 0

	func HasAssignedLookTarget(_id: String) -> bool:
		return assigned

	func GetHeadToAnchorAngleDegrees(_id: String) -> float:
		residual_polls += 1
		return 13.0 if assigned else 0.0

	func GetHeadYawDegrees(_id: String) -> float:
		return 0.0

	func GetHeadPitchDegrees(_id: String) -> float:
		return 0.0

	func GetIdleClipPositionSeconds(_id: String) -> float:
		return 0.48

	func AssignLookTarget(_id: String, anchor: Node) -> void:
		assigned = true
		if anchor.name == "Return":
			returned_usec = Time.get_ticks_usec()

	func ClearLookTarget(_id: String) -> void:
		assigned = false


func _run() -> void:
	var booth := Photobooth.new()
	var subject := Node.new()
	subject.name = "Subject"
	booth.add_child(subject)
	for anchor_name: String in ["Glance", "Return"]:
		var anchor := Node.new()
		anchor.name = anchor_name
		subject.add_child(anchor)
	var driver := UncentredDriver.new()
	root.add_child(driver)
	# Drain startup's first long frame before measuring event cadence.
	await create_timer(1.0).timeout
	await _assign(driver, booth, SHORT, "Glance")
	var start := Time.get_ticks_usec()
	_attempt_return(driver, booth)
	await create_timer(0.65).timeout
	var elapsed := (driver.returned_usec - start) / 1000000.0
	var passed := driver.returned_usec > 0 and elapsed >= 0.25 and elapsed <= 0.30
	print("SCHEDULE_REPLAY result=%s prescribed=0.25 actual=%.6f returned=%s residual=13 observation=0.65" % [
		"PASS" if passed else "FAIL", elapsed, driver.returned_usec > 0])
	print("SCHEDULE_REPLAY residual_polls=%d" % driver.residual_polls)
	booth.free()
	driver.queue_free()
	quit(0 if passed else 1)


func _attempt_return(driver: Node, booth: Photobooth) -> void:
	await create_timer(0.25).timeout
	await _assign(driver, booth, SHORT, "Return")
