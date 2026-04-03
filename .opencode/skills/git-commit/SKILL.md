---
name: git-commit
description: Use this skill when preparing and creating Git commits.
---

# Git Commit Workflow

## Rules

- Keep each commit atomic: one coherent change per commit.
- Stage files first.
- Propose a commit message and ask for user confirmation before running `git commit`.
- Do not commit without explicit confirmation.
- Ensure the commit title includes punctuation.
- Do not hard-wrap the commit body.

## Commit Message Format

- Title: short, specific, and punctuated.
- Body: optional; explain why the change exists.

Example title format:

`Add inventory slot highlights to the UI.`

## Required Flow

1. Review changed files.
2. Stage only files that belong to one atomic change.
3. Draft the commit message.
4. Ask: "Proposed commit message:\n\n<title>\n\n<body if any>\n\nProceed with commit?"
5. Commit only after the user confirms.

## If Scope Is Mixed

- Stop and split into separate commits.
- Ask the user how to group files if grouping is unclear.
