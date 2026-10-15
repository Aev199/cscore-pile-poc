# Crack width research for circular bored-pile sections (2026-10-15)

**EXPERIMENTAL / NOT VALIDATED FOR DESIGN.**

This prototype is a separate SLS probe. It does **not** contribute to
\`pile_rebar.py\` ULS pass/fail, never labels a cage as SLS-approved,
and cannot replace a licensed ARBAT calculation.

## Upstream API

Pinned OpenCS SHA: \`4ae6aa077be5b29145930ec093ba1c9eb7776222\`.
\`CScore.CrackWidthSolver\` can work with a general \`CrossSection\` including
circular fiber geometry. By contrast, \`Sp63CrackWidthChecker\` currently
rejects circular sections (it supports rectangular and tee profiles).

The generic solver accepts:
\`Compute(N, mxLong, mxTotal, myLong, myTotal, phi3)\`
plus actual material characteristics for \`CalcType.N\` and \`CalcType.NL\`.

**API limitation:** it accepts **one axial N**, not separate Nlong and Ntotal.
Cases with different sustained and total axial forces cannot be represented by
this probe and must be treated as not applicable without further work.

**Upstream ambiguity:** internal nonconvergence can return an object with a
zero width and \`PassedLong/PassedShort=true\` in some branches. The probe
conservatively treats a failed cracking threshold calculation (\`CrcConverged\`)
and unexplained zero-width result for an over-threshold moment as
\`indeterminate\`. This does not resolve all hidden fallbacks; in particular,
some total/short-term solve failures can silently reuse the long-term strain
plane. The research dataset therefore sets **sustained=total** to avoid that
particular ambiguity. Do **not** interpret the probe as a certified check.

## Independent ARBAT inputs

Original RTFs:
\`отчет_800_v2.rtf\` and \`отчет_1000_v2.rtf\`
(for 8 N/My/Q tuples each; all Q=0).

Two fixed cages:
- 800mm B30 A500, 12Ø25, bar-surface cover a1=70mm.
- 1000mm B30 A500, 16Ø28, bar-surface cover a1=70mm.

Their RTF rows show \`коэффициент длительной части=1\`. **Research assumption**:
the whole displayed N/My is taken as sustained and total SLS loading. Exact
interpretation of ARBAT's load-duration fraction and the design combination
type should still be confirmed by inspecting ARBAT's calculation form.

A missing crack utilization in the RTF reference is recorded as \`null\`,
**not as 0**. Those cases are not quantitatively compared against crack ratios.

**Service materials are also assumptions** and are kept explicitly separate
from ULS values. Current illustrative model is B30/A500 with service
Rb=22MPa, Rbt=1.75MPa, E=32.5GPa, Rs=500MPa, Es=200GPa;
\`CalcType.N\` and \`NL\` are separately supplied but have equal illustrative
values. No creep reduction or other time-dependent effects are claimed.
This is deliberate exploratory input, not a verified reconstruction of
ARBAT's N/NL diagrams and **not** a normative material parameter table.

## How to run

Run the manual \`CScore pile PoC Windows\` GitHub Actions build after this
commit. It publishes a \`crack_probe\` directory in the usual ZIP artifact:

- \`P800_research_input.json\`, \`P1000_research_input.json\`: exact engine
  requests with explicit service materials and total/sustained actions.
- \`P800_research_output.json\`, \`P1000_research_output.json\`: raw CScore
  numerical results, error statuses, convergence, and \`verified_for_design=false\`.
- \`crack_width_comparison.csv\`: 16 input pairs; numerical ARBAT references
  are only populated where reported in the RTF, with comparisons only for
  experimentally computed CScore results. Every row's
  \`design_status=NOT_VERIFIED\`.

A CScore failure is not silently treated as an accepted SLS design. Numerical
deviations must not be interpreted as model quality until the long-term force
split, actual SLS material diagrams and \`CalcType.N/NL\` modes are aligned.

## Recommended next round

1. Review the initial SLS solver response and reason for any indeterminate
   records; do not force convergence by concealing fallback states.
2. Add a proper fail-closed per-intermediate-solver status to the upstream
   \`CrackWidthSolver\` API before production integration.
3. Confirm RTF long/short input interpretation, true service characteristics
   and crack-width limits from the original ARBAT dialog.
4. Compare ARBAT widths in millimeters, not only utilization ratios, including
   multiple circular cages, a non-cracked case and biaxial bending.
5. Keep ARBAT verification mandatory for all final cage designs. Automatic
   crack-width approval is **not implemented**.

See \`docs/upstream-and-crack-width.md\` for upgrade handling.
