[![AlleyCat logo](game/assets/images/logo.svg)](https://github.com/mysticfall/alleycat)

# Alley Cat

**Alley Cat** is an experimental VR and AI game platform built for
the [Godot Engine](https://github.com/godotengine/godot).

---

### ⚠️ Project Status: Early Experimental

This project is in a very early stage of development. It is currently intended for exploration and inspiration rather
than practical use in production projects.

## 🛠️ Development Requirements

- [Godot Engine .NET 4.6](https://github.com/godotengine/godot)
- [OpenCode](https://opencode.ai)
- [Godot LSP Bridge](https://github.com/MasuRii/opencode-godot-lsp) — set the `GODOT_PATH` environment variable to
  the path of your Godot executable.

## 🧩 Godot Addons

This project uses assets from the following Godot addons:

- [Mirror3D](https://godotengine.org/asset-library/asset/3983) — A customisable 3D mirror addon using a SubViewport.
- [HumanShaders](https://github.com/matmadness/HumanShaders) — A set of shaders for Godot to create realistic humanoid characters.

## 🧹 C# Linting and Formatting

This repository uses Roslyn analysers, Microsoft.CodeAnalysis.NetAnalyzers, and `dotnet format`.

Enable the repository hook once after cloning:

```bash
git config core.hooksPath .githooks
```

Every commit then runs:

- `dotnet format --verify-no-changes AlleyCat.sln`
- `dotnet build AlleyCat.sln -warnaserror`

## 🧪 Integration Testing

See the [Integration-Test Contributor Guide](specs/testing/001-test-framework/contributor-guide.md) for integration-test
quick start, filtering, diagnostics, recovery, and execution-mode requirements.

## 🎮 Game CLI Options

Custom game options are passed as user arguments after Godot's `--` separator, for example
`godot-mono --path game -- <options>`:

| Option | Description |
| ------ | ----------- |
| `-- --skip-splash` | Skips the splash screen at startup. |
| `-- --no-ai` | Suppresses all Mind agent sessions. NPCs keep perceiving and attending, but make no LLM requests. |
| `-- --no-content-pack` | Skips any configured or requested content pack and starts the built-in default content. |
| `-- --integration-test-session <args>` | Runs a persistent integration test session used by the test framework. |
| `-- --integration-probe <args>` | Integration test assembly/type discovery probe used by the test framework. |

---

## 📜 Licence

This project is open-source and available under the terms of the [MIT Licence](LICENSE).
