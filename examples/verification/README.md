# Independent pile section verification scenarios

These JSON files are **experimental design examples**, not real PLAXIS results.

- `P800_reference.json`: Ø800 mm, B30, A500, **12Ø25** fixed.
- `P1000_reference.json`: Ø1000 mm, B30, A500, **16Ø28** fixed.
- `P800_search.json` and `P1000_search.json`: candidate reinforcement variations.

Each contains eight **joint** N/Mx loading pairs for ULS (compression negative as used by CScore).
There are no Q values in this runner, and the CScore numerical check covers **only normal-section strength without second-order effects**. Crack width, shear, material application factors, anchorage and detailing are outside this prototype.

A separately delivered ARBAT SAV package includes matching N/M/Q action values but is **not yet a matched numerical reference**: ARBAT reinforcement, section geometry and material parameters need manual confirmation. Do not compare numeric utilization coefficients until the section and modeling assumptions are identical and saved SAV files are validated in ARBAT.

Run the manual Windows CI workflow. It archives both reference and reinforcement-sweep results as JSON and CSV in the Windows artifact.
