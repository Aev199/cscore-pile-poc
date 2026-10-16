#!/usr/bin/env python3
"""Cross-section SLS research matrix: round + rectangular. NOT design verification.

Constructs true rectangular contours and explicit bar coordinates in CScore,
preserves the unchanged circular BNS workflow, and produces a blank external
ARBAT reference schedule for INDEPENDENT engineer-run checks.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / "examples/verification/diagnostic_forces/P800_NM8.json"
SERVICE_MODEL_ID = "B30_A500_ILLUSTRATIVE_SLS_PARAMETERS_NOT_NORMATIVELY_ALIGNED"
METHODS = ("arbat_conservative", "native_refined")


def bars_rectangle(width: float, height: float, diameter: float, cover: float,
                   side_mid: bool, top_mid: bool) -> list[dict]:
    # cover is to the external face of LONGITUDINAL bars, in mm.
    hx, hy = width / 2 - cover - diameter / 2, height / 2 - cover - diameter / 2
    points = [(-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy)]
    if side_mid:
        points += [(-hx, 0), (hx, 0)]
    if top_mid:
        points += [(0, -hy), (0, hy)]
    return [{"x_mm": x, "y_mm": y, "diameter_mm": diameter} for x, y in points]


def section_definitions(layout: str = "original") -> list[dict]:
    """Original is immutable historical benchmark; arbat_stated is a new
    geometry hypothesis based on the independently supplied ARBAT 2019 RTF.
    ARBAT armature axis/orientation still requires external verification.
    """
    if layout not in ("original", "arbat_stated"):
        raise ValueError("Unsupported geometry layout")
    rectangular = [
        ("R300x500_4d20_cover45", 300, 500, 20, 45, False, False),
        ("R400x600_6d20_cover50", 400, 600, 20, 50, True, False),
        ("R500x700_8d25_cover60", 500, 700, 25, 60, True, True),
        ("R400x400_8d16_cover35", 400, 400, 16, 35, True, True),
    ]
    result = [
        {"id": label, "kind": "rectangle", "width_mm": w, "height_mm": h,
         "cover_to_longitudinal_bar_surface_mm": c, "bar_diameter_mm": phi,
         "bars": bars_rectangle(w, h, phi, c, side, top)}
        for label, w, h, phi, c, side, top in rectangular
    ]
    # Replace the last perimeter layout with a genuine two-layer bar scheme:
    # 4 outer face bars and 4 inward bars on the opposite bending side.
    # This tests differential stress levels, not only multiple edge positions.
    last = result[-1]
    if last["id"] == "R400x400_8d16_cover35":
        last["id"] = "R400x400_8d16_two_layers_cover35"
        last["bars"] = [
            {"x_mm": x, "y_mm": y, "diameter_mm": 16}
            for y in (-157, -90, 90, 157)
            for x in (-157, 157)
        ]
    if layout == "arbat_stated":
        for section in result:
            d, a1 = section["bar_diameter_mm"], section["cover_to_longitudinal_bar_surface_mm"]
            hx = section["width_mm"] / 2 - a1 - d / 2
            hy = section["height_mm"] / 2 - a1 - d / 2
            if section["id"] == "R400x600_6d20_cover50":
                # ARBAT reports S1=3 bars, S2=3 bars. Hypothesis:
                # equally spaced on opposing TOP/BOTTOM faces.
                points = [(x,y) for y in (-hy,hy) for x in (-hx,0.0,hx)]
            elif section["id"] == "R500x700_8d25_cover60":
                # ARBAT S1=4, S2=4, rather than CScore's perimeter middles.
                points = [(x,y) for y in (-hy,hy)
                          for x in (-hx,-hx/3,hx/3,hx)]
            elif section["id"] == "R400x400_8d16_two_layers_cover35":
                # ARBAT S1/S2: 2x16 first row + 2x16 second row,
                # clear bar-to-bar row separation 20mm => 36mm center.
                inside = hy - (d + 20.0)
                points = [(x,y) for y in (-hy,-inside,inside,hy)
                          for x in (-hx,hx)]
            else:
                points = [(x,y) for y in (-hy,hy) for x in (-hx,hx)]
            section["bars"] = [
                {"x_mm":x, "y_mm":y, "diameter_mm":d} for x,y in points
            ]
            section["geometry_alignment"] = (
                "ARBAT RTF bar-count and layer-geometry hypothesis; axes to verify"
            )
    for d, phi, count, cover in (
        (600, 20, 8, 50), (800, 25, 16, 55),
        (1000, 28, 20, 70),
    ):
        result.append({
            "id": f"C{d}_{count}d{phi}_cover{cover}",
            "kind": "circle", "diameter_mm": d, "bar_diameter_mm": phi,
            "bar_count": count, "cover_to_longitudinal_bar_surface_mm": cover,
        })
    return result


def loads() -> list[dict]:
    # One force tuple is kept intact; N in kN, M in kN.m.
    # Long and total N are identical (the pinned upstream solver accepts only one).
    cases = [
        ("T01_pure_Mx", 0, 60, 0, 60, 0),
        ("T02_high_Mx", 0, 180, 0, 180, 0),
        ("T03_reverse_Mx", 0, -180, 0, -180, 0),
        ("T04_My", 0, 0, 110, 0, 110),
        ("T05_biax", 0, 120, 75, 120, 75),
        ("T06_compression", -500, 160, 0, 160, 0),
        ("T07_tension", 150, 130, 0, 130, 0),
        ("T08_low_Mx", 0, 10, 0, 10, 0),
        ("T09_partial_sustained", 0, 160, 0, 80, 0),
        ("T10_partial_biax", -250, 140, 80, 70, 40),
    ]
    return [
        {"id": name, "n_kn": n, "mx_total_knm": mx, "my_total_knm": my,
         "mx_long_knm": mlx, "my_long_knm": mly}
        for name, n, mx, my, mlx, mly in cases
    ]


def make_request(section: dict, mode: str = "arbat_conservative") -> dict:
    if mode not in METHODS:
        raise ValueError("Unrecognized preliminary method")
    base = json.loads(BASE.read_text(encoding="utf-8"))
    materials = base["materials"].copy()
    # Research-only: all SLS characteristics must be explicitly supplied and
    # CAN be changed later after independent confirmation from the ARBAT dialog.
    short = {
        **materials,
        "concrete_rb_kpa": 22000, "concrete_rbt_kpa": 1750,
        "concrete_e_kpa": 32500000, "rebar_rs_kpa": 500000,
        "rebar_e_kpa": 200000000,
        "concrete_et1": 0.00008, "concrete_et2": 0.00015,
    }
    long = short.copy()
    request = {
        "schema_version": 1,
        # Historical independent research must NEVER pretend dummy ULS is a real gate.
        "sls_execution_policy": "research_all",
        "pile": {
            "diameter_mm": section.get("diameter_mm", 0),
            "mesh_resolution": 41,
            "polygon_sides": 96 if section["kind"] == "circle" else 0,
        },
        "materials": materials,
        "loads": [{"id": "uls_dummy_no_approval", "n_kn": 0, "mx_knm": 1, "my_knm": 0}],
        "candidates": [],
        "sls_probe": {
            "short_materials": short, "long_materials": long,
            "phi2": 0.5, "acrc_ult_long_mm": 0.3, "acrc_ult_short_mm": 0.4,
            "preliminary_method": mode, "review_band": 0.03,
            "loads": loads(),
        },
    }
    if section["kind"] == "rectangle":
        request["research_rectangle"] = {
            "width_mm": section["width_mm"], "height_mm": section["height_mm"],
            "mesh_resolution": 41,
            "bars": section["bars"],
        }
        steel_area = sum(math.pi * b["diameter_mm"]**2 / 4 for b in section["bars"])
        request["candidates"] = [{
            "id": section["id"], "bar_count": len(section["bars"]),
            "bar_diameter_mm": section["bar_diameter_mm"],
            "rotation_deg": 0, "bar_axis_radius_mm": 0,
            "steel_area_mm2": steel_area, "clearance_mm": 0,
        }]
    else:
        d, phi, count = section["diameter_mm"], section["bar_diameter_mm"], section["bar_count"]
        a1 = section["cover_to_longitudinal_bar_surface_mm"]
        r = d / 2 - a1 - phi / 2
        clear = 2*r*math.sin(math.pi/count) - phi
        if r <= 0 or clear <= 0:
            raise ValueError(f"Invalid circular bars for {section['id']}")
        request["candidates"] = [{
            "id": section["id"], "bar_count": count, "bar_diameter_mm": phi,
            "rotation_deg": 0, "bar_axis_radius_mm": r,
            "steel_area_mm2": count*math.pi*phi**2/4,
            "clearance_mm": clear,
        }]
    return request


def validate_answer(section: dict, req: dict, answer: dict) -> list[dict]:
    if answer.get("schema_version") != 1:
        raise ValueError("Unrecognized CScore output")
    cases = answer.get("cases")
    if not isinstance(cases, list) or len(cases) != 1:
        raise ValueError("Missing or extra section results")
    if cases[0].get("id") != section["id"]:
        raise ValueError("Different cage ID returned")
    # The 70-case historical sample deliberately has a dummy ULS tuple,
    # so it must be tagged as independent research rather than candidate approval.
    if (req.get("sls_execution_policy") != "research_all"
            or cases[0].get("sls_gate") != "calculated_research_override"):
        raise ValueError("Research-only ARBAT matrix lost explicit ULS bypass provenance")
    sls = cases[0].get("sls_probe")
    expected = {a["id"]: a for a in req["sls_probe"]["loads"]}
    if not isinstance(sls, list) or len(sls) != len(expected) or \
       {v.get("id") for v in sls} != set(expected):
        raise ValueError("Incomplete or duplicated SLS load results")
    rows = []
    for item in sls:
        ids = expected[item["id"]]
        status = item.get("status")
        calculated = status == "research_calculated" and item.get("error") is None
        primary = item.get("preliminary_utilization_long")
        native = item.get("utilization_long")
        primary_short = item.get("preliminary_utilization_short")
        native_short = item.get("utilization_short")
        governing = item.get("preliminary_governing_utilization")
        if calculated:
            if not all(isinstance(v, (int, float)) and math.isfinite(v) and v >= 0
                       for v in (primary, native, primary_short, native_short, governing)):
                raise ValueError("CScore marked a case as computed but gave no finite result")
            if not math.isclose(governing, max(primary,primary_short),rel_tol=1e-8,abs_tol=1e-8):
                raise ValueError("Incorrect governing long/short SLS coefficient")
            if req["sls_probe"]["preliminary_method"] == "arbat_conservative" and (
                primary + 1e-9 < native or primary_short + 1e-9 < native_short
            ):
                raise ValueError("Default preliminary screening is less conservative than native")
            if item.get("preliminary_basis") == "short_only_supplemental_full_load_solver":
                if (item.get("preliminary_review_reason") !=
                    "cracked_only_under_total_load_unvalidated_mcrc_threshold"
                    or item.get("preliminary_decision") != (
                        "preliminary_exceeds_limit"
                        if governing > 1 + req["sls_probe"]["review_band"]
                        else "engineering_review"
                    )
                    or primary != 0 or native != 0
                    or primary_short <= 0 or native_short <= 0):
                    raise ValueError("Invalid supplemental short-only SLS status")
        # A failed solver may omit preliminary fields, but it must remain indeterminate.
        if item.get("verified_for_design") is not False or (
            item.get("preliminary_is_final_approval") is True or
            calculated and item.get("preliminary_is_final_approval") is not False
        ):
            raise ValueError("Research case must never be approved for design")
        if not calculated and item.get("preliminary_decision") not in (None, "indeterminate"):
            raise ValueError("Invalid solver cannot return a preliminary verdict")
        rows.append({
            "section_id": section["id"], "section_kind": section["kind"],
            "case_id": item["id"], "N_kN": ids["n_kn"],
            "Mx_total_kNm": ids["mx_total_knm"], "My_total_kNm": ids["my_total_knm"],
            "Mx_long_kNm": ids["mx_long_knm"], "My_long_kNm": ids["my_long_knm"],
            "method": item.get("preliminary_method"),
            "preliminary_basis": item.get("preliminary_basis"),
            "preliminary_status": item.get("preliminary_decision"),
            "preliminary_review_reason": item.get("preliminary_review_reason"),
            "crack_solver_path": item.get("crack_solver_path", "upstream_default"),
            "secondary_equilibrium_status": item.get("secondary_equilibrium_status"),
            "secondary_equilibrium_converged": item.get("secondary_equilibrium_converged"),
            "secondary_equilibrium_iterations": item.get("secondary_equilibrium_iterations"),
            "secondary_equilibrium_residual": item.get("secondary_equilibrium_residual"),
            "preliminary_width_mm": item.get("preliminary_width_long_mm"),
            "preliminary_utilization": primary if calculated else None,
            "preliminary_width_short_mm": item.get("preliminary_width_short_mm") if calculated else None,
            "preliminary_utilization_short": primary_short if calculated else None,
            "governing_preliminary_utilization": governing if calculated else None,
            "native_width_mm": item.get("width_long_mm") if calculated else None,
            "native_utilization": native if calculated else None,
            "native_width_short_mm": item.get("width_short_mm") if calculated else None,
            "native_utilization_short": native_short if calculated else None,
            "compatibility_width_mm": item.get("width_long_precrack_hypothesis_mm") if calculated else None,
            "psi_one_width_mm": item.get("width_long_psi_one_mm") if calculated else None,
            "crack_spacing_mm": item.get("crack_spacing_m")*1000
                if calculated and isinstance(item.get("crack_spacing_m"), (float,int)) else None,
            "psi_s_native": item.get("psi_s") if calculated else None,
            "cscore_status": status, "error": item.get("error"),
            "arbat_width_long_mm": None, "arbat_width_short_mm": None,
            "arbat_report_status": "NOT_PROVIDED",
            "final_design_status": "NOT_VERIFIED",
        })
    return rows


def check_fully_sustained_width_identity(rows: list[dict]) -> int:
    """For same total and sustained N/M, SP63 a_short=a1+a2-a3=a1.

    The independent refined native solver has the same identity. Check the
    conservative *preliminary* compatibility method as an integration test.
    """
    count = 0
    for row in rows:
        if (row["cscore_status"] != "research_calculated"
            or row["method"] != "arbat_conservative"
            or row["Mx_total_kNm"] != row["Mx_long_kNm"]
            or row["My_total_kNm"] != row["My_long_kNm"]):
            continue
        a = row.get("preliminary_width_mm")
        b = row.get("preliminary_width_short_mm")
        if not (isinstance(a, (int, float)) and isinstance(b, (int, float))
                and math.isfinite(a) and math.isfinite(b)
                and math.isclose(a, b, rel_tol=1e-8, abs_tol=1e-8)):
            raise ValueError(
                f"SP63 fully-sustained crack identity failed: "
                f"{row['section_id']}/{row['case_id']} long={a} short={b}"
            )
        count += 1
    return count


def write_table(path: Path, rows: list[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8-sig", newline="") as file:
        w = csv.DictWriter(file, delimiter=";", fieldnames=list(rows[0]))
        w.writeheader()
        w.writerows(rows)


def generate_cases(output_dir: Path, sections: list[dict], method: str = "arbat_conservative", layout: str = "original") -> list[dict]:
    output_dir.mkdir(parents=True, exist_ok=True)
    summary = []
    for section in sections:
        request = make_request(section, mode=method)
        (output_dir / f"{section['id']}_input.json").write_text(
            json.dumps(request, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
        )
        summary.append({
            **section,
            "source": "GENERATED_RESEARCH_INPUT_NOT_ARBAT_REFERENCE",
            "input_file": f"{section['id']}_input.json",
            "input_sha256": hashlib.sha256(
                (output_dir / f"{section['id']}_input.json").read_bytes()).hexdigest(),
        })
    (output_dir / "manifest.json").write_text(
        json.dumps({"model": SERVICE_MODEL_ID, "layout": layout,
                    "geometry_hypothesis_not_independently_verified": layout=="arbat_stated",
                    "sections": summary}, indent=2, ensure_ascii=False),
        encoding="utf-8"
    )
    arbat_sheet = []
    for section in sections:
        for a in loads():
            arbat_sheet.append({
                "section_id": section["id"], "section_kind": section["kind"],
                "width_mm": section.get("width_mm"), "height_mm": section.get("height_mm"),
                "diameter_mm": section.get("diameter_mm"),
                "cover_to_bar_surface_mm": section["cover_to_longitudinal_bar_surface_mm"],
                "bar_diameter_mm": section["bar_diameter_mm"],
                "bar_count": len(section["bars"]) if section["kind"] == "rectangle"
                    else section["bar_count"],
                "N_kN": a["n_kn"], "Mx_total_kNm": a["mx_total_knm"],
                "My_total_kNm": a["my_total_knm"], "Mx_long_kNm": a["mx_long_knm"],
                "My_long_kNm": a["my_long_knm"], "case_id": a["id"],
                "arbat_acrc_long_mm": None, "arbat_acrc_short_mm": None,
                "arbat_status": "NOT_CALCULATED",
            })
    write_table(output_dir / "arbat_reference_TO_FILL.csv", arbat_sheet)
    return summary


def run(runner: Path, output_dir: Path, method: str = "arbat_conservative", layout: str = "original") -> list[dict]:
    definitions = section_definitions(layout)
    generate_cases(output_dir, definitions, method=method, layout=layout)
    all_rows = []
    for section in definitions:
        source = output_dir / f"{section['id']}_input.json"
        req = json.loads(source.read_text(encoding="utf-8"))
        command = ["dotnet", str(runner.resolve())] if runner.suffix.lower() == ".dll" \
            else [str(runner.resolve())]
        done = subprocess.run(
            command, input=json.dumps(req, allow_nan=False), capture_output=True,
            text=True, encoding="utf-8", timeout=900,
        )
        if done.returncode:
            raise RuntimeError(f"{section['id']}: solver failed: {done.stderr[:1000]}")
        answer = json.loads(done.stdout)
        (output_dir / f"{section['id']}_output.json").write_text(
            json.dumps(answer, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
        )
        rows = validate_answer(section, req, answer)
        all_rows.extend(rows)
        computed = sum(x["cscore_status"] == "research_calculated" for x in rows)
        indeterminate = len(rows)-computed
        print(f"{section['id']}: {computed}/{len(rows)} computed, "
              f"{indeterminate} indeterminate, ARBAT references not yet available")
        if computed == 0:
            raise RuntimeError(
                f"{section['id']}: the research runner returned no usable SLS values; "
                "see archived raw CScore JSON and diagnose before accepting the experiment"
            )
    fully_sustained = check_fully_sustained_width_identity(all_rows)
    print(f"SP63 identity a_short = a_long on {fully_sustained} fully sustained computed SLS cases")
    restored = [x for x in all_rows
                if x.get("crack_solver_path") == "supplemental_full_load_Acrc2"]
    unresolved = [x for x in all_rows if x["cscore_status"] != "research_calculated"]
    print(f"Partial sustained only: {len(restored)} supplemental short-only "
          f"research calculations; {len(unresolved)} still indeterminate.")
    for x in restored:
        if x["preliminary_status"] not in ("engineering_review",
                                             "preliminary_exceeds_limit"):
            raise ValueError("Recovered short-only load cannot be accepted")
        print(f"  short-only supplemental: {x['section_id']} {x['case_id']}, "
              f"short K={x['preliminary_utilization_short']:.4f}, "
              "UNVERIFIED/ENGINEERING CHECK REQUIRED")
    for x in unresolved:
        print(f"  unresolved: {x['section_id']} {x['case_id']}, "
              f"reason={x.get('error')}, path={x.get('crack_solver_path')}, "
              f"secondary_eq={x.get('secondary_equilibrium_status')}, "
              f"residual={x.get('secondary_equilibrium_residual')}")
    write_table(output_dir / "cross_section_sls_matrix.csv", all_rows)
    print(f"Research matrix {len(definitions)} sections, {len(all_rows)} load tuples; "
          "zero ARBAT comparisons until externally verified reports arrive.")
    return all_rows


if __name__ == "__main__":
    cli = argparse.ArgumentParser()
    cli.add_argument("--runner", type=Path)
    cli.add_argument("--method", choices=METHODS, default="arbat_conservative")
    cli.add_argument("--layout", choices=("original","arbat_stated"), default="original")
    cli.add_argument("--out", type=Path, default=ROOT/"results/sls_matrix")
    a = cli.parse_args()
    if a.runner:
        run(a.runner, a.out, method=a.method, layout=a.layout)
    else:
        models = generate_cases(a.out, section_definitions(a.layout), method=a.method,
                                layout=a.layout)
        print(f"Prepared {len(models)} sections and blank ARBAT reference schedule in {a.out}")
