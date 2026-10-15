# ARBAT SAV fallback (strict eight-load export)

This exports **input files only**. ARBAT must be run by an engineer to obtain results.

Supported original templates (do not use copies modified in ARBAT):
- \`арбат - свая 800.SAV\` SHA256 \`91207f399fc84253486ff233814565c7fd2ba79a080fd07e97685ad0051563aa\`
- \`арбат - свая 1000.SAV\` SHA256 \`5df6fd111716d53ef90690f6636fc7413d8ac55cef68325fb0b345a85cc1978e\`

Expected CSV (semicolon separator, UTF-8):
\`case_id;N_kN;My_kNm;Qz_kN\`, exactly 8 or an integer multiple of 8 **signed concurrent action tuples**. Do not assemble independent component maxima.

Example in a Windows terminal from repository root, with the original SAV file placed in the working directory:

\`\`\`powershell
python tools/arbat_sav_export.py --template "арбат - свая 800.SAV" --forces examples/arbat_fallback/P800_forces.csv --out generated_arbat/P800
python tools/arbat_sav_export.py --template "арбат - свая 1000.SAV" --forces examples/arbat_fallback/P1000_forces.csv --out generated_arbat/P1000
\`\`\`

Never run twice into the same output directory. SAVs and manifest are not overwritten. An unknown source layout or original SHA refuses to run. File output is checked by reading back every value and checking the changed-byte allowlist.

Manually open each SAV in licensed ARBAT, verify geometry/materials and all 8 forces, calculate, and export complete RTF. After independent verification of the RTF parser, the final workflow can join reports to their exact case IDs.

New diagnostic SAV files with N-only / M-only experiments were provided separately; they retain the original eight-load templates and change only N, My and Qz. See \`docs/arbat-discrepancy-and-fallback.md\`.

**Passing the current CScore ULS check is not sufficient to release working drawings.** Other ULS/SLS/design requirements remain unverified until ARBAT reports are reviewed.
