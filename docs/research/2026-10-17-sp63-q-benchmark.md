# SP 63.13330: focused transverse-shear benchmark

Date: 2026-10-17
Scope: diaphragm wall as a rectangular strip / panel; preliminary verification only.

## Sources and independent arithmetic

Normative starting point: SP 63.13330.2018, section 8.1.32-8.1.33,
conditions (8.55)-(8.59), and construction limits in section 10.3.
Reference text: https://meganorm.ru/mega_doc/norm/osnovnye-polozheniya/0/sp_63_13330_2018_svod_pravil_betonnye_i_zhelezobetonnye.html

tools/sp63_q_handcheck.py is a deliberately separate, minimal arithmetic
reference using kN, m and kPa. It does not call OpenCS or CScore. Python
examples assert **hard numerical values** for normal shear, weak transverse
reinforcement, no stirrups, inadmissible pitch and concrete/stirrup C caps.

The pinned Windows CScore smoke also independently computes the expected
Qb, Qsw and overall maximum of exact (8.55)/(8.56) utilizations using the
actual reported b, h0, Rb, Rbt, phi_n, qsw, pitch, C and Q. A disagreement
fails the build instead of being silently interpreted as design approval.

## Test cases (b=1 m, h0=0.5 m, Rb=12,000 kPa, Rbt=1,000 kPa,
phi_n=1, C=0.75 m, applied Q=400 kN)

| Input | Qb, kN | Qsw, kN | Notes |
| --- | ---: | ---: | --- |
| qsw=300 kN/m, pitch=200 mm | 500 | 168.75 | Standard branch |
| qsw=100 kN/m, pitch=200 mm | 250 | 56.25 | Alternative low-qsw concrete expression, 250 kN concrete lower cap |
| qsw=0, pitch=200 mm | 500 | 0 | No transverse contribution |
| qsw=300 kN/m, pitch=1000 mm | 500 | 0 | Pitch exceeds Rbt*b*h0^2/Q = 625 mm |

The low-qsw branch is a significant hazard for naive auto-pitch selection:
a smaller qsw affects not only Qsw but may change Qb. Do NOT optimize
pitch by assuming strength is linear in 1/pitch or that any lower qsw
can only change Qsw.

## Remaining gates before automatically selecting transverse pitch

- Verify which calculation model and clauses apply to the actual diaphragm
  wall retaining-work scenario; check strip/panel assumptions and correct
  working direction (not just match published algebra).
- Independently check representative actual project sections, especially
  small/large N and alternating M sign.
- Confirm transverse bar effective legs per physical cage, real anchorage
  and development, fixed boundary/support conditions and joint N/M/Q
  from one PLAXIS phase/combination.
- Clarify treatment of differing pitch within a single inclined projection.
  Current conservative neighborhood approximation is NOT itself a verified
  normative method.
- Check section 8.1.35 inclined moment, section 10.3 detailing, spacing
  restrictions and supports before issuing a design verdict.
- Retain verified_for_design=false and DRAFT_NOT_VERIFIED on reports.
- Only after independent checks use engineer-permitted pitch list to
  generate preliminary proposals, with manual correction and recalculation.

This milestone tests the arithmetic branch and interface to CScore, not
the full physical validity of the shear design method.
