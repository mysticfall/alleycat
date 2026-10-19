extends "res://tests/ai009_head_orientation/ai009_orienting_photobooth.gd"

const Replay = preload("res://tests/ai009_head_orientation/scheduling_replay.gd")

class Driver extends Replay.UncentredDriver:
	var residual := 13.0
	var phase := 0.48

	func GetHeadToAnchorAngleDegrees(_id: String) -> float:
		return residual

	func GetIdleClipPositionSeconds(_id: String) -> float:
		return phase


func _run() -> void:
	_continue_on_failure = true
	var booth := Photobooth.new()
	var subject := Node.new()
	subject.name = "Subject"
	booth.add_child(subject)
	for anchor_name: String in ["Glance", "Return"]:
		var anchor := Node.new()
		anchor.name = anchor_name
		subject.add_child(anchor)
	var driver := Driver.new()
	root.add_child(driver)
	await create_timer(1.0).timeout
	for residual: float in [0.0, 13.0]:
		driver.residual = residual
		for duration: float in [EYES_ONLY_GLANCE_WAIT_SECONDS, OUT_OF_CONE_GLANCE_WAIT_SECONDS]:
			for ending: String in ["return", "clear"]:
				_assign(driver, booth, SHORT, "Glance")
				var start: int = _last_assignment_usec[SHORT]
				await _wait_brief_deadline(SHORT, duration)
				if ending == "return":
					_assign(driver, booth, SHORT, "Return")
				else:
					_clear(driver, SHORT)
				var elapsed := (Time.get_ticks_usec() - start) / 1000000.0
				_assert(elapsed >= duration and elapsed <= duration + 0.05,
					"schedule residual=%.1f ending=%s prescribed=%.2f actual=%.6f" % [residual, ending, duration, elapsed])
	# A centred but permanently wrong phase and an uncentred head each fail closed.
	for blocked_by: String in ["centring", "phase"]:
		driver.assigned = true
		driver.residual = 13.0 if blocked_by == "centring" else 0.0
		driver.phase = 0.48 if blocked_by == "centring" else 2.0
		var before_events := driver.returned_usec
		var before_failures := _failure_summaries.size()
		var start := Time.get_ticks_usec()
		var ready := await _prepare_assignment(driver, SHORT, 0.7)
		var elapsed := (Time.get_ticks_usec() - start) / 1000000.0
		var recorded := _failure_summaries.size() == before_failures + 1
		if recorded:
			_failure_summaries.pop_back() # Expected failure, not a photobooth production outcome.
		_assert(not ready and recorded and elapsed >= 0.7 and elapsed < 0.9
			and before_events == driver.returned_usec,
			"setup fails closed blocked_by=%s actual=%.6f ready=%s" % [blocked_by, elapsed, ready])
	driver.residual = 0.0
	driver.phase = 0.48
	_assert(await _prepare_assignment(driver, SHORT, 1.0), "normal setup succeeds")
	_assign(driver, booth, SHORT, "Return")
	print("SCHEDULE_CONTRACT_COMPLETE failures=%d" % _failure_summaries.size())
	booth.free()
	driver.queue_free()
	quit(0 if _failure_summaries.is_empty() else 1)
