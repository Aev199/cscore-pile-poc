#!/usr/bin/env python3
"""Isolated experimental CScore crack-width probe for the independently supplied ARBAT RTFs.

Never upgrades a ULS candidate to design-approved status. The user reports long share
coefficient = 1, so the research test uses sustained==total axial/moment actions.
Explicit research-only service material assumptions are *not* independently calibrated.
"""
from __future__ import annotations

import argparse
import csv
import json
import math
import subprocess
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import pile_rebar as piles

REFERENCE = ROOT / "examples/verification/arbat_matched/arbat_reference_v2_nm8.json"
BASE = ROOT / "examples/verification/diagnostic_forces"

# These material parameters are explicitly labeled research assumptions.
# They are NOT claimed to reproduce ARBAT material diagrams or creep.
SLS_MODEL_ID = "B30_A500_unverified_service_diagrams_N_NL_equal"
SERVICE_PARAMS = {
    "concrete_rb_kpa": 22000, "concrete_rbt_kpa": 1750,
    "concrete_e_kpa": 32500000, "concrete_ec1": -0.0015,
    "concrete_ec2": -0.0035, "concrete_et1": 0.00008,
    "concrete_et2": 0.00015, "rebar_rs_kpa": 500000,
    "rebar_e_kpa": 200000000, "rebar_ec2": -0.025,
    "rebar_et2": 0.025,
}


def prepare_request(section: str, reference: dict) -> dict:
    source = json.loads((BASE / f"{section}_NM8.json").read_text(encoding="utf-8"))
    piles.validate_input(source)
    accepted, rejected = piles.make_candidates(source)
    if len(accepted) != 1 or rejected:
        raise ValueError(f"{section}: expected one geometrically admissible reference cage")
    group = reference["groups"][section]
    diameter = group["diameter_mm"]
    if source["pile"]["diameter_mm"] != diameter:
        raise ValueError("Reference section diameter mismatch")
    if accepted[0]["bar_count"] != group["bar_count"] or \
       accepted[0]["bar_diameter_mm"] != group["bar_diameter_mm"]:
        raise ValueError("Reference cage bar count/diameter mismatch")
    if not math.isclose(
        accepted[0]["bar_axis_radius_mm"],
        diameter/2 - group["a1_mm"] - group["bar_diameter_mm"]/2, abs_tol=1e-8
    ):
        raise ValueError("Bar center does not match ARBAT a1 cover")
    req = piles.build_engine_request(source, accepted)
    expected_ids = [row["id"] for row in group["cases"]]
    if len(set(expected_ids)) != 8:
        raise ValueError("ARBAT source did not contain eight unique cases")
    req["loads"] = [source["loads"][0]]  # ULS is a dummy, not used in SLS comparison.
    req["sls_probe"] = {
        "short_materials": SERVICE_PARAMS.copy(),
        "long_materials": SERVICE_PARAMS.copy(),
        "phi2": 0.5,
        "acrc_ult_long_mm": 0.3,
        "acrc_ult_short_mm": 0.4,
        "loads": [{
            "id": r["id"],
            "n_kn": r["n_kn"],
            "mx_total_knm": r["my_knm"], "my_total_knm": 0,
            "mx_long_knm": r["my_knm"], "my_long_knm": 0
        } for r in group["cases"]],
    }
    return req


def check_answer(section: str, reference: dict, answer: dict) -> list[dict]:
    if not isinstance(answer, dict) or answer.get("schema_version") != 1:
        raise ValueError("Invalid CScore JSON")
    raw = answer.get("cases")
    if not isinstance(raw, list) or len(raw) != 1:
        raise ValueError("CScore must return one reference candidate")
    probe = raw[0].get("sls_probe")
    if not isinstance(probe, list):
        raise ValueError("Missing separate SLS probe results")
    expected = {r["id"]: r for r in reference["groups"][section]["cases"]}
    if len(probe) != len(expected) or {row.get("id") for row in probe} != set(expected):
        raise ValueError("Incomplete/duplicated SLS force results")
    rows = []
    for case in probe:
        ref = expected[case["id"]]
        valid = (
            case.get("status") == "research_calculated"
            and case.get("error") is None
            and case.get("crc_converged") is True
            and case.get("verified_for_design") is False
            and all(isinstance(case.get(key), (float,int))
                    and math.isfinite(case[key]) and case[key] >= 0
                    for key in ("width_long_mm", "width_short_mm",
                                "utilization_long", "utilization_short"))
        )
        # Never extrapolate a zero crack width to a pass if ARBAT reported positive cracks.
        if valid and case.get("cracked") is False and (
            ref["arbat_crack_long_util"] is not None or
            ref["arbat_crack_short_util"] is not None
        ):
            valid = False
        status = "computed_experimental" if valid else "indeterminate_requires_arbat"
        rows.append({
            "section": section, "case": case["id"], "N_kN": ref["n_kn"],
            "ARBAT_My_kNm": ref["my_knm"], "sls_assumption": SLS_MODEL_ID,
            "sustained_equals_total_assumed": True,
            "ARBAT_width_long_util": ref["arbat_crack_long_util"],
            "ARBAT_width_short_util": ref["arbat_crack_short_util"],
            "CScore_width_long_mm": case.get("width_long_mm") if valid else None,
            "CScore_width_short_mm": case.get("width_short_mm") if valid else None,
            "CScore_util_long": case.get("utilization_long") if valid else None,
            "CScore_util_short": case.get("utilization_short") if valid else None,
            "CScore_status": status,
            "raw_upstream_status": case.get("status"),
            "raw_error": case.get("error"),
            "ARBAT_vs_CScore_long_pct": (
                (case["utilization_long"] / ref["arbat_crack_long_util"] - 1) * 100
                if valid and ref["arbat_crack_long_util"] and ref["arbat_crack_long_util"] > 0
                else None
            ),
            "ARBAT_vs_CScore_short_pct": (
                (case["utilization_short"] / ref["arbat_crack_short_util"] - 1) * 100
                if valid and ref["arbat_crack_short_util"] and ref["arbat_crack_short_util"] > 0
                else None
            ),
            "design_status": "NOT_VERIFIED",
        })
    return rows


def run(runner: Path, output_dir: Path, timeout: int = 900) -> list[dict]:
    reference = json.loads(REFERENCE.read_text(encoding="utf-8"))
    output_dir.mkdir(parents=True, exist_ok=True)
    all_rows = []
    for section in ("P800", "P1000"):
        request = prepare_request(section, reference)
        req_file = output_dir / f"{section}_research_input.json"
        req_file.write_text(json.dumps(request, ensure_ascii=False, indent=2), encoding="utf-8")
        command = ["dotnet", str(runner.resolve())] if runner.suffix.lower() == ".dll" \
            else [str(runner.resolve())]
        completed = subprocess.run(
            command, input=json.dumps(request, allow_nan=False), timeout=timeout,
            capture_output=True, text=True, encoding="utf-8"
        )
        if completed.returncode:
            raise RuntimeError(f"{section}: CScore failed: {completed.stderr[:1800]}")
        answer = json.loads(completed.stdout)
        (output_dir / f"{section}_research_output.json").write_text(
            json.dumps(answer, indent=2, ensure_ascii=False), encoding="utf-8"
        )
        rows = check_answer(section, reference, answer)
        all_rows.extend(rows)
        ok = sum(r["CScore_status"] == "computed_experimental" for r in rows)
        print(f"{section}: {ok}/8 experimentally computed, {8-ok}/8 indeterminate")
    output = output_dir / "crack_width_comparison.csv"
    with output.open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(all_rows[0]), delimiter=";")
        writer.writeheader()
        writer.writerows(all_rows)
    print("Reference crack-width model has not been independently aligned with ARBAT.")
    print(f"Research comparison: {output}")
    return all_rows


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--runner", type=Path, required=True)
    p.add_argument("--output-dir", type=Path, default=ROOT / "results/crack_probe")
    a = p.parse_args()
    run(a.runner, a.output_dir)
