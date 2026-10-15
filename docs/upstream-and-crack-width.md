# Upstream CScore: update policy and crack-width capabilities

## Version control

OpenCS source: https://github.com/rumata-ap/OpenCS
Upstream default branch: master
Pinned production-test source: config/opencs.lock.json

The Windows build reads the SHA in the lock, checks out that exact revision,
runs upstream tests, builds the CScore adapter and runs the independent ARBAT
numeric regression suite. There is deliberately no "floating latest" build.

Lightweight workflow .github/workflows/watch-opencs.yml is scheduled weekly
(Tuesday at 06:18 UTC) and can be dispatched manually. It compares the locked
revision against OpenCS/master and filters CScore/, CScore.Tests/, CSmath/,
CSTriangulation/. If relevant changes are found, the workflow creates or
updates ONE open GitHub Issue for manual review; it does NOT push code, rewrite
the lock, merge PRs or spend Windows build minutes.

### Safe upgrade sequence

1. Inspect upstream changes in CScore and transitive source projects, including
   material law, convergence handling, signs/units and external API changes.
2. On a separate GitHub branch set config/opencs.lock.json to a chosen upstream
   commit. Update engine_commit provenance in pile_rebar.py and the C# runner,
   maintaining code/test consistency.
3. Manually run Windows build from this branch, including CScore upstream
   tests, Python contract tests, CScore numerical smoke and independent
   P800/P1000 reference comparisons. Never assume upstream tests alone prove
   equivalence to ARBAT.
4. Examine arbat_v2_comparison.csv and every edge case: notably P800 T08
   near unity, where ARBAT and CScore disagree by ~0.5% relative.
5. If new features are enabled (e.g. SLS/cracks), add corresponding independent
   ARBAT reference cases before merging.
6. Have an engineer review numerical deltas. Merge only after explicit
   approval. Preserve rollback to the previous pinned SHA.

Note: the GitHub Issues feature and Actions token must permit issue creation.
If issues are disabled or GitHub Actions has restricted permissions, the watch
job will fail visibly; it must NOT silently claim tracking is configured.

## What upstream actually supports for SP63 crack width

Pinned OpenCS at 4ae6aa077be5b29145930ec093ba1c9eb7776222 contains:

- CScore/CrackWidthSolver.cs — general crack-width solver using CrossSection,
  with N, MxLong, MxTotal, MyLong, MyTotal and service material states.
  It returns AcrcLong / AcrcShort, utilization and other physical outputs.
  CrossSection is not restricted to rectangles. A circular pile may be
  constructed using the same section geometry used for ULS.
- CScore/CrackingSolver.cs — moment of cracking / strain-plane calculation.
- CScore/Sp63/CrackWidth/Sp63CrackWidthChecker.cs — formula-based
  check limited explicitly to Rectangular and Tee section kinds;
  circular sections are not applicable.

**Important safety limitations in the pinned general solver**:

1. CrackWidthSolver.Compute creates an uncracked "zero result", whose
   PassedLong and PassedShort default to true; if cracking plane is unavailable,
   SolveDamped fails, tensile reinforcement properties are missing, or other
   intermediate solves fail, execution can return this zero object without
   a distinct fatal/nonconverged status. Checking "PassedLong" alone is UNSAFE.
2. Even if a long-term/total solve does not converge, current code may substitute
   the long-term plane for the total plane (likewise for the crack threshold
   plane). Convergence is therefore NOT fully exposed as an independent
   status bit at this API boundary.
3. This PoC builds CalcType.N/NL material diagrams as placeholder copies of
   short-term CalcType.C solely because CScore currently builds all four
   diagrams eagerly. They are not real service material properties: using
   these placeholders for SLS would be methodologically wrong.
4. Proper SLS requires distinct long-term/short-term load components,
   actual service resistances/diagrams, crack-width limits, and testing of
   cover/bar properties against the engineering problem, plus biaxial tests.

### Recommended implementation plan

- First: add a fail-closed crack-width result contract to CScore upstream or
  our pinned fork/adapter that exposes convergence for EACH solve, plus
  explicit applicability and failure reasons. Never silently turn "not computed"
  into width 0.
- Second: implement physically justified CalcType.N/NL characteristics and
  explicit SLS force combinations; do not reuse ULS design values.
- Third: use the circular CrossSection and run the full solver on RTF benchmarks
  with P800/P1000, both cracked/uncracked and high utilizations, independent
  long-term/short-term load components, and cases near Wlim.
- Fourth: only then display a qualified crack result in the cage-search UI.
  All unverified SLS runs must remain indeterminate and go to ARBAT.

The presence of code does not mean numerical validity on all circular and
biaxial cases has been established. ARBAT remains required for final release.
