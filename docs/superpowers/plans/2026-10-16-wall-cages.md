# Wall cages implementation plan

> **For agentic workers:** use superpowers:executing-plans, inline execution.

**Goal:** Select identical wall cages from line forces without rounding As/m.
**Architecture:** Separate Python wall geometry/orchestration, existing CScore rectangle and fail-closed result parser. No upstream changes.
**Tech Stack:** Python standard library, existing .NET runner.
**Spec:** docs/wall-cage-selection.md

## Global constraints
1–3 identical cages; one row per face; separate faces; per-metre N/Mx;
ULS-first; experimental SLS; no automatic design approval; manual CI only.

## Review focus
Fractional bars per metre; face asymmetry; zero/invalid dimensions;
unknown force components; missing or inconsistent solver results.

## Tasks
- [x] Add `tests/test_wall_rebar.py`: 3m/3 cages/5Ø25 => 200mm and 2454.369mm²/m; nonintegral density; unequal faces; collision and invalid force rejection; scaled forces preserve source.
- [x] Implement `wall_rebar.py`: validate_input, make_candidates, build_engine_request, calculate, run and CLI. Reuse existing fail-closed response validation.
- [x] Add example and real-engine smoke tool: same density at 1m and 3m; reverse moment; overload skips SLS. Include in manual workflow and release.
- [x] Run complete Python suite and CLI preview; inspect workflow YAML and diff; commit and push feature branch, report numerical verification limits.

Verification: 87 Python tests passed. Review found an inter-cage clearance omission, reproduced by a failing test and fixed. Real Windows numerical smoke pending.
