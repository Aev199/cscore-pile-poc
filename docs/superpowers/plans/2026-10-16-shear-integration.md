# Integration plan: transverse shear Q for BNS and diaphragm walls

Status: **Stage 1 implemented (local CScore Q screening); full inclined-section verification still open**.
Branch: `feat/section-workbench`. Based on pinned OpenCS commit
`4ae6aa077be5b29145930ec093ba1c9eb7776222`.

## Starting facts

- `workbench/model.py` validates, normalizes and plots `q`, but does **not**
  send it to `section_input` or CScore. The current result is **normal-section
  ULS only**, followed by preliminary, unverified SLS.
- For walls, `wall_rebar.scaled_loads` multiplies N and M from per-metre forces
  by `panel_width_mm / 1000`. Q must be transformed by the **same factor**
  exactly once. Pile Plate/Embedded Beam force-scale handling must apply the
  pile-spacing factor exactly once if input is per metre.
- The pinned upstream already contains `CScore/Sp63Shear`, notably
  `ShearInclinedChecker`, `ShearInclinedInput`, `InclinedSectionGeometryPair`,
  `Sp63ShearApplicability`, `StirrupResolver` and tests.
- Upstream's **automatic normative mode** only covers a single solid
  rectangular concrete section in one shear plane. Curved/circular BNS is
  research-only unless the engineer explicitly approves and documents an
  equivalent-section model with positive `b`; this is an **assumption**, not
  automatic normative verification.
- SP 63.13330.2018 clauses 8.1.31–8.1.35 require more than a spot Q check:
  strip (8.55), inclined shear section (8.56–8.59), N influence (8.1.34)
  and inclined-section moment (8.63–8.65). Stirrups count only where
  the applicable detailing requirements are met (section 10.3).

## Design decisions

1. **Never return global PASS from N/M alone.** Record independent
   `normal_uls`, `shear_uls`, `inclined_moment_uls`, `sls_research`,
   `detailing` statuses (PASS / FAIL / INDETERMINATE / NOT_EVALUATED).
   Keep final `NOT_VERIFIED` until independent acceptance.
2. Preserve full simultaneous (`N,M,Q`) tuples for each load case / phase
   over depth. Do not form a fictitious triple by mixing independent envelopes.
   No interpolation between *different* phases or missing data treated as zero.
3. **Wall**: calculate for the full panel, with `Q_panel = q_per_m *
   panel_width_m`, and a full-width rectangular concrete section. The
   transverse reinforcement resistance follows the *physical* stirrup/link
   arrangement in 1–3 identical cages, with effective branches and spacing,
   NOT the count of longitudinal bars and NOT imaginary uniformly spaced links.
   Verify equivalence/compatibility of physical cage layout and homogenized
   longitudinal bars before claiming a normative result.
4. **BNS**: use forces per pile. Explicitly distinguish closed ties from
   a continuous helix. Do not map spiral pitch to a two-leg stirrup without
   an independently reviewed equivalence, orientation and anchorage check.
   Circular geometry remains unverified until justified and benchmarked.
5. Keep transverse steel input separate from existing `ties_diameter_mm`
   (currently only a geometric envelope around longitudinal bars). Add
   explicit transverse bar **type**, steel class / `Rsw`, diameter, pitch,
   number of effective branches / physical geometry, and different pitches
   over depth. Validate real spacing and anchorage conditions; fail closed if
   construction conditions cannot be confirmed.
6. Examine `Q(z)` along each original load-case profile and the possible
   projection length `C` in the same stage, including support proximity and
   longitudinal bar cutoffs. No unconditional assumption of a simple beam
   support for a geotechnical embedded member. Check sign/units and expected
   `dM/dz` relation before any automatic interpolation of PLAXIS profiles.
   If an exact load/support reconstruction is impossible, return an explicit
   limited screening outcome, **not** a normative PASS.
7. Do **not** upgrade research-only CScore shear, SLS or unverified
   equivalent geometry into design approval merely because `eta < 1`.

## Progress at this commit

- [x] Separate physical transverse link input and explicit opt-in wall Q screening.
- [x] Preserve joint N/M/Q and scale Q exactly once to panel width.
- [x] Route isolated ULS tuples to pinned CScore `ShearInclinedChecker`.
- [x] Fail closed on incomplete results; retain research-only statuses and no final acceptance.
- [x] Workbench Q controls, visible diagram and inspector; browser/contract/real-engine CI tests added.
- [ ] Confirm the new Windows CI run passes; do not claim numerical verification before that.
- [ ] Replace constant profile by trustworthy phase-specific longitudinal profile and support model.
- [ ] Prove cage-link mechanical model, detailing and anchorage; enable zoning / pitch selection.
- [ ] Independent wall benchmarks; circular BNS separately.

## Implementation order

### 1. Data contract and status safety
- Schema v2 / migration from v1; independent `transverse` and zone records.
- Preserve `q` through `section_input`, with correct per-panel/per-pile
  scaling, force-case IDs and sign provenance.
- Add tests for missing Q, zero Q, ±Q, mixed phases, panel width / pile
  spacing exactly-once conversion, wrong units and stale results after edit.
- Make the UI explicit that Q is **currently not checked** before engine
  integration.

### 2. Rectangular walls first
- Create a C# runner adapter for `CScore.Sp63Shear` on the **pinned** engine.
- Check one shear plane, `T = 0`, genuine rectangular section, conservative
  handling of support locations, correct signed moments and axial N.
- Inspect strip, inclined-section shear and inclined moment separately.
- Verify N effects / stirrup eligibility / automatic `b,h0,Ns` calculations
  and physical cage-to-`A_sw/s` mapping.
- If original profile is not reconstructible, report diagnostic screening,
  not `PASS`.

### 3. BNS as a separate acceptance stream
- Verify closed hoops first and review use of an equivalent rectangular
  section against independent circular-section benchmarks.
- Implement real helix semantics and validate applicability separately.
- Show `NOT_APPLICABLE` or `INDETERMINATE`, never an optimistic substitution,
  if circular/spiral rules are not established.

### 4. Joint selection and UI
- Longitudinal N/M screening -> transverse Q design (diameter / link
  configuration / selected pitch zones) -> repeat checks after either is
  manually edited. SLS stays explicitly research-only.
- Provide Q(z), limiting Q capacity / utilization or required transverse
  pitch by depth, accepted transverse pitch zones, and the governing check.
- Report `Q_b`, `Q_sw`, `C_critical`, `phi_n`, `b`, `h0`, `R_sw`,
  actual `s_w`, each check's clause, and explicit missing-verification notes.
- Do not make longitudinal reinforcing steel a substitute for transverse
  steel or vice versa.

### 5. Verification gates
- Pin unchanged upstream SHA. Run existing 105+ Python tests and CScore
  Sp63Shear upstream tests, then new full-stack real-engine tests on Windows.
- Independent hand calculations for walls with concrete-only Q, transverse
  reinforcement required, changes in Q/M sign, N compression/tension,
  panel cage counts 1/2/3, local step changes, support/anchor zones and
  overloaded Q. Circular/spiral benchmarks require separate documented
  acceptance before normalizing their statuses.
- Artifact + browser QA tests demonstrating no global PASS if Q is missing,
  failed, out of scope or indeterminate.

References:
- https://docs.opencs.propgs.ru/calculations/inclined-sections/
- СП 63.13330.2018 §8.1.31–8.1.35 and §10.3, including amendment No. 2.
