#!/usr/bin/env python3
"""Research-only ARBAT vs CScore benchmark comparison.

ARBAT 'limit moment' utilization is compared with CScore's fixed-N MomentFactor,
NOT with AllFactor (which scales N and M simultaneously).
No acceptance threshold is applied: mechanical models and material parameters may differ.
"""
from __future__ import annotations
import argparse
import csv
import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REFERENCE = ROOT / "examples/verification/arbat_matched/arbat_reference_20261015.json"


def compare(reference: Path, results_dir: Path, output: Path) -> list[dict]:
    data = json.loads(reference.read_text(encoding="utf-8"))
    if data.get("schema_version") != 1:
        raise ValueError("Unknown ARBAT reference schema")
    rows = []
    for group, section in data["groups"].items():
        expected = {row["id"]: row for row in section["cases"]}
        radius = section["diameter_mm"] / 2 - section["a1_mm"]
        for variant in ("nominal", "gamma09_sensitivity"):
            path = results_dir / f"{group}_{variant}.json"
            result = json.loads(path.read_text(encoding="utf-8"))
            if result.get("calculation_scope") != "section_uls_only_no_eta_no_shear_no_crack_no_code_detailing":
                raise ValueError(f"Unexpected calculation scope: {path}")
            cases = result.get("cases", [])
            if len(cases) != 1 or abs(cases[0]["bar_axis_radius_mm"] - radius) > 1e-7:
                raise ValueError(f"Unexpected cage geometry in {path}: expected r={radius}")
            candidate = cases[0]
            load_results = {x["id"]: x for x in candidate["loads"]}
            if set(load_results) != set(expected):
                raise ValueError(f"Mismatched set of loading IDs in {path}")
            for load_id, ref in expected.items():
                measured = load_results[load_id]
                c = measured.get("moment_utilization")
                conv = measured.get("moment_converged") is True and measured.get("moment_error") is None
                usable = conv and isinstance(c, (int, float)) and math.isfinite(c) and c >= 0
                measured_moment = float(c) if usable else None
                arb = ref["moment_utilization"]
                rows.append({
                    "group": group, "variant": variant, "load_id": load_id,
                    "N_kN": ref["n_kn"], "ARBAT_My_kNm": ref["my_knm"], "ARBAT_Qz_kN": ref["qz_kn"],
                    "ARBAT_moment_util": arb,
                    "CScore_fixed_N_moment_util": measured_moment,
                    "CScore_all_factor_util": measured.get("utilization"),
                    "difference": measured_moment-arb if usable else None,
                    "relative_difference_pct": 100*(measured_moment-arb)/arb if usable and arb>0 else None,
                    "CScore_moment_converged": conv,
                    "moment_error": measured.get("moment_error"),
                    "ARBAT_concrete_strain_util": ref["concrete_strain_utilization"],
                    "ARBAT_crack_long_util": ref["long_crack_utilization"],
                    "ARBAT_shear_inclined_util": ref["shear_inclined_utilization"],
                    "ARBAT_overall_util": ref["overall_utilization"],
                })
    output.parent.mkdir(parents=True, exist_ok=True)
    if not rows:
        raise ValueError("No comparison data")
    with output.open("w", newline="", encoding="utf-8-sig") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0]), delimiter=";")
        writer.writeheader()
        writer.writerows(rows)
    n = sum(row["CScore_fixed_N_moment_util"] is not None for row in rows)
    if n == 0:
        raise ValueError("All fixed-N moment calculations failed; comparison cannot be completed")
    print(f"ARBAT comparison rows: {len(rows)}, converged comparable moment values: {n}")
    print("RESEARCH ONLY: different second-order treatment/material laws may explain discrepancies.")
    return rows


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--reference", type=Path, default=REFERENCE)
    ap.add_argument("--results-dir", type=Path, default=ROOT / "results")
    ap.add_argument("--output", type=Path, default=ROOT / "results/arbat_comparison.csv")
    a = ap.parse_args()
    compare(a.reference, a.results_dir, a.output)


if __name__ == "__main__":
    main()
