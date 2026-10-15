# Independent CScore / ARBAT verification, second test batch (2026-10-15)

Status: **research prototype**. CScore checks only nonlinear normal-section ULS strength. ARBAT remains a required independent final verification tool, including shear and crack width.

## Inputs and evidence

- Original user-provided RTF reports: `отчет_800.rtf`, `отчет_1000.rtf`, `отчет_800_v2.rtf`, `отчет_1000_v2.rtf`.
- Original eight-load SAV templates `арбат - свая 800.SAV` and `арбат - свая 1000.SAV` are NOT committed.
- Confirmed sections: 800mm / 12Ø25 A500 and 1000mm / 16Ø28 A500, B30, `a1=70 mm`, `γb1=0.9`, length 3m.
- CScore source pinned to OpenCS commit `4ae6aa077be5b29145930ec093ba1c9eb7776222`.
- Successful GitHub Actions run: https://github.com/Aev199/cscore-pile-poc/actions/runs/38708463408

## Root cause of the original 5–15% discrepancy

There were *multiple non-equivalent input and measurement assumptions*:

1. **Concrete cover**. ARBAT bar-count/diameter mode uses `a1` as bar-surface concrete cover, **not** a bar-center offset. Correct center radii are 317.5mm for Ø800 (25mm bars) and 416mm for Ø1000 (28mm bars), not 330/430mm. In the current CScore geometry contract `cover_mm=58` and `ties_diameter_mm=12` reproduces these radii for the fixed cages; this conversion is not a normative detailing check.
2. **Concrete material factor**. ARBAT reports `γb1=0.9`. A CScore sensitivity model uses `Rb=0.9×17MPa` and `Rbt=0.9×1.15MPa`. This is only a verified *numeric sensitivity comparison*, not a claim that all normative constitutive diagrams are identical.
3. **Accidental eccentricity**. For a statically determinate compression member, ARBAT's own manual specifies `e0=e+ea`. For 3m elements under SP63, the diagnostic approximation `ea=max(l0/600, D/30, 10mm)` gives 26.667/33.333mm. CScore runner does not add this automatically. Diagnostic runs add `sign(M)×|N|×ea` to moments only for `N<0`.
4. **Measure of usage**. CScore `MomentFactor` scales M at fixed N; `AllFactor` scales N and M jointly, preserving `M/N`. The ARBAT help describes interaction-domain boundary searches **at a fixed ratio M/N along the radial line from the origin**. This supports using CScore `AllFactor` as the appropriate comparison metric for ARBAT's factor in these cases; it is not interchangeable with `MomentFactor`.

Official reference:
- https://scadsoft.com/help/Arbat/ru/Arbat1049_rtf/ResistRC.htm
- https://scadsoft.com/help/Arbat/ru/Arbat1049_rtf/About.htm

## Existing eight-loading comparisons (old load batch, corrected hypotheses)

At corrected bar-surface cover + CScore `γ=0.9` + additive `ea` + **AllFactor**, the mean absolute relative difference across eight ARBAT RTF load rows is:
- P800: **0.456%**, maximum deviation **0.915%**.
- P1000: **0.616%**, maximum deviation **0.810%**.

All 16 comparisons converge, with no discrepancy in the normal-section pass/fail verdict. There are three identical N/M force pairs in each eight-row batch, so they do not represent 16 unique N–M problems. This is close agreement for *these examples*, not general verification of all RC design cases.

## Independent eight-loading v2 reports (pure bending, compression, tension)

Reference JSON: `examples/verification/arbat_matched/arbat_reference_v2_nm8.json`.
CScore numerical outputs from run 38708463408: `P800_NM8.json`, `P1000_NM8.json`.

Using the same correct bar positions + 0.9 material factor but **without accidentally added ea**, mean deviations for **AllFactor** are:

| Axial condition | P800 | P1000 |
|---|---:|---:|
| Pure bending N=0 | 0.482% | 0.613% |
| Compression N<0 | 8.143% | 7.697% |
| Tension N=+500kN | 0.457% | 0.284% |

The segregation strongly supports eccentricity treatment as the principal cause of compression-only discrepancies.

**A critical limit case**: P800 T08 (N=+500kN, My=+600kNm) has ARBAT K=**0.998**, while unmodified CScore AllFactor K=**1.00295**. The absolute difference is small (≈0.50% relative) but the pass/fail classification flips. Small numerical agreement **is not** a reliable automatic approval rule near K=1.

A further independent test to confirm the compression-eccentricity hypothesis is now scripted: `tools/arbat_v2_benchmark.py`. It produces separate `P800_NM8_ea.json` and `P1000_NM8_ea.json` without altering the original loads, then compares both methods against the new RTF reference in `arbat_v2_comparison.csv`. **This new numerical run has not yet been executed** and must not be counted as verified.

## Independent v2 confirmation: GitHub Actions run 38716913205

Successful run: https://github.com/Aev199/cscore-pile-poc/actions/runs/38716913205
Pinned CScore commit unchanged. 486 targeted upstream tests passed. Separate ARBAT v2 cross-validation step completed successfully and generated `arbat_v2_comparison.csv`.

Using the **independent eight-case v2 reports** and comparing with CScore **AllFactor**, with correct bar placement, concrete 0.9 factor and accidental eccentricity added for compression:

| 8-case comparison | P800 | P1000 |
|---|---:|---:|
| Mean absolute relative deviation, without accidental eccentricity | 4.306% | 4.072% |
| Mean absolute relative deviation, with accidental eccentricity | **0.408%** | **0.515%** |
| Maximum individual deviation with accidental eccentricity | 0.825% | 0.958% |
| Other pass/fail differences outside near-boundary cases | 0 | 0 |

This reproduces the independently prepared initial test batch's sub-1% agreement, strongly supporting the corrected geometry, concrete coefficient and ARBAT's `N/M` radial-load and accidental-eccentricity handling **for these examples**.

**Remaining critical disagreement:** `P800_T08`, combined tensile `N=+500 kN` and `M=600 kN·m`, gives ARBAT `K=0.998` (pass by moment) and CScore `K=1.002953` (fail by moment). It is within about 0.5% relative but changes the verdict. ARBAT also reports long-term crack utilization `2.666` here, so the overall design FAILS. This must not be reported as an approved cage.

`tools/arbat_v2_benchmark.py` now checks the 16 reference comparisons against a 2% **regression tolerance**, but that threshold is **not** an engineering acceptance criterion. It also prints near-unity cases for mandatory review. The guard was committed **after** the successful workflow run and still needs its own CI validation.

## Product implications for safety

1. Apply documented SP63 accidental eccentricity treatment to routine CScore candidate calculations before treating them as even preliminary ULS predictions. The current ordinary `pile_rebar.py` inputs and `pass_uls` field still do **not** implement the corrected external `ea` transform by default.
2. Keep original and transformed signed N–M actions visible to audit; a transformed-case evaluation must not overwrite source PLAXIS loads.
3. Near unity, do not auto-accept a preliminary result based on CScore verdict. ARBAT is mandatory for final design anyway, including shear, cracking and second-order effects.
4. Archive RTF/SAV input hashes and check every load case against its corresponding ARBAT report before labeling the final result valid.

## Product decision

- Use CScore for **preliminary** cage candidate ranking only, with convergence/solver errors as indeterminate.
- Retain all signed concurrent N/My/Q tuples, their case identifiers, cross-section parameters and hash/audit trail.
- Use ARBAT SAV exporter only with SHA256-checked original templates, eight concurrent action groups per SAV, no geometry/material byte modifications.
- ARBAT licensed calculation and complete machine-verifiable RTF report remain required for **all final accepted cages**, not only numerically uncertain cases, because CScore cannot yet verify shear, cracking, constructive details, second-order effects and material assumptions.
- Mark near-boundary factors (e.g. |K−1| <= 0.02) for explicit engineering attention; this is a *review heuristic*, not a certified safety margin. Never interpret CScore's `pass_uls` as a complete project approval.

## Next steps

1. Run manual Windows GitHub Actions after the new `arbat_v2_benchmark.py` change. The workflow computes the corrected-ea v2 cases and writes a quantitative four-way comparison.
2. Investigate any remaining discrepancy at K≈1 and variation with section polygon/discretization.
3. Introduce report ingestion for ARBAT (RTF) and strict case-by-case result completeness gating; no unverified result may be upgraded to PASS.
4. Extend reliable SAV serialization to arbitrary case counts only after round-trip verification.
5. Extend independent validation to biaxial N–Mx–My, other concrete/rebar classes, second-order effects and SLS/shear behavior.
