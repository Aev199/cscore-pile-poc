# Complete the native CScore connection cycle

User scope: finish engine integration first; do not alter selection ranking,
reinforcement layout or invent engineering formulas. Pinned OpenCS stays unchanged.

## Contract

Add an explicit check of adopted sections, independent of selection. For each
station/candidate, the runner returns a versioned list of native checks with
status, source method, basis, native details, and a reason for missing/not-applicable
checks. The report must never equate missing data or research results with design
approval. Existing ULS -> SLS gating stays in force.

Checks: N/M normal strength; crack formation; long/short crack width; strip and
inclined Q; inclined-section moment; second-order/member check. Anchorage and
constructive detailing remain explicitly unassessed (reinforcement stage deferred).
Native automatic shear qualification rejects circular BNS; no equivalent rectangle
or spiral-to-tie conversion is invented.

## Implementation

1. Add Python request/result contract tests. Require all check IDs/case identities,
   finite native ratios for calculated checks, explicit reasons for other states,
   correct method provenance, and no final design approval. Run failing tests.
2. Add C# ConnectionReport.cs and optional request DTO. Map existing native NDM/SLS
   results; call native shear checker with CheckMoment=true only with confirmed
   support distance and anchorage data. Call native MomentFactor with eta parameters
   only with explicit effective lengths/duration and accounted-for eccentricity.
   Member checks remain separate from the existing NDM selection result.
3. Add model.check_adopted and async HTTP endpoint, passing original joint loads,
   correct width/pile scaling and adopted sections. Do not change acceptance logic.
4. Add a compact context dialog and native result list in the existing inspector.
   Save context; invalidate results after edits. Support distance is per station,
   not one invented support for the entire wall. No automatic declaration of ends
   as supports. Profile screen remains scoped independently.
5. Add native smoke tests: wall/pile, signs, Q failure, supported/unsupported BNS,
   SLS missing/skipped/indeterminate, inclined moment and missing context, eta and
   missing lengths, JSON protocol mismatch. Run build, Python tests, relevant
   upstream tests and real engine checks. Request independent review.
6. Document native capability boundary and publish verified source to the existing
   authorized GitHub branch. Windows/browser verification must be reported honestly.

## Completion evidence

Implemented in this branch. .NET Release build and 167 Python tests pass;
189 relevant pinned upstream tests pass. Existing wall/workbench real-engine
smokes still pass (wall 1m/3m ULS utilization both 0.16540518).
Native connection smoke covers supports in each direction, zero/missing supports,
SLS-only instability and partial directional failure preservation. Browser QA
with actual runner covers the new dialog, persistence, complete HTTP/native
path and stale-report removal after a failed rerun. Independent review findings
were reproduced as failing cases and fixed; focused re-review found no blockers.
Windows execution remains a separate workflow verification.
