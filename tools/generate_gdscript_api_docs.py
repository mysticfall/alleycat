#!/usr/bin/env python3

"""Generate concise Markdown API docs for GDScript files."""

from __future__ import annotations

import argparse
import re
from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class GdProperty:
    name: str
    type_name: str
    annotations: list[str] = field(default_factory=list)
    summary: str = ""


@dataclass
class GdMethod:
    name: str
    params: str
    return_type: str
    is_static: bool = False
    summary: str = ""


@dataclass
class GdSignal:
    name: str
    params: str
    summary: str = ""


@dataclass
class GdClassApi:
    file_path: Path
    relative_path: Path
    class_name: str
    extends_name: str = ""
    summary: str = ""
    constants: list[str] = field(default_factory=list)
    signals: list[GdSignal] = field(default_factory=list)
    properties: list[GdProperty] = field(default_factory=list)
    methods: list[GdMethod] = field(default_factory=list)


CLASS_RE = re.compile(r"^\s*class_name\s+([A-Za-z_]\w*)")
EXTENDS_RE = re.compile(r"^\s*extends\s+([^#\n]+)")
CONST_RE = re.compile(r"^\s*const\s+([A-Za-z_]\w*)")
SIGNAL_RE = re.compile(r"^\s*signal\s+([A-Za-z_]\w*)(?:\((.*?)\))?")
VAR_RE = re.compile(
    r"^\s*(?P<annotations>(?:@[A-Za-z_]\w*(?:\([^)]*\))?\s+)*)var\s+"
    r"(?P<name>[A-Za-z_]\w*)"
    r"(?:\s*:\s*(?P<type>[^=:#\n]+))?"
)
FUNC_RE = re.compile(
    r"^\s*(?P<static>static\s+)?func\s+"
    r"(?P<name>[A-Za-z_]\w*)\s*\((?P<params>[^)]*)\)"
    r"\s*(?:->\s*(?P<return>[^:#\n]+))?"
)


def inline_summary(line: str, max_chars: int) -> str:
    marker_index = line.find("##")
    if marker_index < 0:
        return ""

    text = line[marker_index + 2 :].strip()
    return to_summary([text], max_chars)


def to_summary(comment_lines: list[str], max_chars: int) -> str:
    if not comment_lines:
        return ""

    text = " ".join(line.strip() for line in comment_lines if line.strip())
    if not text:
        return ""

    if len(text) <= max_chars:
        return text

    return text[: max_chars - 1].rstrip() + "…"


def parse_annotations(annotation_text: str) -> list[str]:
    if not annotation_text:
        return []

    return re.findall(r"@[A-Za-z_]\w*", annotation_text)


def parse_gdscript(
    path: Path,
    root: Path,
    include_private: bool,
    include_all_vars: bool,
    max_summary_chars: int,
) -> GdClassApi:
    lines = path.read_text(encoding="utf-8").splitlines()
    pending_doc: list[str] = []

    class_name = path.stem
    extends_name = ""
    class_summary = ""
    file_doc_assigned = False

    constants: list[str] = []
    signals: list[GdSignal] = []
    properties: list[GdProperty] = []
    methods: list[GdMethod] = []

    for line in lines:
        stripped = line.strip()

        if stripped.startswith("##"):
            pending_doc.append(stripped[2:].strip())
            continue

        class_match = CLASS_RE.match(line)
        if class_match:
            class_name = class_match.group(1)
            class_summary = to_summary(
                pending_doc, max_summary_chars
            ) or inline_summary(line, max_summary_chars)
            pending_doc = []
            file_doc_assigned = True
            continue

        extends_match = EXTENDS_RE.match(line)
        if extends_match:
            extends_name = extends_match.group(1).strip()
            pending_doc = []
            continue

        signal_match = SIGNAL_RE.match(line)
        if signal_match:
            signal_name = signal_match.group(1)
            signal_params = (signal_match.group(2) or "").strip()
            if include_private or not signal_name.startswith("_"):
                signals.append(
                    GdSignal(
                        name=signal_name,
                        params=signal_params,
                        summary=to_summary(pending_doc, max_summary_chars)
                        or inline_summary(line, max_summary_chars),
                    )
                )
            pending_doc = []
            continue

        const_match = CONST_RE.match(line)
        if const_match:
            constants.append(const_match.group(1))
            pending_doc = []
            continue

        var_match = VAR_RE.match(line)
        if var_match:
            annotations = parse_annotations(var_match.group("annotations") or "")
            is_exported = any(
                annotation.startswith("@export") for annotation in annotations
            )
            if include_all_vars or is_exported:
                properties.append(
                    GdProperty(
                        name=var_match.group("name"),
                        type_name=(var_match.group("type") or "Variant").strip(),
                        annotations=annotations,
                        summary=to_summary(pending_doc, max_summary_chars)
                        or inline_summary(line, max_summary_chars),
                    )
                )
            pending_doc = []
            continue

        func_match = FUNC_RE.match(line)
        if func_match:
            method_name = func_match.group("name")
            if include_private or not method_name.startswith("_"):
                methods.append(
                    GdMethod(
                        name=method_name,
                        params=func_match.group("params").strip(),
                        return_type=(func_match.group("return") or "Variant").strip(),
                        is_static=bool(func_match.group("static")),
                        summary=to_summary(pending_doc, max_summary_chars)
                        or inline_summary(line, max_summary_chars),
                    )
                )
            pending_doc = []
            continue

        if stripped.startswith("@") or stripped == "":
            continue

        if pending_doc and not file_doc_assigned:
            class_summary = to_summary(pending_doc, max_summary_chars)
            file_doc_assigned = True
        pending_doc = []

    relative_path = path.relative_to(root)
    return GdClassApi(
        file_path=path,
        relative_path=relative_path,
        class_name=class_name,
        extends_name=extends_name,
        summary=class_summary,
        constants=constants,
        signals=signals,
        properties=properties,
        methods=methods,
    )


def render_markdown(apis: list[GdClassApi], source_root_label: str) -> str:
    lines: list[str] = [
        "# GDScript API Reference",
        "",
        f"Source Root: `{source_root_label}`",
    ]

    if not apis:
        lines.extend(["", "No GDScript files found."])
        return "\n".join(lines) + "\n"

    for api in apis:
        title = f"## Class {api.class_name} (`{api.relative_path.as_posix()}`)"
        lines.extend(["", title])

        if api.extends_name:
            lines.append(f"- Extends: `{api.extends_name}`")
        if api.summary:
            lines.append(f"- Summary: {api.summary}")

        if api.constants:
            lines.extend(["", "### Constants"])
            for constant in api.constants:
                lines.append(f"- `{constant}`")

        if api.signals:
            lines.extend(["", "### Signals"])
            for signal in api.signals:
                signature = (
                    f"{signal.name}({signal.params})"
                    if signal.params
                    else f"{signal.name}()"
                )
                if signal.summary:
                    lines.append(f"- `{signature}` — {signal.summary}")
                else:
                    lines.append(f"- `{signature}`")

        if api.properties:
            lines.extend(["", "### Properties"])
            for prop in api.properties:
                annotation_suffix = ""
                if prop.annotations:
                    annotation_suffix = f" ({', '.join(prop.annotations)})"
                signature = f"{prop.name}: {prop.type_name}{annotation_suffix}"
                if prop.summary:
                    lines.append(f"- `{signature}` — {prop.summary}")
                else:
                    lines.append(f"- `{signature}`")

        if api.methods:
            lines.extend(["", "### Methods"])
            for method in api.methods:
                static_prefix = "static " if method.is_static else ""
                signature = f"{static_prefix}{method.name}({method.params}) -> {method.return_type}"
                if method.summary:
                    lines.append(f"- `{signature}` — {method.summary}")
                else:
                    lines.append(f"- `{signature}`")

    lines.append("")
    return "\n".join(lines)


def collect_apis(
    root: Path, include_private: bool, include_all_vars: bool, max_summary_chars: int
) -> list[GdClassApi]:
    script_paths = sorted(root.rglob("*.gd"))
    return [
        parse_gdscript(path, root, include_private, include_all_vars, max_summary_chars)
        for path in script_paths
    ]


def main() -> int:
    script_dir = Path(__file__).resolve().parent
    repo_root = script_dir.parent

    parser = argparse.ArgumentParser(
        description="Generate concise Markdown API docs for GDScript files."
    )
    parser.add_argument(
        "source_root",
        nargs="?",
        default="game/scripts",
        help="Directory containing .gd files",
    )
    parser.add_argument(
        "--output",
        "-o",
        default="",
        help="Output Markdown file path; prints to stdout when omitted",
    )
    parser.add_argument(
        "--include-private",
        action="store_true",
        help="Include private methods/signals (names starting with _)",
    )
    parser.add_argument(
        "--include-all-vars",
        action="store_true",
        help="Include non-exported variables (default: exported only)",
    )
    parser.add_argument(
        "--max-summary-chars",
        type=int,
        default=140,
        help="Maximum characters per summary line",
    )

    args = parser.parse_args()

    source_root_path = Path(args.source_root)
    if not source_root_path.is_absolute():
        source_root_path = repo_root / source_root_path

    root = source_root_path.resolve()
    if not root.exists() or not root.is_dir():
        raise SystemExit(f"Source root does not exist or is not a directory: {root}")

    source_root_label = args.source_root
    try:
        source_root_label = root.relative_to(repo_root).as_posix()
    except ValueError:
        source_root_label = str(root)

    apis = collect_apis(
        root, args.include_private, args.include_all_vars, args.max_summary_chars
    )
    markdown = render_markdown(apis, source_root_label)

    if args.output:
        output_path = Path(args.output)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(markdown, encoding="utf-8")
    else:
        print(markdown, end="")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
