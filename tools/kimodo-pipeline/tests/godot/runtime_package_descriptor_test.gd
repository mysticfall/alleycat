extends SceneTree

var passed := 0
var failed := 0
var descriptor: Script


func _init() -> void:
	descriptor = _load_helper("runtime_package_descriptor.gd")
	var library := '[gd_resource type="AnimationLibrary" format=3]\n\n[sub_resource type="Animation" id="Animation_a"]\n'
	var with_uid := library.replace(' format=3]', ' format=3 uid="uid://fixture"]')
	var trailing := library.to_utf8_buffer()
	trailing.append(0)
	var cases := [
		["valid top-level UID ignored", with_uid.to_utf8_buffer(), library.to_utf8_buffer()],
		["empty UID not ignored", library.replace(' format=3]', ' format=3 uid="uid://"]').to_utf8_buffer(), null],
		["missing quote not ignored", library.replace(' format=3]', ' format=3 uid="uid://fixture').to_utf8_buffer(), null],
		["UID-like body text not ignored", (library + '; uid="uid://fixture"\n').to_utf8_buffer(), null],
		["body mutation significant", library.replace("Animation_a", "Animation_b").to_utf8_buffer(), null],
		["whitespace mutation significant", library.replace("\n\n", "\n \n").to_utf8_buffer(), null],
		["CRLF versus LF significant", with_uid.replace("\n", "\r\n").to_utf8_buffer(), library.replace("\n", "\r\n").to_utf8_buffer()],
		["trailing-byte mutation significant", trailing, null],
	]
	for case in cases:
		var content := case[1] as PackedByteArray
		var expected := content if case[2] == null else case[2] as PackedByteArray
		var canonical: PackedByteArray = descriptor.canonical_runtime_library_bytes(content)
		_expect(canonical == expected, "%s canonical bytes" % case[0])
		_expect(_sha256(canonical) == _sha256(expected), "%s canonical digest" % case[0])
		if case[0] != "valid top-level UID ignored":
			_expect(_sha256(canonical) != _sha256(library.to_utf8_buffer()), "%s remains hash-significant" % case[0])
	var vectors_path := _tool_root().path_join("tests/fixtures/runtime_library_canonicalisation_vectors.json")
	var vectors: Variant = JSON.parse_string(FileAccess.get_file_as_string(vectors_path))
	_expect(vectors is Dictionary and not ((vectors as Dictionary).get("vectors", []) as Array).is_empty(), "shared canonicalisation vectors load: %s" % vectors_path)
	if vectors is Dictionary:
		for vector in (vectors as Dictionary)["vectors"]:
			var content := String(vector["content_hex"]).hex_decode()
			var expected := content if vector["canonical_hex"] == null else String(vector["canonical_hex"]).hex_decode()
			_expect(descriptor.canonical_runtime_library_bytes(content) == expected, "shared vector: %s" % vector["name"])
	print("ANIM-002 runtime descriptor UID tests passed: %d" % passed)
	quit(1 if failed > 0 else 0)


func _sha256(content: PackedByteArray) -> String:
	var context := HashingContext.new()
	if context.start(HashingContext.HASH_SHA256) != OK or context.update(content) != OK:
		return ""
	return context.finish().hex_encode()


func _expect(condition: bool, message: String) -> void:
	if not condition:
		push_error(message)
		failed += 1
		return
	passed += 1


func _tool_root() -> String:
	return String(get_script().resource_path).get_base_dir().get_base_dir().get_base_dir()


func _load_helper(filename: String) -> Script:
	return load(_tool_root().path_join("godot/lib").path_join(filename).simplify_path()) as Script
