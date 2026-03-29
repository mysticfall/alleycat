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

## 🧹 C# Linting and Formatting

This repository uses Roslyn analysers, Microsoft.CodeAnalysis.NetAnalyzers, and `dotnet format`.

Enable the repository hook once after cloning:

```bash
git config core.hooksPath .githooks
```

Every commit then runs:

- `dotnet format --verify-no-changes AlleyCat.sln`
- `dotnet build AlleyCat.sln -warnaserror`

---

## 📜 Licence

This project is open-source and available under the terms of the [MIT Licence](LICENSE).
