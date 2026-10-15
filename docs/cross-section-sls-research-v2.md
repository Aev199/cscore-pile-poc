# SLS crack-width research: rectangular + circular sections, conservative defaults

**Status: RESEARCH. No new numeric results beyond the previous P800/P1000 ARBAT cases have been obtained yet. This document describes the next experiment, NOT an independently verified design module.**

## Recorded methodological decisions

1. The current CScore author method is an intentional **refined, post-cracking-stress formulation**, not a known error. We MUST NOT fork/revert that method or claim ARBAT is physically more accurate because its number is higher.
2. The preliminary **default** is \`arbat_conservative\`: a reconstruction based on the pre-cracking rebar-stress hypothesis. Its provisional width is \`max(width_precrack_research, width_CScore_native)\` for the equal long/total load configurations where the alternative can be computed. This makes the default **not less conservative than native CScore**, but does NOT make it certified as a replica of ARBAT.
3. If the pre-crack comparison is not applicable or fails, the default temporarily uses an explicit SP63 \`psi_s=1\` initial screening result, also enveloped with native CScore width. This is particularly important for tests with different sustained/total actions, for which the existing pre-crack sensitivity is not directly transferable. If neither conservative path can be calculated, output is \`indeterminate\`, never an implicit pass.
4. \`native_refined\` remains available by the user's EXPLICIT choice. Both native and conservative outputs are retained in the result. Neither is an engineering approval.
5. An experimental, non-normative **engineering review band ±3% around utilization=1.0** prevents premature categorical decisions in this PoC. Hence BOTH \`K=0.989\` and \`K=1.014\` are \`engineering_review\` (NOT final PASS/FAIL). The band is a product-level screening heuristic; it is not a tolerance or relaxation of SP63 or ARBAT. A valid normative result below 1.0 may formally satisfy a particular check but still require broader project verification.
6. ARBAT remains required for independent final verification, including cracking, shear, other actions, members, and detailing.

## Default and explicit alternative

In direct CScore \`sls_probe\` input and optional \`pile_rebar.py\` input:

\`\`\`json
"sls_probe": {
  "preliminary_method": "arbat_conservative",
  "review_band": 0.03,
  "phi2": 0.5,
  "acrc_ult_long_mm": 0.3,
  "acrc_ult_short_mm": 0.4,
  "short_materials": { "...": "complete explicit service diagram" },
  "long_materials": { "...": "complete explicit service diagram" },
  "loads": [
    {
      "id": "SLS-01",
      "n_kn": -200,
      "mx_total_knm": 100, "my_total_knm": 20,
      "mx_long_knm": 50, "my_long_knm": 10
    }
  ]
}
\`\`\`

The pinned \`CrackWidthSolver\` has **a single axial force N for sustained and total parts**. When N_long differs from N_total, this input contract cannot represent the complete problem; do not silently treat N as interchangeable. Our standard input JSON with explicit material parameters is \`examples/verification/P800_sls_preselection_example.json\`.

If the user requests refined native behavior, set \`"preliminary_method": "native_refined"\`; the underlying original CScore numerical result is ALWAYS returned under \`utilization_long\` and \`width_long_mm\` even when conservative preliminary evaluation is selected.

The preliminary output has status \`preliminary_below_limit\`, \`preliminary_exceeds_limit\`, \`engineering_review\`, or \`indeterminate\`. All have \`preliminary_is_final_approval=false\` and \`verified_for_design=false\`. The ordinary \`pile_rebar.py\` does not use this experimental SLS status to automatically accept a reinforcement cage.

## New independent experimental matrix

\`tools/cross_section_sls_matrix.py\` generates **7 sections × 10 load combinations = 70 cases**:

Rectangles (actual rectangle concrete contour and explicit longitudinal bar coordinates):
- \`R300x500_4d20_cover45\` — 300×500mm, four Ø20 bars
- \`R400x600_6d20_cover50\` — 400×600mm, six Ø20 bars including side middles
- \`R500x700_8d25_cover60\` — 500×700mm, eight Ø25 bars distributed along perimeter
- \`R400x400_8d16_two_layers_cover35\` — 400×400mm, eight Ø16 bars with two reinforcement rows near each face

Circular comparative studies, not the original ARBAT reference:
- \`C600_8d20_cover50\`
- \`C800_16d25_cover55\`
- \`C1000_20d28_cover70\`

All stated cover distances are to the OUTER SURFACE OF LONGITUDINAL bars. Local bar coordinates are explicitly listed in \`manifest.json\`. The CScore rectangle validation guards against bars outside concrete faces or mutually overlapping longitudinal bars. Detailed detailing and physical cage feasibility are not certified.

Each section has 10 simultaneous N/M combinations: pure bending, reversed moment, compression, tension, bending about both axes, biaxial bending, low-moment/uncracked, and **partial sustained force**. The reference table is blank until the corresponding independent ARBAT reports are obtained.

Inputs/materials are experimental B30/A500 service diagrams, identical to the earlier crack probe and **not proven to reproduce ARBAT's material model**. Proper independent comparison requires equal concrete/rebar characteristics, crack limits, axial forces, sustained portions, sign conventions, geometry and load history in both programs.

## Running

Manual Windows GitHub Actions workflow \`CScore pile PoC Windows\` publishes:

- \`sls_matrix/manifest.json\` — authoritative source geometry and bar positions;
- \`sls_matrix/<section>_input.json\` and \`<section>_output.json\` — complete engine I/O;
- \`sls_matrix/cross_section_sls_matrix.csv\` — all calculated native, conservative and compatibility values with their statuses;
- \`sls_matrix/arbat_reference_TO_FILL.csv\` — blank independent ARBAT result schedule for engineer preparation.

Experiment can also be run with \`--method native_refined\` to select the author's formulation as primary **without altering either of the computed result sets**:

\`\`\`powershell
python tools/cross_section_sls_matrix.py --runner publish/CScorePileRunner.exe --out results/sls_matrix --method arbat_conservative
\`\`\`

The script verifies 70 complete case identities and that default preliminary utilization never falls below native for any calculated case. An indeterminate case is archived but never converted to an approval.

## Minimal ARBAT test program (suggested)

Rather than ask for 70 manual reports initially, independently calculate these first:

- R300×500/4Ø20: T02, T06 and T07
- R500×700/8Ø25: T05 (biaxial), T09 (partial sustained)
- R400×400/8Ø16 two rows: T03 (reverse bending), T10 (partial sustained biaxial)
- C600/8Ø20: T07 (tension) and T09 (partial sustained)

For each case, export the RTF and the original SAV. We will use the reports to map verified force signs, steel layers, crack limits, material diagrams and durations. **A rectangular ARBAT SAV serializer is NOT available** and cannot safely be inferred from the existing circular SAV templates. Never attempt byte patching of an unknown proprietary layout.

The ultimate goal is to measure absolute crack width in millimeters and pass/fail consistency in independently documented cases, not to adjust a scalar to force 2% agreement.

## Acceptance milestones

- Geometry and force round-trip exact on both sides, including double curvature, layer position and sustained fractions.
- Separate independent numerical references for normal reinforcement and multiple reinforcement rows in rectangular cross sections.
- Native refined option unchanged and reproducible across upgrades.
- Every intermediate cracking/plane solve exposes convergence (the pinned upstream silently substitutes some states; this remains a gap).
- Measurable errors and limitations explicitly documented before any elevation above \`RESEARCH\`.

Original author refinements are recorded in \`docs/crack-arbat-compatibility.md\`.
