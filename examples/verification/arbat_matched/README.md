# ARBAT ↔ CScore — matched section geometry benchmark

**Research only; not suitable for design release.** This folder contains reference data transcribed from the user's original ARBAT 23.1.1.1 RTF reports and confirmed against two original 8-load SAV files. The original SAV/RTF documents are not stored in this repository.

| Item | P800 | P1000 |
|---|---:|---:|
| Circular section diameter | 800 mm | 1000 mm |
| Concrete | B30 | B30 |
| Longitudinal bars | 12Ø25 A500 | 16Ø28 A500 |
| Stirrups | Ø12 A240 @200 | Ø12 A240 @200 |
| ARBAT a1, to longitudinal bar center | 70 mm | 70 mm |
| Exact bar-axis radius matched in CScore | 330 mm | 430 mm |
| CScore `cover_mm` needed for this fixed cage | 45.5 mm | 44 mm |
| ARBAT member length | 3 m | 3 m |
| ARBAT concrete gb1 | 0.9 | 0.9 |

IMPORTANT: CScore `cover_mm` means cover to the outer stirrup face. ARBAT's `a1` is the distance to the longitudinal reinforcement centroid. CScore cage axis radius uses D/2 − cover − stirrup Ø − longitudinal bar Ø/2. The converted 45.5 / 44 mm are for matching *this particular bar diameter* and should not be mistaken for a generally acceptable pile concrete cover.

Cases have matching force values from the ARBAT files, with force convention compression negative. In particular, **P1000_E04 is N=+1500 kN in tension**, unlike the earlier, now-corrected prototype input.

- `P800_nominal.json`, `P1000_nominal.json`: concrete Rb=17 MPa (without reduction).
- `P800_gamma09_sensitivity.json`, `P1000_gamma09_sensitivity.json`: sensitivity run, concrete Rb=15.3 MPa and Rbt=1.035 MPa, modeled as 0.9× the nominal values. This is **NOT a verified reconstruction** of every ARBAT material coefficient or load-duration assumption. Avoid tuning material values to obtain agreement.
- `arbat_reference_20261015.json`: the ARBAT moment/strain/shear/crack and overall utilizations, kept distinct.

CScore runner reports **two different quantities**: original `utilization` from `AllFactor(N,Mx,My)` (which scales all actions) and new `moment_utilization` from `MomentFactor(N,Mx,My)` (which scales moments at fixed axial force). Only the second is a plausible counterpart for ARBAT's “Прочность по предельному моменту сечения”; differences in material diagrams, slenderness treatment, sign/axes and failure model remain possible.

The ARBAT report uses moment `My`; our CScore uses `Mx`. For equally spaced 12 and 16 bars, a 90° rotation maps between identical geometric symmetry axes, but material laws and concrete polygon discretization still require verification.

The ARBAT P800 section has L0/i ≈15 (>14), so second-order member effects may enter; our CScore runner deliberately disables η. P1000 L0/i ≈12, making it a cleaner section-level benchmark.

**DO NOT compare** ARBAT shear, crack-width or overall case utilization to the CScore moment-only result. In P1000_E04 the ARBAT shear-strip report produces an enormous 180266.5 utilization. This value must be investigated, not treated as a stable physically meaningful strength ratio.

The workflow `CScore pile PoC Windows` now calculates the four files and writes `results/arbat_comparison.csv`. No numerical CScore values are asserted here until that workflow is run on this commit.
