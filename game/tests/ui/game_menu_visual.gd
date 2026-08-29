extends SceneTree

const TEST_SCENE_PATH := "res://tests/ui/game_menu_visual.tscn"
const STATE_DRIVER_SCRIPT_PATH := "res://tests/ui/GameMenuVisualStateDriver.cs"
const OUTPUT_ROOT := "game-menu/game_menu_visual"
const OPTION_RESUME := 0
const OPTION_EXIT_GAME := 1


func _init() -> void:
	await _run()


func _run() -> void:
	# Autoload singletons (including the Global game node) are added on the first processed
	# frames, after this script's _init starts. Wait for them so the fixture's Game stub enters
	# an already-owned singleton and legally replaces the Global instance through Game.SetInstance's
	# integration-test branch; adding the fixture earlier would make the Global autoload throw.
	await SceneUtils.wait_frames(self, 2)

	var fixture: Node = SceneUtils.instantiate_scene(TEST_SCENE_PATH)
	if fixture == null:
		SceneUtils.fatal_error_and_quit("Game menu visual runner: failed to load scene: %s" % TEST_SCENE_PATH)
		return

	root.add_child(fixture)
	# The menu's real open path pauses the scene tree mid-capture; the visual fixture keeps
	# processing so viewport rendering and texture updates stay live for the screenshots.
	fixture.process_mode = Node.PROCESS_MODE_ALWAYS
	await SceneUtils.wait_frames(self, 2)

	var viewport: SubViewport = SceneUtils.require_node(fixture, ^"Game/GameMenuViewport") as SubViewport
	var display: TextureRect = SceneUtils.require_node(fixture, ^"OverlayDisplay") as TextureRect
	var menu: Node = SceneUtils.require_node(fixture, ^"Game/GameMenuViewport/UIOverlay/GameMenu")

	if viewport == null or display == null or menu == null:
		SceneUtils.fatal_error_and_quit("Game menu visual runner: required fixture nodes are missing")
		return

	# Show the captured viewport in the window so the windowed run previews the production composition.
	display.texture = viewport.get_texture()

	var driver: Node = (load(STATE_DRIVER_SCRIPT_PATH) as Script).new()
	if driver == null:
		SceneUtils.fatal_error_and_quit("Game menu visual runner: failed to load state driver script")
		return

	# Scenario 1: resting closed state left behind by GameMenu._Ready.
	_require_state(driver, menu, false, OPTION_RESUME, "closed resting state")
	await SceneUtils.wait_frames(self, 2)
	await SceneUtils.capture_viewport_screenshot(
		self, viewport, "%s/scenarios/01_menu_closed.jpg" % OUTPUT_ROOT)

	# Scenario 2: opened through the real open path; Resume is selected by default.
	if not bool(driver.call("TryOpenMenu", menu)):
		SceneUtils.fatal_error_and_quit("Game menu visual runner: TryOpenMenu did not open the menu")
		return
	_require_state(driver, menu, true, OPTION_RESUME, "open with Resume selected")
	await SceneUtils.wait_frames(self, 2)
	await SceneUtils.capture_viewport_screenshot(
		self, viewport, "%s/scenarios/02_menu_open_resume_selected.jpg" % OUTPUT_ROOT)

	# Scenario 3: navigated through the real navigation path; Exit Game becomes the selected option.
	var selected_option: int = int(driver.call("NavigateSelectionDown", menu))
	if selected_option != OPTION_EXIT_GAME:
		SceneUtils.fatal_error_and_quit(
			"Game menu visual runner: expected selection %d after navigation but got %d"
			% [OPTION_EXIT_GAME, selected_option])
		return
	_require_state(driver, menu, true, OPTION_EXIT_GAME, "open with Exit Game selected")
	await SceneUtils.wait_frames(self, 2)
	await SceneUtils.capture_viewport_screenshot(
		self, viewport, "%s/scenarios/03_menu_open_exit_selected.jpg" % OUTPUT_ROOT)

	driver.free()
	print("GAME_MENU_VISUAL_CAPTURE_PASS artefact_root=game/temp/%s" % OUTPUT_ROOT)
	quit(0)


func _require_state(driver: Node, menu: Node, expected_open: bool, expected_option: int, label: String) -> void:
	var is_open: bool = bool(driver.call("GetIsOpen", menu))
	var selected_option: int = int(driver.call("GetSelectedOption", menu))

	if is_open != expected_open or selected_option != expected_option or menu.visible != expected_open:
		SceneUtils.fatal_error_and_quit(
			"Game menu visual runner: %s assertion failed (IsOpen=%s, SelectedOption=%d, visible=%s)"
			% [label, is_open, selected_option, menu.visible])
