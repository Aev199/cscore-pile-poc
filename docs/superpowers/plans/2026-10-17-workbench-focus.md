# Workbench direction: practical KZh first (2026-10-17)

## Implemented

- [x] Keep the depth-wise N/M/Q forces, longitudinal background, strengthening zones and manual edits as the main workflow.
- [x] Expose transverse diameter, pitch, allowed typical steps and pitch zones by default.
- [x] Keep research Q calculations, M/Q axis diagnostics and per-bar positions behind closed advanced controls. No automatic background diagnostics on routine changes.
- [x] Export a DRAFT_NOT_VERIFIED CSV of merged reinforcement intervals, split at changes in longitudinal or transverse reinforcement.
- [x] Maintain compatibility with previous projects, CScore and PLAXIS imports.
- [x] Add Python/API and browser tests.
- [x] Windows CI #39047191930 (SHA 434b109): 152 Python tests, real CScore wall/pile smoke, browser QA and Windows archive all succeeded.
- [x] Begin independent numeric shear audit: separate SP63 (8.55)-(8.59) reference formulas, fixed-value cases, and cross-check of CScore Qb/Qsw/eta in Windows smoke.
- [x] Windows CI #39110944191 (SHA a02f256): 157 Python tests and CScore 8.55-8.59 numerical cross-check passed, browser QA and archive passed.
- [x] First KZh output beyond CSV: 1:1 DXF R2000 **draft wall elevation** with actual cage bounding geometry, wall-depth reinforcement zone boundaries, layers and a per-zone text schedule; marks unapproved scope.
- [x] Add validation tests for DXF entity geometry, wall dimensions, one/three cages, height coordinates and HTTP/browser export contract.
- [ ] New DXF export additions are not yet Windows CI verified.

## Next gates in strict order

1. Validate the existing CScore shear methodology against independent, reproducible SP63 calculations for diaphragm walls. Address supports, applicability, shear both directions, variable pitch, detail/anchorage and inclined-moment checks. Do not treat research ratios as design passes.
2. Only then implement automatic step selection from the engineer's permitted pitch list, merge neighboring equal-pitch zones, permit manual override, and recheck the selected construction. No formula interpolation or heuristic capacity assumptions.
3. Generate KZh sections, actual cage detailing, bar marks, bar lengths and specifications from the reviewed interval schedule. Current CSV is draft input only.
4. Handle BNS circular shear as a separately verified task; do not silently reuse rectangular-wall assumptions.

## Completion checks

- Regression tests for N/M, SLS, Q and PLAXIS; browser user flow; real CScore Windows smoke.
- Independent numerical benchmarks and engineering review before any normative approval.
- Avoid additional per-bar or per-projection features without evidence from real design cases.
