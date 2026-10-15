#!/usr/bin/env python3
"""Prepare ARBAT fallbacks from *original, verified* eight-load ResistRC SAV templates.

This script never executes ARBAT and never claims a design is approved.
Only N, My and Qz are patched; all other source bytes are held invariant.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import struct
from pathlib import Path

MAGIC = b"ArbatResistRCMode\0"
SIZE = 65837
COUNT_AT = 8245
LOADS = 8
FIRST = 8249
STRIDE = 1364
UNIT_SCALE = 1000.0  # Verified against user-supplied 8-load reports.
CALIBRATED_SHA = {
    "91207f399fc84253486ff233814565c7fd2ba79a080fd07e97685ad0051563aa": "P800",
    "5df6fd111716d53ef90690f6636fc7413d8ac55cef68325fb0b345a85cc1978e": "P1000",
}


def digest(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def validate_layout(raw: bytes) -> None:
    if len(raw) != SIZE or not raw.startswith(MAGIC):
        raise ValueError("Only calibrated ArbatResistRCMode 8-load SAV format is supported")
    if struct.unpack_from("<I", raw, COUNT_AT)[0] != LOADS:
        raise ValueError("SAV must contain precisely 8 loads")


def validate_reference_template(raw: bytes) -> str:
    validate_layout(raw)
    sha = digest(raw)
    if sha not in CALIBRATED_SHA:
        raise ValueError(
            f"Template SHA256 {sha} is not one of the two verified original P800/P1000 SAVs. "
            "Do not modify arbitrary proprietary binary layouts."
        )
    return CALIBRATED_SHA[sha]


def load_rows(path: Path) -> list[dict]:
    with path.open(encoding="utf-8-sig", newline="") as f:
        data = list(csv.DictReader(f, delimiter=";"))
    if not data or len(data) % LOADS:
        raise ValueError("Number of force rows must be a nonzero multiple of 8")
    identifiers = set()
    result = []
    for row in data:
        case_id = (row.get("case_id") or "").strip()
        if not case_id or case_id in identifiers:
            raise ValueError("Case identifiers must be unique and non-empty")
        identifiers.add(case_id)
        values = [float(row[key]) for key in ("N_kN", "My_kNm", "Qz_kN")]
        if any(not math.isfinite(x) or abs(x) > 1e7 for x in values):
            raise ValueError(f"Invalid forces for {case_id}")
        result.append(dict(case_id=case_id, N_kN=values[0], My_kNm=values[1], Qz_kN=values[2]))
    return result


def patch_eight(raw: bytes, rows: list[dict]) -> bytes:
    validate_layout(raw)
    if len(rows) != LOADS:
        raise ValueError("Expected exactly 8 combined (N, My, Qz) action tuples")
    out = bytearray(raw)
    allowed = set()
    for idx, record in enumerate(rows):
        values = tuple(float(record[k]) for k in ("N_kN", "My_kNm", "Qz_kN"))
        if not all(math.isfinite(x) and abs(x) <= 1e7 for x in values):
            raise ValueError(f"Nonfinite/out-of-range action in row {idx}")
        pos = FIRST + idx * STRIDE
        struct.pack_into("<ddd", out, pos, *(v * UNIT_SCALE for v in values))
        allowed.update(range(pos, pos + 24))
    modified = [i for i, (a, b) in enumerate(zip(raw, out)) if a != b]
    if len(out) != len(raw) or any(i not in allowed for i in modified):
        raise AssertionError("Undocumented bytes were altered")
    for idx, record in enumerate(rows):
        values = struct.unpack_from("<ddd", out, FIRST + idx * STRIDE)
        for k, actual in zip(("N_kN", "My_kNm", "Qz_kN"), values):
            if not math.isclose(actual / UNIT_SCALE, float(record[k]), rel_tol=1e-13, abs_tol=1e-8):
                raise AssertionError("SAV readback disagrees with requested actions")
    return bytes(out)


def export(template: Path, forces: Path, output_dir: Path) -> dict:
    original = template.read_bytes()
    group = validate_reference_template(original)
    rows = load_rows(forces)
    output_dir.mkdir(parents=True, exist_ok=True)
    batches = []
    for i in range(0, len(rows), LOADS):
        segment = rows[i:i + LOADS]
        target = output_dir / f"{group}_batch_{i // LOADS + 1:03d}.SAV"
        if target.exists():
            raise FileExistsError(f"Will not overwrite existing SAV: {target}")
        buf = patch_eight(original, segment)
        with target.open("xb") as stream:
            stream.write(buf)
        batches.append({"sav": target.name, "sha256": digest(buf), "cases": segment})
    manifest = {
        "source_sha256": digest(original),
        "source_mode": "ArbatResistRCMode",
        "source_section": group,
        "source_file": str(template),
        "forces_file": str(forces),
        "batches": batches,
        "status": "INPUT_GENERATED_ONLY",
        "next_action": "Open each SAV in licensed ARBAT, verify inputs, calculate and save RTF. "
                       "No automatic verification or engineering approval is implied.",
    }
    output_manifest = output_dir / f"{group}_manifest.json"
    if output_manifest.exists():
        raise FileExistsError(f"Will not overwrite existing manifest: {output_manifest}")
    with output_manifest.open("x", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
    return manifest


def main() -> None:
    cli = argparse.ArgumentParser()
    cli.add_argument("--template", type=Path, required=True, help="One of the two ORIGINAL P800 or P1000 SAV files")
    cli.add_argument("--forces", type=Path, required=True, help="Semicolon CSV: case_id;N_kN;My_kNm;Qz_kN")
    cli.add_argument("--out", type=Path, required=True)
    args = cli.parse_args()
    res = export(args.template, args.forces, args.out)
    print(f"Prepared {len(res['batches'])} SAV batches for ARBAT manual verification. No analysis performed.")


if __name__ == "__main__":
    main()
