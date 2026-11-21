#!/usr/bin/env python3
"""Golden CScore requests from the verified Python single-section path.

The desktop application builds its requests in C#; SectionCalc.Tests compares them
with these files so both paths send the engine the same section and actions.
"""
import argparse, copy, json, sys
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]; sys.path.insert(0, str(ROOT))
from workbench import model

OUT = ROOT / 'tests/SectionCalc.Tests/golden'


def base(kind):
    p = json.loads((ROOT / 'examples/verification/member_length15_manual.json').read_text(encoding='utf-8'))
    p.update(kind=kind, eccentricity_confirmed=False, materials_confirmed=True, title='golden')
    p['calculation_context'].update(second_order_mode='cscore_member', l0_x_m=7, l0_y_m=6.5, psi=0.8)
    p['member_load_preparation'] = dict(mode='sp63', length_m=7, structural_scheme='statically_indeterminate')
    return p


def pile():
    p = base('pile')
    p.update(force_scale='per_member', pile_background=14, pile_diameter=25)
    p['pile'].update(diameter_mm=900, cover_mm=55, ties_diameter_mm=10, min_clear_spacing_mm=25)
    p['transverse'] = dict(mode='screen', diameter_mm=10, spacing_mm=150, effective_legs_per_cage=2,
                           rsw_kpa=170000, constructive_confirmed=True)
    p['calculation_context']['circular_shear'] = dict(method='vniigs_1984_two_legs', a_mm=90, winding_diameter_mm=None,
        circular_efficiency=None, confirmed=True,
        efficiency_basis='Принятая модель: один полный виток — две условные ветви; b = 0,885D, h0 = b − a.')
    p['rows'] = [dict(z=0, case='U', basis='ULS', n=-2000, m=320, my=40, q=150, m_long=None),
                 dict(z=0, case='S', basis='SLS', n=-1500, n_long=-1100, m=240, my=30, q=0, m_long=180, my_long=20)]
    return p


def wall():
    p = base('wall')
    p.update(force_scale='per_metre', background=[4, 5], diameters=[20, 25])
    p['wall'].update(panel_width_mm=1000, thickness_mm=600, cover_mm=50, end_cover_mm=50, ties_diameter_mm=10,
                     cage_width_mm=900, cage_count=1, min_clear_spacing_mm=25)
    p['transverse'] = dict(mode='screen', diameter_mm=10, spacing_mm=200, effective_legs_per_cage=4,
                           rsw_kpa=170000, constructive_confirmed=True)
    p['calculation_context'].update(inclined_moment_requested=True, anchorage_confirmed=True, anchorage_factor=0.8,
                                    sections=[dict(z=0, support_distance_positive_m=2.5, support_distance_negative_m=3.5)])
    p['rows'] = [dict(z=0, case='U', basis='ULS', n=-800, m=-350, my=0, q=200, m_long=None),
                 dict(z=0, case='S', basis='SLS', n=-600, m=-260, my=0, q=0, m_long=-200, my_long=0)]
    return p


def capture(p, runner):
    requests = []

    def engine(req):
        requests.append(copy.deepcopy(req))
        import wall_rebar
        return wall_rebar.invoke_engine(runner, req)
    model.check_adopted(p, engine=engine, runner=runner, single_section=True)
    return requests[0]


if __name__ == '__main__':
    ap = argparse.ArgumentParser(); ap.add_argument('--runner', type=Path, required=True)
    a = ap.parse_args(); OUT.mkdir(parents=True, exist_ok=True)
    for name, factory in (('pile', pile), ('wall', wall)):
        req = capture(factory(), a.runner.resolve())
        (OUT / f'{name}_request.json').write_text(json.dumps(req, ensure_ascii=False, indent=1), encoding='utf-8')
        print(name, 'loads', [l['id'] for l in req['loads']])
