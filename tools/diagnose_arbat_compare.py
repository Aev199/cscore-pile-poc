#!/usr/bin/env python3
"""Compare CScore ARBAT diagnostic hypotheses without tuning hidden parameters.

Produces both raw rows and summary of N,M scaling alternatives. No fit is proof.
"""
from __future__ import annotations

import csv
import json
import math
import statistics
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REFERENCE = ROOT / "examples/verification/arbat_matched/arbat_reference_20261015.json"


def analyze(diagnostics_dir: Path, results_dir: Path) -> tuple[list[dict], list[dict]]:
    reference = json.loads(REFERENCE.read_text(encoding="utf-8"))["groups"]
    manifest = json.loads((diagnostics_dir / "manifest.json").read_text(encoding="utf-8"))
    rows = []
    summary = []
    for item in manifest:
        cfile = results_dir / f"{item['id']}.json"
        result = json.loads(cfile.read_text(encoding="utf-8"))
        cases = result.get("cases", [])
        if len(cases) != 1:
            raise ValueError(f"{cfile.name}: expected one fixed cage")
        candidate = cases[0]
        if abs(candidate["bar_axis_radius_mm"] - item["axis_radius_mm"]) > 1e-7:
            raise ValueError(f"{cfile.name}: cage radius mismatch")
        computed = {x["id"]: x for x in candidate["loads"]}
        arbat = {x["id"]: x for x in reference[item["section"]]["cases"]}
        actions = {x["id"]: x for x in item["applied_actions"]}
        if set(computed) != set(arbat) or set(arbat) != set(actions):
            raise ValueError(f"{cfile.name}: missing or duplicated actions")
        per_metric = {"moment": [], "all": []}
        verdict_mismatches = {"moment": 0, "all": 0}
        for key, a in arbat.items():
            r = computed[key]
            factors = {
                "moment": r.get("moment_utilization") if r.get("moment_converged") is True and r.get("moment_error") is None else None,
                "all": r.get("utilization") if r.get("converged") is True and r.get("error") is None else None,
            }
            for metric, value in factors.items():
                valid = isinstance(value, (int, float)) and math.isfinite(value) and value >= 0
                if valid:
                    per_metric[metric].append(abs(value / a["moment_utilization"] - 1.0))
                    verdict_mismatches[metric] += (value <= 1.0) != (a["moment_utilization"] <= 1.0)
                rows.append({
                    "scenario": item["id"], "section": item["section"], "loading": key,
                    "cover_hypothesis": item["cover_hypothesis"], "concrete_factor": item["gamma_concrete"],
                    "eccentricity_added": item["added_accidental_eccentricity"],
                    "bar_axis_radius_mm": item["axis_radius_mm"],
                    "ARBAT_N_kN": a["n_kn"], "ARBAT_My_kNm": a["my_knm"],
                    "CScore_N_kN": actions[key]["n_kn"], "CScore_Mx_kNm": actions[key]["mx_knm"],
                    "factor_kind": metric, "ARBAT_reported_moment_utilization": a["moment_utilization"],
                    "CScore_utilization": value if valid else None,
                    "relative_difference_pct": 100 * (value / a["moment_utilization"] - 1) if valid else None,
                    "error": (r.get("moment_error") if metric == "moment" else r.get("error")) if not valid else "",
                })
        for metric, err in per_metric.items():
            summary.append({
                "scenario": item["id"], "section": item["section"],
                "cover_hypothesis": item["cover_hypothesis"],
                "gamma": item["gamma_concrete"], "eccentricity_added": item["added_accidental_eccentricity"],
                "factor_kind": metric, "cases_converged": len(err),
                "mape_pct": 100 * statistics.mean(err) if err else None,
                "max_abs_relative_error_pct": 100 * max(err) if err else None,
                "different_pass_fail_verdicts": verdict_mismatches[metric],
            })
    return rows, summary


def write_csv(path: Path, rows: list[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0]), delimiter=";")
        writer.writeheader()
        writer.writerows(rows)


if __name__ == "__main__":
    diag_dir = ROOT / "results/diagnostics"
    rows, summary = analyze(diag_dir, diag_dir)
    write_csv(ROOT / "results/diagnostic_comparison.csv", rows)
    write_csv(ROOT / "results/diagnostic_summary.csv", summary)
    print("Diagnostic comparison completed:", len(rows), "rows")
    for row in sorted(summary, key=lambda x: (x["section"], x["mape_pct"] if x["mape_pct"] is not None else 1e9)):
        if row["cases_converged"] == 8:
            print(row["section"], row["scenario"], row["factor_kind"],
                  f"MAPE={row['mape_pct']:.2f}%", "verdict differences=", row["different_pass_fail_verdicts"])
    if any(row["cases_converged"] != 8 for row in summary):
        raise SystemExit("Some numerical cases are not converged; inspect detailed CSV")
