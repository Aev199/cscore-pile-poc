#!/usr/bin/env python3
"""Crack-width benchmarks from published worked examples, run through the runner.

Sources (numbers transcribed from the published solutions):
- Posobie to SP 52-101-2003, example 43 (slab, force method) and example 44
  (column, N_l=500 kN, partly sustained moment, force method).
- SCAD Soft, ARBAT test 10 / Perelmuter, Popok, Skoruk 2013: the same slab as
  example 43 solved by the nonlinear deformation model (SP63 8.2.14).
Example 42 of the Posobie is a T-section, which the runner does not model.
"""
import argparse, json, subprocess
from pathlib import Path

B15 = dict(concrete_rb_kpa=11000, concrete_rbt_kpa=1100, concrete_e_kpa=24000000, concrete_ec1=-0.0015,
           concrete_ec2=-0.0035, concrete_et1=8e-05, concrete_et2=0.00015, rebar_rs_kpa=400000,
           rebar_e_kpa=200000000, rebar_ec2=-0.025, rebar_et2=0.025)
ULS = dict(B15, concrete_rb_kpa=8500, concrete_rbt_kpa=750, rebar_rs_kpa=350000)
SLAB = dict(width_mm=1150, height_mm=300, bars=[dict(x_mm=-533 + i * 1066 / 5, y_mm=-108, diameter_mm=14) for i in range(6)])
COLUMN = dict(width_mm=400, height_mm=500, bars=[dict(x_mm=x, y_mm=y, diameter_mm=28) for y in (-200, 200) for x in (-150, 150)])
# reference: (section, N, M total, M sustained, {quantity: (value, source)})
CASES = [
    ('slab_example43', SLAB, 0, -60, -50, {
        'long_mm': [(0.20, 'Posobie SP52-101 ex.43, force method, psi_s=1-0.8Mcrc/M'),
                    (0.306, 'SCAD/ARBAT test 10, NDM theory'), (0.292, 'ARBAT 21 (test 10, 0.974 x 0.3 mm)')],
        'sigma_long_mpa': [(235.9, 'Posobie ex.43'), (236.692, 'SCAD NDM')],
        'sigma_crc_mpa': [(22.651, 'SCAD NDM at Mcrc (8.2.14)')],
        'ls_mm': [(400, 'Posobie ex.43')]}),
    ('column_example44', COLUMN, -500, -240, -150, {
        'short_mm': [(0.36, 'Posobie SP52-101 ex.44, acrc1+acrc2-acrc3')],
        'long_mm': [(0.174, 'Posobie ex.44 acrc1 from its sigma_s,l=144.3, psi_s=0.861')],
        'sigma_long_mpa': [(144.3, 'Posobie ex.44')], 'sigma_total_mpa': [(331.2, 'Posobie ex.44')],
        'ls_mm': [(400, 'Posobie ex.44')]}),
]


def run(runner, mode):
    rows = []
    for name, rect, n, m, ml, refs in CASES:
        req = dict(schema_version=1, sls_execution_policy='research_all',
                   pile=dict(diameter_mm=0, mesh_resolution=81, polygon_sides=0), materials=ULS,
                   loads=[dict(id='uls_dummy_no_approval', n_kn=0, mx_knm=1, my_knm=0)],
                   candidates=[dict(id=name, bar_count=len(rect['bars']), bar_diameter_mm=rect['bars'][0]['diameter_mm'],
                                    rotation_deg=0, bar_axis_radius_mm=0)],
                   research_rectangle=dict(rect, mesh_resolution=81),
                   sls_probe=dict(short_materials=B15, long_materials=B15, phi2=0.5, acrc_ult_long_mm=0.3,
                                  acrc_ult_short_mm=0.4, preliminary_method=mode, review_band=0.03,
                                  loads=[dict(id='split', n_kn=n, mx_total_knm=m, my_total_knm=0, mx_long_knm=ml, my_long_knm=0),
                                         dict(id='total', n_kn=n, mx_total_knm=m, my_total_knm=0, mx_long_knm=m, my_long_knm=0),
                                         dict(id='sustained', n_kn=n, mx_total_knm=ml, my_total_knm=0, mx_long_knm=ml, my_long_knm=0)]))
        command = ['dotnet', str(runner)] if runner.suffix.lower() == '.dll' else [str(runner)]
        out = subprocess.run(command, input=json.dumps(req), capture_output=True, text=True, timeout=300, check=True)
        probes = {p['id']: p for p in json.loads(out.stdout)['cases'][0]['sls_probe']}
        split, total, sustained = probes['split'], probes['total'], probes['sustained']
        assert split['status'] == 'research_calculated', split
        ours = dict(long_mm=split['preliminary_width_long_mm'], short_mm=split['preliminary_width_short_mm'],
                    sigma_long_mpa=sustained['sigma_rebar_kpa'] / 1000, sigma_total_mpa=total['sigma_rebar_kpa'] / 1000,
                    sigma_crc_mpa=(sustained.get('sigma_rebar_precrack_kpa') or 0) / 1000, ls_mm=sustained['crack_spacing_m'] * 1000)
        for key, references in refs.items():
            for value, source in references:
                rows.append(dict(case=name, method=mode, quantity=key, reference=value, product=round(ours[key], 4),
                                 dev_pct=round((ours[key] / value - 1) * 100, 1), source=source))
    return rows


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('--runner', type=Path, required=True)
    ap.add_argument('--out', type=Path)
    a = ap.parse_args()
    rows = run(a.runner.resolve(), 'arbat_conservative') + [r for r in run(a.runner.resolve(), 'native_refined')
                                                           if r['quantity'] in ('long_mm', 'short_mm')]
    for r in rows:
        print(f"{r['case']:17} {r['method']:18} {r['quantity']:15} ref={r['reference']:<8} ours={r['product']:<8} {r['dev_pct']:+6.1f}%  {r['source']}")
    if a.out:
        a.out.parent.mkdir(parents=True, exist_ok=True)
        a.out.write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding='utf-8')
