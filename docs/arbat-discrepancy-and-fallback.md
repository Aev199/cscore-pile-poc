# ARBAT / CScore — why the results differ (research log, 2026-10-15)

## What has actually been verified

- The user's ORIGINAL eight-loading \`арбат - свая 800.SAV\` and \`арбат - свая 1000.SAV\` match the 16 N/My/Q action tuples in the two ARBAT RTF reports.
- Source sections: B30, A500, Ø800 12Ø25, Ø1000 16Ø28, \`a1=70 mm\`, length 3 m, \`gamma_b1=0.9\`, statically determinate. Program: ARBAT 23.1.1.1.
- Prior CScore benchmark ran at upstream OpenCS commit \`4ae6aa077be5b29145930ec093ba1c9eb7776222\` (no numerical non-convergence).

## Concrete mistake in previous "matched" benchmark

We set the longitudinal bar CENTERS at 70 mm from the outer concrete face.
This interpretation **was not warranted**.

Official ARBAT \`ResistRC\` documentation explicitly distinguishes input modes:
- bar-count/diameter mode: *concrete cover of reinforcement*;
- reinforcement-area mode: *distance to steel area centroid*.

The user's report lists actual bars (\`S1 - 12Ø25\` / \`S1 - 16Ø28\`), so the first interpretation applies.
Official SCAD Soft verification example defines \`a1\` as distance from the BAR EDGE to the outer concrete face.

References:
- https://scadsoft.com/help/Arbat/ru/Arbat1049_rtf/ResistRC.htm
- https://scadsoft.com/tests_arbat/index/test/24
- https://base.garant.ru/72230218/ (SP63 8.1.7 accidental eccentricity)

For a single uniform circular ring:
- Ø800: old r = 400-70 = **330 mm**; corrected bar-cover interpretation r = 400-70-25/2 = **317.5 mm**.
- Ø1000: old r = 500-70 = **430 mm**; corrected r = 500-70-28/2 = **416 mm**.

CScore prototype accepts cover to OUTER STIRRUP and stirrup diameter as geometry inputs; for this comparison, setting cover=58 mm, stirrupØ=12 mm, reproduces the corrected bar center. This is a *coordinate conversion*, not an endorsement of the 58-mm physical outer-stirrup cover for a specific pile construction.

## A second modeling difference: accidental eccentricity

ARBAT reports a statically determinate structural scheme and automatic accidental eccentricity.
SP63.13330 8.1.7 gives minimum ea = max(L0/600, D/30, 10 mm), added to the analysis eccentricity in statically determinate compression.
For length L0=3000 mm:
- Ø800: ea ≈ **26.67 mm**
- Ø1000: ea ≈ **33.33 mm**

Original CScore adapter sends plain N/M (no ea). For diagnostic variants we externally ADD \`sign(M) × |N| × ea\` **only when N<0**. This is a hypothesis to test, not a proven reproduction of all of ARBAT's moment and second-order procedures. Eccentricity effects on coefficient extraction may be nontrivial.

## A third difference: the definition of the reported factor

The CScore runner calculates:
- \`AllFactor(N,Mx,My)\`: scales N and both moments together;
- \`MomentFactor(N,Mx,My)\`: holds N fixed and scales both moments.

We cannot yet prove ARBAT's \`Прочность по предельному моменту сечения\` uses exactly the second definition. In fact, on *the existing old-radius, gamma=0.9 results*:

| Section | Mean absolute relative difference: fixed N | Joint N-M scaling |
|---|---:|---:|
| Ø800, six unique N-M cases | 6.04% | 7.66% |
| Ø1000, six unique N-M cases | 15.48% | 6.47% |

The 8-case tables contain E02/E07/E08 duplicates in N-M (different Q), so the six-unique-case figures are less biased. This does NOT establish that ARBAT uses joint scaling. We need pure-bending and fixed-N sweeps, or a direct description of the ARBAT coefficient algorithm.

## How to run the controlled experiment

Manual GitHub Actions \`CScore pile PoC Windows\` now:
1. Builds CScore from the pinned upstream SHA.
2. Performs original smoke and 486 upstream targeted tests.
3. Recomputes the earlier reference and candidate searches.
4. Runs \`P800_NM8\`, \`P1000_NM8\` with identical N/M to the delivered ARBAT companion diagnostic SAV files.
5. Generates 16 cases of an isolation matrix: diameter {800,1000} × \`a1\` interpretation {to bar surface, prior to bar center} × concrete factor {1.0,0.9} × external accidental eccentricity {off,on}.
6. Publishes detailed result per action, and \`diagnostic_summary.csv\` + \`diagnostic_comparison.csv\` (including both scaling metrics, convergence flags, signed errors and pass/fail mismatches).

All combinations remain exploratory. Picking whichever CScore hypothesis numerically matches ARBAT best is **not normative verification**. Rules/parameters must be independently justified before acceptance.

## The ARBAT fallback boundary

- CScore: fast preliminary ULS normal-section candidate filter. Never call a cage \`approved\` on this check alone.
- ARBAT: final independent verification of ULS, SLS crack widths, shear, slenderness and other report checks (as applicable).
- \`tools/arbat_sav_export.py\` copies one of the two original, SHA256-calibrated eight-loading SAV files and modifies only validated \`N, My, Qz\` numeric triples in exactly 8-record batches. No geometry/material rewrite. SHA mismatch or unknown record layout => hard failure.
- ARBAT calculation requires the licensed ARBAT program; SAV export itself is NOT an automated ARBAT run. User must open, inspect, calculate, save RTF.
- Preserve each concurrent signed N/M/Q action group and load ID. Never assemble independent maximum N, M, Q components.
- Future versions may support arbitrary numbers of load entries **only after** independently validating the record-count/stride/serialization and successfully round-tripping through ARBAT.
- RTF report ingestion and automatic comparison are a future stage; never silently treat a missing/failed/unparseable report as passing.

## Blockers before production use

Verify Rb/E/ε diagrams (including load-duration effects), material coefficients, moment-factor definition, accidental eccentricity and eta, numerical discretization, other code checks, and source/input/report traceability. No automatic working-design release yet.
