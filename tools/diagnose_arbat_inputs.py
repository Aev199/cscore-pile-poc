#!/usr/bin/env python3
"""Generate traceable, isolated CScore sensitivity inputs for ARBAT comparisons.

Every input is a *hypothesis*, not a verified description of ARBAT internal mechanics.
Reference ARBAT loads remain unchanged in arbat_reference_20261015.json.
"""
from __future__ import annotations

import copy
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / "examples/verification"
TARGET = BASE / "diagnostics"


def generate(output_dir: Path = TARGET) -> list[dict]:
    output_dir.mkdir(parents=True, exist_ok=True)
    manifest = []
    for d, phi, count in ((800, 25, 12), (1000, 28, 16)):
        original = json.loads((BASE / f"P{d}_reference.json").read_text(encoding="utf-8"))
        assert original["pile"]["diameter_mm"] == d
        assert original["search"]["bar_counts"] == [count]
        assert original["search"]["bar_diameters_mm"] == [phi]
        a1 = 70.0
        tie = 12.0
        length = 3.0
        ea_mm = max(length * 1000 / 600, d / 30, 10.0)
        for cover_mode in ("bar_surface", "bar_center_prior"):
            for gamma in (1.0, 0.9):
                for eccentricity in (False, True):
                    data = copy.deepcopy(original)
                    # CScore outer-cover-to-stirrup + stirrup Ø + longitudinal bar radius.
                    # ARBAT bar_surface: a1 is distance to bar outer surface.
                    cover = a1 - tie if cover_mode == "bar_surface" else a1 - tie - phi / 2
                    data["pile"]["cover_mm"] = cover
                    data["pile"]["ties_diameter_mm"] = tie
                    data["materials"]["concrete_rb_kpa"] = 17000 * gamma
                    data["materials"]["concrete_rbt_kpa"] = 1150 * gamma
                    if eccentricity:
                        for load in data["loads"]:
                            n = load["n_kn"]
                            m = load["mx_knm"]
                            # Hypothesis only: externally added SP63 ea for statically determinate compression.
                            # We do NOT claim that ARBAT implements exactly this for its reported K.
                            if n < 0:
                                load["mx_knm"] = m + (1 if m >= 0 else -1) * abs(n) * ea_mm / 1000
                    name = f"P{d}_{cover_mode}_gamma{int(100*gamma)}_ea{int(eccentricity)}"
                    destination = output_dir / f"{name}.json"
                    destination.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
                    bar_radius = d / 2 - cover - tie - phi / 2
                    manifest.append({
                        "id": name,
                        "section": f"P{d}",
                        "input": str(destination.name),
                        "cover_hypothesis": cover_mode,
                        "gamma_concrete": gamma,
                        "added_accidental_eccentricity": eccentricity,
                        "ea_mm": ea_mm if eccentricity else 0,
                        "axis_radius_mm": bar_radius,
                        "applied_actions": [
                            {"id": x["id"], "n_kn": x["n_kn"], "mx_knm": x["mx_knm"], "my_knm": x["my_knm"]}
                            for x in data["loads"]
                        ],
                    })
    (output_dir / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return manifest


if __name__ == "__main__":
    output = generate()
    print(f"Generated {len(output)} traceable diagnostic scenarios: {TARGET}")
