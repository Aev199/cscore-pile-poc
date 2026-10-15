#!/usr/bin/env python3
"""ARBAT v2 independent 8-case N–M benchmark.

ARBAT RTF values are a manually transcribed independent reference.
The calculation of a given candidate is always delegated to the pinned CScore.
This module does not implement RC mechanics or approve an engineering design.
"""
from __future__ import annotations

import argparse
import csv
import json
import math
import statistics
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REFERENCE = ROOT / "examples/verification/arbat_matched/arbat_reference_v2_nm8.json"
INPUTS = ROOT / "examples/verification/diagnostic_forces"


def corrected_inputs(output_dir: Path, input_dir: Path = INPUTS) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    for diameter in (800, 1000):
        path = input_dir / f"P{diameter}_NM8.json"
        data = json.loads(path.read_text(encoding="utf-8"))
        assert data["pile"]["diameter_mm"] == diameter
        # Only for statically determinate members under COMPRESSION, based on
        # SP63 8.1.7 as interpreted in ARBAT help. Never used for tensile axial load.
        l0_mm = 3000.0
        ea_mm = max(l0_mm / 600, diameter / 30, 10)
        for load in data["loads"]:
            n, m = load["n_kn"], load["mx_knm"]
            if n < 0:
                load["mx_knm"] = m + math.copysign(abs(n) * ea_mm / 1000.0, m if m != 0 else 1.0)
        target = output_dir / f"P{diameter}_NM8_ea.json"
        target.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def comparison(results_dir: Path, output_csv: Path) -> list[dict]:
    ref = json.loads(REFERENCE.read_text(encoding="utf-8"))
    if ref["schema_version"] != 1:
        raise ValueError("Unrecognized ARBAT reference")
    rows = []
    for section, data in ref["groups"].items():
        expected = {c["id"]: c for c in data["cases"]}
        for hypothesis in ("as_given", "plus_ea"):
            case_suffix = "" if hypothesis == "as_given" else "_ea"
            source = results_dir / f"{section}_NM8{case_suffix}.json"
            calc = json.loads(source.read_text(encoding="utf-8"))
            candidates = calc["cases"]
            if len(candidates) != 1:
                raise ValueError(f"{source}: expected one longitudinal cage")
            computed = {v["id"]: v for v in candidates[0]["loads"]}
            if len(computed) != 8 or set(computed) != set(expected):
                raise ValueError(f"{source}: missing/duplicated force tuples")
            for case_id, target in expected.items():
                case = computed[case_id]
                for metric, col, cv, ev in (
                    ("joint_NM", "utilization", "converged", "error"),
                    ("fixed_N", "moment_utilization", "moment_converged", "moment_error"),
                ):
                    value = case.get(col)
                    ok = case.get(cv) is True and case.get(ev) is None \
                        and isinstance(value, (int, float)) and math.isfinite(value) and value >= 0
                    arbat = target["arbat_moment_util"]
                    if not ok:
                        raise ValueError(f"{source}: nonconvergence at {case_id}/{metric}: {case.get(ev)}")
                    rows.append({
                        "section": section, "case_id": case_id, "hypothesis": hypothesis,
                        "metric": metric, "N_kN": target["n_kn"], "My_kNm": target["my_knm"],
                        "N_class": "tension" if target["n_kn"] > 0 else
                                   "compression" if target["n_kn"] < 0 else "pure_bending",
                        "ARBAT_moment_factor": arbat, "CScore_factor": value,
                        "abs_relative_error_pct": 100 * abs(value / arbat - 1),
                        "signed_relative_error_pct": 100 * (value / arbat - 1),
                        "different_pass_fail_verdict": (arbat <= 1) != (value <= 1),
                        "boundary_2pct": abs(arbat - 1) <= .02 or abs(value - 1) <= .02,
                        "ARBAT_long_crack_factor": target["arbat_crack_long_util"],
                    })
    if len(rows) != 64:
        raise ValueError(f"Expected 64 rows, got {len(rows)}")
    output_csv.parent.mkdir(parents=True, exist_ok=True)
    with output_csv.open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0]), delimiter=";")
        writer.writeheader()
        writer.writerows(rows)
    print("Independent ARBAT v2 comparison, mean absolute relative deviations:")
    for section in ("P800", "P1000"):
        for hypothesis in ("as_given", "plus_ea"):
            for metric in ("joint_NM", "fixed_N"):
                filtered = [r for r in rows if (r["section"], r["hypothesis"], r["metric"]) ==
                            (section, hypothesis, metric)]
                mean_err = statistics.mean(r["abs_relative_error_pct"] for r in filtered)
                mismatches = sum(r["different_pass_fail_verdict"] for r in filtered)
                print(f"  {section:5} {hypothesis:8} {metric:8} MAPE={mean_err:6.2f}% "
                      f"boundary verdict mismatches={mismatches}")
    # Regression control only: ±2% is not a safety margin or code acceptance limit.
    # Previous, independently generated ARBAT RTF results are treated as frozen.
    # A pass/fail disagreement is accepted in the regression only when both
    # strengths lie near unity, and MUST still be escalated for ARBAT review.
    reference_rows = [
        r for r in rows
        if r["hypothesis"] == "plus_ea" and r["metric"] == "joint_NM"
    ]
    if len(reference_rows) != 16:
        raise ValueError("Expected 16 independent validation rows")
    failed_regressions = [
        r for r in reference_rows
        if not math.isfinite(r["abs_relative_error_pct"]) or r["abs_relative_error_pct"] > 2.0
    ]
    if failed_regressions:
        raise ValueError(f"ARBAT v2 numerical regression exceeded 2%: {failed_regressions}")
    unsafe_disagreement = [
        r for r in reference_rows
        if r["different_pass_fail_verdict"] and not r["boundary_2pct"]
    ]
    if unsafe_disagreement:
        raise ValueError(f"Unexpected distant pass/fail disagreement: {unsafe_disagreement}")
    near_boundary = [r for r in reference_rows if r["boundary_2pct"]]
    print(f"Regression reference reproduced: {len(reference_rows)} cases, "
          f"{len(near_boundary)} near-boundary cases requiring engineering review.")
    if near_boundary:
        for row in near_boundary:
            print(f"  REVIEW {row['case_id']}: ARBAT={row['ARBAT_moment_factor']:.6f} "
                  f"CScore={row['CScore_factor']:.6f}; "
                  f"verdict_mismatch={row['different_pass_fail_verdict']}")
    return rows


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--prepare-inputs", action="store_true")
    p.add_argument("--output-dir", type=Path, default=ROOT / "examples/verification/diagnostic_forces")
    p.add_argument("--results-dir", type=Path, default=ROOT / "results")
    p.add_argument("--output-csv", type=Path, default=ROOT / "results/arbat_v2_comparison.csv")
    args = p.parse_args()
    if args.prepare_inputs:
        corrected_inputs(args.output_dir)
    else:
        comparison(args.results_dir, args.output_csv)


if __name__ == "__main__":
    main()
