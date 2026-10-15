# First 70-case SLS matrix audit — 15 October 2026

## Provenance and acceptance boundary

**Compiled and executed:** https://github.com/Aev199/cscore-pile-poc/actions/runs/38761812114, SHA `dd22596c4efce583809f89db4f2b246dacf99c32`. 58 Python contract tests and all 486 upstream CScore targeted tests passed. Windows EXE built, ordinary pile SLS sample passed contract checks, and the 70-case research matrix completed.

**Important:** the green workflow means the *research pipeline* completed correctly, NOT that every individual crack solver case converged or that ARBAT agrees. The 70-case matrix has **ZERO** external ARBAT reference values; `arbat_reference_TO_FILL.csv` intentionally contains empty numeric reference fields. The only independent ARBAT numerical comparison so far is the earlier nine comparable circular-pile cases documented in `docs/crack-arbat-compatibility.md`.

The report below was calculated from the actual archived `sls_matrix/cross_section_sls_matrix.csv` and per-section JSON in the Actions artifact, not guessed from the source code.

## Coverage

| Geometry | Total | Calculated | Indeterminate |
|---|---:|---:|---:|
| Four rectangles | 40 | 30 | 10 |
| Three circles | 30 | 29 | 1 |
| **All** | **70** | **59** | **11** |

| Cross-section ID | Calculated / 10 | Exceeds preliminary crack limit | Below preliminary crack limit | Indeterminate |
|---|---:|---:|---:|---:|
| R300x500_4d20_cover45 | 5 | 4 | 1 | 5 |
| R400x600_6d20_cover50 | 10 | 7 | 3 | 0 |
| R500x700_8d25_cover60 | 8 | 0 | 8 | 2 |
| R400x400_8d16_two_layers_cover35 | 7 | 6 | 1 | 3 |
| C600_8d20_cover50 | 10 | 6 | 4 | 0 |
| C800_16d25_cover55 | 9 | 0 | 9 | 1 |
| C1000_20d28_cover70 | 10 | 0 | 10 | 0 |

Among the 59 research-calculated cases, 36 were below and 23 above the **preliminary** governing long/short SLS limit. None happened to be within the configured ±3% engineering-review band; the handling of 0.989/1.014 was covered by dedicated synthetic contract tests.

**Not an error by itself:** all ten C1000 cases returned zero cracking because each applied bending moment lay below its computed crack threshold (for example `Mcrc≈304 kNm` at N=0, vs 180 kNm maximum single-axis bending), using the present unverified SLS material model. This must be checked with ARBAT rather than taking zero as independently established.

## Eleven indeterminate combinations (not approvals)

- R300x500/4Ø20: T02, T03, T04, T05 and T07 (**five**).
- R400x400/8Ø16, two reinforcement rows: T02, T03 and T07 (**three**).
- R500x700/8Ø25: T09 and T10 (**two**).
- C800/16Ø25: T09 (**one**).

Raw CScore returns `cracked=false` in these cases even though the full bending moment is **greater** than `Mcrc`; for example R300×500 T02 has `Mtotal=180 kNm`, `Mcrc≈35.13 kNm`, and R500×700 T09 has `Mtotal=160 kNm`, `Mlong=80 kNm`, `Mcrc≈115.32 kNm`. Our adapter correctly converts these ambiguous upstream states to `indeterminate_crack_solver_or_upstream_silent_fallback` rather than interpreting a zero crack width as passing. The root cause *within upstream* remains to be investigated; it could involve solver nonconvergence or an early-return branch.

This distinction matters: the benchmark currently checks **pipeline completion**, not 70/70 numerical validity.

## Preliminary method choices verified by CI

- Default `arbat_conservative`: historical pre-crack stress reconstruction where applicable, conservatively enveloped with the unchanged original CScore width. Fallback for nonapplicable situations: code-permitted preliminary `psi_s=1` screening; no hidden calibration.
- Optional `native_refined`: original upstream author's post-crack stress refinement, not classified as an incorrect algorithm.
- For each candidate, take **maximum of normalized long- and short-term crack-width checks**; any missing required check is indeterminate.
- A near-unity `K=0.989` or `K=1.014` falls into `engineering_review`, not automatic release, with the current configurable ±0.03 band. This is a screening product heuristic and does not alter SP63 or ARBAT's formal limits.
- The ordinary `pile_rebar.py` example selects an ULS-only candidate 12Ø25 for P800 but **does not** select the same cage as a passed combined ULS+SLS candidate: its four SLS checks include three limit exceedances and one indeterminate case. That is correct fail-closed behavior.

No SLS check in this study constitutes an independent engineering acceptance; all final decisions remain `NOT_VERIFIED`.

## First independent ARBAT test batch (9 cases, five geometries)

These nine cases were chosen AFTER reviewing actual solver outputs, to exercise near-boundary results, geometries with working numerical solutions, biaxial bending, partial sustained loads and hard-to-interpret zero fallback states. **All units**: N in kN; moments in kN·m; full (total) and long (sustained) moments are listed separately. See exact bar positions and material parameters in `sls_matrix/manifest.json` and individual `*_input.json` files in the Actions artifact.

| No. | Section | Case | N | Mx total | My total | Mx long | My long | Current CScore K governing / status |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 1 | R300×500, 4Ø20, cover 45 | T01 | 0 | 60 | 0 | 60 | 0 | 1.032 / exceeds |
| 2 | R300×500, 4Ø20, cover 45 | T02 | 0 | 180 | 0 | 180 | 0 | indeterminate |
| 3 | R400×600, 6Ø20, cover 50 | T05 | 0 | 120 | 75 | 120 | 75 | 2.164 / exceeds |
| 4 | R400×600, 6Ø20, cover 50 | T09 | 0 | 160 | 0 | 80 | 0 | 1.303 / exceeds |
| 5 | R500×700, 8Ø25, cover 60 | T05 | 0 | 120 | 75 | 120 | 75 | 0.887 / below |
| 6 | R500×700, 8Ø25, cover 60 | T09 | 0 | 160 | 0 | 80 | 0 | indeterminate |
| 7 | R400×400, 8Ø16, two layers, cover 35 | T03 | 0 | -180 | 0 | -180 | 0 | indeterminate |
| 8 | R400×400, 8Ø16, two layers, cover 35 | T10 | -250 | 140 | 80 | 70 | 40 | 1.510 / exceeds |
| 9 | C600, 8Ø20, cover 50 | T07 | +150 | 130 | 0 | 130 | 0 | 2.127 / exceeds |

**Optional tenth sanity test:** C1000/20Ø28, T02 pure Mx=180 kN·m, N=0 (CScore currently states uncracked and K=0); verify whether ARBAT also predicts an uncracked state.

For all rectangles, ensure the ARBAT application actually permits matching each explicit bar coordinate/layer. The standard rectangular bar arrangement dialog may not reproduce arbitrary bar coordinates: an approximation must NEVER be reported as an independent comparison of identical geometries.

ARRANGEMENT REQUIREMENT: verify sign and axis mappings between CScore input and ARBAT's My/Mz and its positive bending convention against the UI section schematic; do NOT assume `Mx(CScore)=My(ARBAT)` holds for every rectangular section orientation.

ARBAT supports checking two-plane moments under SP63 and **expanded entry of design, normative and long-term normative actions**. Use the expanded dialog, not a single scalar duration coefficient, particularly for T09/T10 with different total/sustained moments. See official help: https://scadsoft.com/help/Arbat/ru/Arbat1049_rtf/ResistRC.htm (pages "Усилия", "Трещиностойкость"). Set material characteristics, crack limits, geometry, static scheme and input force provenance consistently before comparing.

For each independent batch, return **original SAV and full RTF** along with the tested ARBAT version and a brief note of exact material/cover/rebar settings. The existing SAV byte patcher only supports two validated circular eight-load templates; it MUST NOT be applied to rectangle SAV.

## Immediate follow-up implementation priorities

1. Isolate the upstream path where `Cracked=false` above `Mcrc`, especially R300x500 T02 and R500x700 T09, without suppressing any safety guards.
2. Add progress/coverage reporting (e.g. 59/70) separate from green CI, so engineers see incomplete numerical computation.
3. Validate minimum stable rectangular cases and compare nine independent RTF results, including biaxial and partial sustained loads.
4. Extend the SLS service material law and force-combination specification before claiming numeric agreement is normative.

**No arbitrary 1–2% coefficient adjustment will be made merely to reproduce ARBAT.**
