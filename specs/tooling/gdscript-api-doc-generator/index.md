# GDScript API Documentation Generator

## Requirement
Generate concise Markdown API documentation for GDScript source files.

## Goal
Generate concise Markdown API documentation for GDScript source files to support developer reference.

## User Requirements
- Developers can generate API docs for GDScript classes with configurable verbosity and filtering.
- Generated documentation includes class name, inheritance, constants, signals, exported variables, and functions.
- Documentation output can be directed to stdout or a specified file.
- Optional inclusion of private methods/signals and all variables (not just exported) is supported.
- Summary length for descriptions can be constrained to improve readability.
- The tool parses doc comments beginning with `##` for rich descriptions.

## Technical Requirements
- Accept a source root argument (relative to repo root or absolute), defaulting to `game/scripts`.
- Parse GDScript files to extract:
  - `class_name` declarations
  - `extends` clauses
  - Constants
  - Signals
  - Exported variables (those preceded by `@export`)
  - Functions (including signatures and return types)
- Recognise and include documentation comments that start with `##`.
- Provide command-line flags:
  - `--include-private`: Include private methods and signals in output
  - `--include-all-vars`: Include all variables, not just exported ones
  - `--max-summary-chars <N>`: Limit summary descriptions to N characters
  - `--output <FILE>`: Write output to specified file (default: stdout)
- Output formatted Markdown with clear sections for each class.
- Exit with error if source root is missing or not a directory.
- Create output path as requested when supplied; write to stdout when no output file specified.

## In Scope
- Parsing GDScript syntax for structural elements.
- Extracting and formatting doc comments.
- Command-line interface for configuration.
- Markdown output generation.
- Missing or invalid source root reporting.

## Out Of Scope
- Generating HTML or other documentation formats.
- Cross-referencing between classes in output.
- Dependency graph analysis or impact assessment.
- Integration with Godot's built-in documentation system.
- C# or other language API documentation generation.

## Acceptance Criteria
- User Requirements:
  - [ ] Running the tool without arguments generates docs for `game/scripts` to stdout.
  - [ ] The `--output` flag correctly writes to the specified file.
  - [ ] The `--include-private` flag includes private methods and signals in the output.
  - [ ] The `--include-all-vars` flag includes non-exported variables.
  - [ ] The `--max-summary-chars` flag truncates summaries appropriately.
  - [ ] Doc comments starting with `##` are included in the output.
- Technical Requirements:
  - [ ] The tool correctly parses `class_name`, `extends`, constants, signals, exported vars, and functions.
  - [ ] Output is valid Markdown with clear class separation.
  - [ ] Default source root is `game/scripts` when no argument provided.
  - [ ] The tool exits with an error when source root is missing or not a directory.
  - [ ] When an output file is specified, the tool creates the file at the requested path.
  - [ ] An explicit relative source root is accepted.
  - [ ] An absolute source root is accepted.
  - [ ] Missing/non-directory roots fail.

## References
- Source script: `tools/generate_gdscript_api_docs.py`
