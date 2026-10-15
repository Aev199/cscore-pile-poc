# ARBAT crack-width compatibility research: origin of \`psi_s\` mismatch

Date: 2026-10-15. **RESEARCH ONLY. Neither CScore nor a compatibility hypothesis is yet validated as a complete SLS design checker for bored piles.**

## Reproducible evidence

Reference ARBAT RTF: \`отчет_800_v2.rtf\`, \`отчет_1000_v2.rtf\` (16 cases), values in
\`examples/verification/arbat_matched/arbat_reference_v2_nm8.json\`.

First CScore SLS CI:
- https://github.com/Aev199/cscore-pile-poc/actions/runs/38741879627
- 16/16 research cases computed; 9 RTF cases had a numeric crack reference.
- On those 9, CScore predicted smaller sustained crack widths than ARBAT:
  P800 MAPE=25.4% (six comparisons), P1000 MAPE=32.1% (three).
- Non-conservative disagreement: P800 T07 ARBAT K=1.360 vs
  CScore K=0.695; P1000 T08 ARBAT K=1.508 vs CScore K=0.980.

Second CScore SLS CI (more internal quantities):
- https://github.com/Aev199/cscore-pile-poc/actions/runs/38747007374
- 486/486 upstream targeted tests passed; no runtime errors.
- In all nine comparable cases \`ls=400mm\`: the upper bound of this
  calculation under SP63's crack-spacing expression. **Consequently,
  weighting the effective steel area does not explain the observed
  nine-case difference; neither does the per-rebar-only extra output.**

## What the upstream author actually changed

**Direct developer source evidence, not speculation:**

1. https://github.com/rumata-ap/OpenCS/commit/04c2a9435fe653806389468cb2ff65ddbadc0d4c
   The author changed \`sigma_s,crc\` from the **pre-crack**
   strain plane (concrete in tension) to the **post-crack** plane.
   Commit explains ~22.6MPa -> ~172MPa on another real model.
   \`psi_s=1-0.8*sigma_s,crc/sigma_s\`, therefore larger post-crack
   \`sigma_s,crc\` reduces \`psi_s\` and predicted crack width.
2. https://github.com/rumata-ap/OpenCS/commit/c67638272d90032648d791f4ebffeb30242d6b53
   Introduced weighted tensile reinforcement area \`As_tens=sum(Ai*sigma_i/sigma_max)\`
   to avoid including nearly unstressed bars equally when finding \`ls\`.
   This is an explicitly extra-code engineering refinement for skew bending.
3. https://github.com/rumata-ap/OpenCS/commit/73e009b30275fd26f473847c9726b65bc39907d5
   Adds **non-normative, per-bar crack-width breakdown**. It is not the
   scalar whole-section \`AcrcLong\` currently benchmarked.
4. https://github.com/rumata-ap/OpenCS/commit/088936f37e9f9bd9b517d6ef2ce396eec414014a
   Offers optional \`PsiSMethod.Strain8232\` besides the default
   \`Stress8138\`. We do not silently enable it.
5. Pinned upstream \`CScore.Tests/CrackWidthSolverExample47Tests.cs\` uses
   \`CalcType.N\` for full AND sustained loads under SP63 §6.1.26.
   Our adapter now follows that specific upstream convention.

## Analytical inverse test (not reverse-engineering of ARBAT)

Keep **CScore's** stress \`sigma_s\`, crack spacing \`ls\`, steel modulus
\`Es\`, \`phi_1\`, \`phi_2\`, \`phi_3\` unchanged; find \`psi_required\`
that would yield ARBAT's observed crack-width utilization:

\`psi_implied = psi_CScore * K_ARB / K_CScore\`
\`sigma_s,crc_implied = sigma_s * (1-psi_implied) / 0.8\`

| RTF case | CScore ψs | ψs implied from ARBAT | implied σs,crc, MPa |
|---|---:|---:|---:|
| P800 T01 | 0.414 | 0.844 | 32.8 |
| P800 T02 | 0.803 | 0.946 | 33.7 |
| P800 T04 | 0.880 | 0.944 | 21.8 |
| P800 T06 | 0.875 | 0.924 | 15.5 |
| P800 T07 | 0.461 | 0.902 | 33.2 |
| P800 T08 | 0.710 | 0.952 | 29.9 |
| P1000 T02 | 0.607 | 0.894 | 31.4 |
| P1000 T04 | 0.605 | 0.855 | 23.6 |
| P1000 T08 | 0.591 | 0.910 | 33.4 |

A notably narrow range of ~15–34 MPa aligns qualitatively with the
old ~22.6 MPa *example*, whereas current post-crack \`sigma_s,crc\`
in these runs is ~26–182 MPa. This is a strong hypothesis **but NOT proof**
that ARBAT uses pre-crack steel stress, since we have not measured
ARBAT's \`psi_s\`, \`ls\`, \`sigma_s\`, or material diagrams directly.

## Noninvasive comparison in our adapter

Original upstream post-crack \`CrackWidthSolver\` is left UNMODIFIED.

A **separate research calculation** obtains CScore's own pre-crack
strain plane for the same N/M and derives pre-crack steel stress using
the same steel service material diagram. For *sustained == total*
reference cases only it estimates:

\`psi_pre = clamp(1 - 0.8*sigma_pre/sigma_service, 0.2, 1)\`
\`Acrc_pre_hypothesis = Acrc_upstream * psi_pre/psi_upstream\`

This isolates the effect of changing only the **reference stress
used in ψs**, preserving all other CScore results; it is not claimed
to emulate every ARBAT convention, nor to be more physically correct.
If the pre-crack solution does not reproduce the original \`Mcrc\`
within 0.05kNm, or outputs are missing, it returns an error status.
Normal \`CScore.AcrcLong\` is never overwritten and this comparison
never approves a pile.

Files:
- \`src/CScorePileRunner/Program.cs\`: fields
  \`sigma_rebar_precrack_kpa\`, \`psi_s_precrack_hypothesis\`,
  \`width_long_precrack_hypothesis_mm\`, \`utilization_long_precrack_hypothesis\`.
- \`tools/run_crack_research.py\`: comparison CSV with both raw and
  sensitivity metric, and inferred ARBAT \`psi_s\` / \`sigma_s,crc\`.

**Next:** rerun Windows workflow from the new SHA. Compare the
unmodified and pre-crack variants against the 9 independent ARBAT
results. If the sensitivity test explains the gap, research the
*documented* ARBAT/SCAD definition of \`sigma_s,crc\` before adopting any
compatibility mode beyond diagnostics. No hidden calibrated multiplier
may be introduced. Keep ARBAT final SLS verification mandatory.

## Separate code-permitted conservative screening: ψs=1

SP63.13330.2018 §8.2.15 explicitly permits initially taking
`psi_s=1`. When a section does not satisfy the crack-width limitation
on that check, the code provides for a refined computation of ψs. This
provides a **methodologically defensible preliminary screen**, not an
arbitrarily calibrated multiplier.

Keeping all original CScore terms (σs, ls, Es, φ1/2/3) and changing only
ψs to 1, the *existing run 38747007374* can be analyzed exactly:
`K_psi1 = K_CScore / psi_s_CScore` for cracked records only.

| RTF case | ARBAT Klong | CScore default Klong | SP63 ψs=1 screen Klong |
|---|---:|---:|---:|
| P800 T01 | 0.660 | 0.324 | 0.782 |
| P800 T02 | 2.202 | 1.869 | 2.328 |
| P800 T04 | 1.378 | 1.284 | 1.459 |
| P800 T06 | 0.705 | 0.667 | 0.763 |
| P800 T07 | 1.360 | 0.695 | 1.509 |
| P800 T08 | 2.666 | 1.987 | 2.800 |
| P1000 T02 | 0.989 | 0.671 | 1.106 |
| P1000 T04 | 0.519 | 0.367 | 0.607 |
| P1000 T08 | 1.508 | 0.980 | 1.658 |

Mean absolute relative error for these rows: P800 **9.05%**
(vs original 25.4%) and P1000 **12.92%** (vs original 32.1%).
No false-pass vs ARBAT among these nine cases when using ψs=1, but
one **conservative false-fail** P1000 T02 (1.106 vs 0.989). No guarantee
outside this sample, and none of the values certify SLS compliance
because service material diagrams/hidden solver fallback remain
incompletely validated.

New runner research fields:
- `width_long_psi_one_mm`, `utilization_long_psi_one`
- original upstream result (untouched)
- legacy pre-cracking stress sensitivity result (independent, separate)

The ψs=1 screen is particularly useful for *early candidate ranking*:
if it passes (and stresses/force model validated), it is conservative
relative to the same CScore expression with ψs≤1. If it fails, one
can request the more detailed calculation and ARBAT verification.
**This is not authorization to omit ARBAT in our current workflow.**

Source references:
- https://files.stroyinf.ru/Data2/1/4293732/4293732352.pdf (SP63 §8.2.15)
- https://github.com/rumata-ap/OpenCS/commit/04c2a9435fe653806389468cb2ff65ddbadc0d4c

The new code implementing both research contrasts has not yet run
through GitHub CI: do not report these new output fields as verified.

## Limitations

- The nine ARBAT RTF rows contain only utilization factors, not internal
  steel stress, crack spacing, crack initiation reference or ψs.
- SLS material parameters and the long/short load interpretation in the
  original probed cases remain illustrative approximations.
- CScore's pinned crack solver has silent partial fallback paths;
  returning a number does not imply full convergence.
- A more conservative number is NOT necessarily a more accurate
  solution; matching a commercial product does not independently
  establish mechanical validity.
