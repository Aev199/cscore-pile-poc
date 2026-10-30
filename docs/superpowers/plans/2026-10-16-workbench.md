# Workbench implementation plan

> **For agentic workers:** inline execution with superpowers:executing-plans.

**Goal:** Local UI around existing wall and pile calculations.
**Architecture:** Python domain service + read-only PLAXIS adapter + localhost HTTP + static SVG UI.
**Tech Stack:** Python stdlib; browser HTML/CSS/JS; existing CScore EXE.
**Spec:** docs/superpowers/specs/2026-10-16-workbench.md

## Constraints / review focus
Preserve joint forces; no phase interpolation; no double spacing conversion;
no false SLS approval; no stale result after edits; no password persistence.

- [x] Test and implement `workbench/model.py`: parse/normalize, selection, zones, recheck.
- [x] Implement `workbench/plaxis.py`: capability-checked read-only output extraction.
- [x] Implement `workbench_app.py` and `web/`: projects, async calculation, diagrams and editing.
- [x] Run Python/browser tests, add Windows smoke and packaging; independent review; publish branch.

Verification: 105 Python tests pass; browser interaction QA passes. Review findings fixed: unique phase identity, stale-zone replacement, failed reconnect clearing. New real-engine workbench smoke awaits manual Windows run; PLAXIS v22.01 awaits live check.
