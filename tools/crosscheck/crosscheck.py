"""Independent cross-check of CScore against structuralcodes and concreteproperties.

For a set of sections (piles, a wall strip, a column) the same SP 63 material diagrams
are given to three programs:

* CScore through CScorePileRunner (the engine of the application);
* structuralcodes (fib-international, Apache 2.0);
* concreteproperties (R. van Leeuwen, MIT).

Compared: the N–M interaction (Mult at fixed N), the squash and tension loads, the
moment–curvature diagram at a fixed N. The SP 63 tables of the application are
compared with structural-materials (rumata-ap, MIT).

The concrete diagram is the two-line one of SP 63 6.1.21 (Rb on 0.0015…0.0035, no
tension at the ultimate state, as CScore ten=false), the bars are elastic–plastic
with the strain limit 0.025: exactly what the application sends to CScore
(SectionCalc.MaterialTables.Engine).

Run (developer tool, not shipped):

    pip install -r tools/crosscheck/requirements.txt
    dotnet build src/CScorePileRunner -c Release
    python tools/crosscheck/crosscheck.py --out docs/crosscheck
"""
from __future__ import annotations

import argparse
import json
import math
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
RUNNER = ROOT / 'src/CScorePileRunner/bin/Release/net10.0/CScorePileRunner.dll'

# Strains of the application's request (MaterialTables.Engine).
EC1, EC2, ET1, ET2, ES_U = 0.0015, 0.0035, 0.00008, 0.00015, 0.025
POLYGON_SIDES, MESH = 96, 41


@dataclass
class Bar:
    x: float  # mm
    y: float  # mm
    d: float  # mm

    @property
    def area(self) -> float:
        return math.pi * self.d ** 2 / 4


@dataclass
class Case:
    key: str
    title: str
    concrete: str
    rebar: str
    outline: list[tuple[float, float]]  # mm, closed implicitly
    bars: list[Bar]
    pile: dict | None = None  # runner circular description
    rectangle: dict | None = None  # runner research_rectangle
    n_curvature: float = 0.0  # kN, N of the moment-curvature diagram (- compression)
    short_term: bool = False
    rb: float = 0.0
    rbt: float = 0.0
    eb: float = 0.0
    rs: float = 0.0
    rsc: float = 0.0
    es: float = 0.0
    results: dict = field(default_factory=dict)


def circle(diameter: float) -> list[tuple[float, float]]:
    r = diameter / 2
    return [(r * math.cos(2 * math.pi * i / POLYGON_SIDES), r * math.sin(2 * math.pi * i / POLYGON_SIDES))
            for i in range(POLYGON_SIDES)]


def pile_case(key, title, diameter, count, d, axis_radius, concrete, rebar, n_curvature) -> Case:
    bars = [Bar(axis_radius * math.cos(2 * math.pi * i / count), axis_radius * math.sin(2 * math.pi * i / count), d)
            for i in range(count)]
    return Case(key, title, concrete, rebar, circle(diameter), bars,
                pile={'diameter_mm': diameter, 'count': count, 'd': d, 'axis_radius': axis_radius},
                n_curvature=n_curvature)


def rect_case(key, title, width, height, bars, concrete, rebar, n_curvature) -> Case:
    w, h = width / 2, height / 2
    return Case(key, title, concrete, rebar, [(-w, -h), (w, -h), (w, h), (-w, h)], bars,
                rectangle={'width_mm': width, 'height_mm': height}, n_curvature=n_curvature)


def face(count, d, y, width, axis_x):
    xs = np.linspace(-width / 2 + axis_x, width / 2 - axis_x, count)
    return [Bar(float(x), y, d) for x in xs]


def cases() -> list[Case]:
    return [
        pile_case('pile800', 'Свая Ø800, 12Ø25 A500, B30', 800, 12, 25, 317.5, 'B30', 'A500', -1500),
        pile_case('pile1200', 'Свая Ø1200, 24Ø32 A500, B35', 1200, 24, 32, 514, 'B35', 'A500', -3000),
        rect_case('wall800', 'Стена 1000×800 (1 п. м), 5Ø28 + 5Ø20 A500, B30', 1000, 800,
                  face(5, 28, -400 + 70, 1000, 100) + face(5, 20, 400 - 60, 1000, 100), 'B30', 'A500', 0),
        rect_case('col400x600', 'Колонна 400×600, 8Ø20 A400, B25', 400, 600,
                  face(3, 20, -300 + 50, 400, 50) + face(3, 20, 300 - 50, 400, 50)
                  + [Bar(-150, 0, 20), Bar(150, 0, 20)], 'B25', 'A400', -800),
    ]


# ---------------------------------------------------------------- materials

def app_tables() -> tuple[dict, dict]:
    """The SP 63 tables of the application, read from Materials.cs."""
    text = (ROOT / 'src/SectionCalc/Materials.cs').read_text(encoding='utf-8')
    num = r'\s*([\d.]+)'
    concretes = {m[0]: dict(zip(['Rb', 'Rbt', 'Rb_ser', 'Rbt_ser', 'Eb'], map(float, m[1:])))
                 for m in re.findall(r'new\("(B\d+)",' + ','.join([num] * 5) + r'\)', text)}
    rebars = {m[0]: dict(zip(['Rs', 'Rsc', 'Rsc_short', 'Rs_ser', 'Rsw', 'Es'], map(float, m[1:])))
              for m in re.findall(r'new\("(A\d+)",' + ','.join([num] * 6) + r'\)', text)}
    if not concretes or not rebars:
        raise RuntimeError('Таблицы Materials.cs не разобраны')
    return concretes, rebars


def library_tables(names_c, names_r) -> tuple[dict, dict]:
    from structural_materials import sp63_materials as sp
    concretes = {}
    for g in names_c:
        c = sp.Concrete(g, long_term=False).to_dict()
        concretes[g] = {'Rb': c['Rb_base_MPa'], 'Rbt': c['Rbt_base_MPa'], 'Rb_ser': c['Rb_ser_MPa'],
                        'Rbt_ser': c['Rbt_ser_MPa'], 'Eb': c['Eb_initial_MPa']}
    rebars = {}
    for g in names_r:
        long_, short = sp.Rebar(g, long_term=True).to_dict(), sp.Rebar(g, long_term=False).to_dict()
        rebars[g] = {'Rs': long_['Rs_design_MPa'], 'Rsc': long_['Rsc_design_MPa'], 'Rsc_short': short['Rsc_design_MPa'],
                     'Rs_ser': long_['Rs_ser_MPa'], 'Rsw': long_['Rsw_MPa'], 'Es': long_['Es_MPa']}
    return concretes, rebars


def compare_tables() -> list[dict]:
    app_c, app_r = app_tables()
    lib_c, lib_r = library_tables(app_c, app_r)
    rows = []
    for kind, app, lib in (('бетон', app_c, lib_c), ('арматура', app_r, lib_r)):
        for grade, values in app.items():
            for key, v in values.items():
                ref = lib[grade][key]
                rows.append({'kind': kind, 'grade': grade, 'value': key, 'app': v, 'library': ref,
                             'same': abs(v - ref) <= 1e-9 * max(1, abs(ref))})
    return rows


def assign_materials(case: Case, gb1: float = 0.9):
    """Design values the application sends for an ordinary (not short-term) combination: γb1 to Rb and Rbt."""
    app_c, app_r = app_tables()
    c, s = app_c[case.concrete], app_r[case.rebar]
    case.rb, case.rbt, case.eb = c['Rb'] * gb1, c['Rbt'] * gb1, c['Eb']
    case.rs, case.es = s['Rs'], s['Es']
    case.rsc = s['Rsc_short'] if case.short_term else s['Rsc']


# ---------------------------------------------------------------- CScore

def runner_request(case: Case, loads: list[dict]) -> dict:
    req = {
        'schema_version': 1,
        'materials': {
            'concrete_rb_kpa': case.rb * 1000, 'concrete_rbt_kpa': case.rbt * 1000, 'concrete_e_kpa': case.eb * 1000,
            'concrete_ec1': -EC1, 'concrete_ec2': -EC2, 'concrete_et1': ET1, 'concrete_et2': ET2,
            'rebar_rs_kpa': case.rs * 1000, 'rebar_e_kpa': case.es * 1000, 'rebar_ec2': -ES_U, 'rebar_et2': ES_U,
        },
        'loads': loads,
    }
    if case.pile:
        p = case.pile
        req['pile'] = {'diameter_mm': p['diameter_mm'], 'mesh_resolution': MESH, 'polygon_sides': POLYGON_SIDES}
        req['candidates'] = [{'id': case.key, 'bar_count': p['count'], 'bar_diameter_mm': p['d'],
                              'bar_axis_radius_mm': p['axis_radius'], 'rotation_deg': 0,
                              'steel_area_mm2': p['count'] * math.pi * p['d'] ** 2 / 4, 'clearance_mm': 50}]
    else:
        r = case.rectangle
        req['pile'] = {'diameter_mm': 0, 'mesh_resolution': MESH, 'polygon_sides': 0}
        req['research_rectangle'] = {'width_mm': r['width_mm'], 'height_mm': r['height_mm'], 'mesh_resolution': MESH,
                                     'bars': [{'x_mm': b.x, 'y_mm': b.y, 'diameter_mm': b.d} for b in case.bars]}
        req['candidates'] = [{'id': case.key, 'bar_count': len(case.bars), 'bar_diameter_mm': case.bars[0].d,
                              'bar_axis_radius_mm': 0, 'rotation_deg': 0,
                              'steel_area_mm2': sum(b.area for b in case.bars), 'clearance_mm': 50}]
    return req


def run_runner(request: dict, command: str | None = None) -> dict:
    argv = ['dotnet', str(RUNNER)] + ([command] if command else [])
    p = subprocess.run(argv, input=json.dumps(request, allow_nan=False), capture_output=True, text=True,
                       encoding='utf-8', timeout=1800)
    if p.returncode != 0:
        raise RuntimeError(f'CScorePileRunner: {p.stderr.strip()[-2000:]}')
    return json.loads(p.stdout)


def cscore_axial(case: Case) -> tuple[float, float]:
    """Squash (compression, kN < 0) and tension (kN > 0) loads: AllFactor along ±N."""
    out = run_runner(runner_request(case, [{'id': 'c', 'n_kn': -1, 'mx_knm': 0, 'my_knm': 0},
                                           {'id': 't', 'n_kn': 1, 'mx_knm': 0, 'my_knm': 0}]))
    loads = {l['id']: l for l in out['cases'][0]['loads']}
    return -loads['c']['factor'], loads['t']['factor']


def cscore_nm(case: Case, ns: list[float]) -> dict[str, list]:
    """Mult at fixed N (MomentFactor) for both signs of the moment about x."""
    loads = []
    for i, n in enumerate(ns):
        loads.append({'id': f'p{i}', 'n_kn': n, 'mx_knm': 1, 'my_knm': 0})
        loads.append({'id': f'm{i}', 'n_kn': n, 'mx_knm': -1, 'my_knm': 0})
    out = run_runner(runner_request(case, loads))
    by = {l['id']: l for l in out['cases'][0]['loads']}
    pos = [by[f'p{i}'].get('moment_factor') for i in range(len(ns))]
    neg = [(-by[f'm{i}']['moment_factor'] if by[f'm{i}'].get('moment_factor') is not None else None) for i in range(len(ns))]
    return {'pos': pos, 'neg': neg}


def cscore_curvature(case: Case) -> dict:
    req = runner_request(case, [{'id': 'u', 'n_kn': case.n_curvature, 'mx_knm': 1, 'my_knm': 0}])
    req['moment_curvature'] = {'n_kn': case.n_curvature, 'mx_knm': 1, 'my_knm': 0, 'calc_type': 'C',
                               'use_psi': False, 'aux_points_per_segment': 12}
    return run_runner(req, '--moment-curvature')


# ---------------------------------------------------------------- structuralcodes

def sc_section(case: Case, tension: bool, rsc: float | None = None):
    from shapely.geometry import Polygon
    from structuralcodes.geometry import SurfaceGeometry, add_reinforcement
    from structuralcodes.materials.basic import GenericMaterial
    from structuralcodes.materials.constitutive_laws import UserDefined
    from structuralcodes.sections import BeamSection

    if tension:
        cx, cy = [-EC2, -EC1, 0, ET1, ET2], [-case.rb, -case.rb, 0, case.rbt, case.rbt]
    else:
        cx, cy = [-EC2, -EC1, 0, 1], [-case.rb, -case.rb, 0, 0]
    concrete = GenericMaterial(2400, UserDefined(cx, cy, flag=0), name='concrete')
    rc = rsc or case.rs
    steel = GenericMaterial(7850, UserDefined([-ES_U, -rc / case.es, 0, case.rs / case.es, ES_U],
                                              [-rc, -rc, 0, case.rs, case.rs], flag=0), name='steel')
    # Concrete is displaced by the bars, as in CScore (CreateRebarArea) and concreteproperties (add_bar).
    from shapely.geometry import Point
    poly = Polygon(case.outline)
    for b in case.bars:
        poly = poly.difference(Point(b.x, b.y).buffer(b.d / 2, quad_segs=8))
    geo = SurfaceGeometry(poly, concrete, concrete=True)
    for b in case.bars:
        geo = add_reinforcement(geo, (b.x, b.y), b.d, steel)
    return BeamSection(geo, integrator='marin')


def sc_nm(case: Case, ns: list[float], rsc: float | None = None) -> dict[str, list]:
    sec = sc_section(case, tension=False, rsc=rsc)
    out = {'pos': [], 'neg': []}
    for n in ns:
        for key, theta in (('pos', math.pi), ('neg', 0.0)):
            try:
                r = sec.section_calculator.calculate_bending_strength(theta=theta, n=n * 1e3)
                out[key].append(abs(r.m_y) / 1e6 * (1 if key == 'pos' else -1))
            except ValueError:
                out[key].append(None)
    return out


def sc_axial(case: Case) -> tuple[float, float]:
    sec = sc_section(case, tension=False)
    lim = sec.section_calculator.calculate_limit_axial_load()
    return lim[0] / 1e3, lim[1] / 1e3


def sc_curvature(case: Case, chi_max: float) -> dict:
    sec = sc_section(case, tension=True)
    chi = np.linspace(0, chi_max, 120)[1:] / 1e3  # 1/m → 1/mm
    r = sec.section_calculator.calculate_moment_curvature(theta=math.pi, n=case.n_curvature * 1e3, chi=chi)
    return {'chi': (np.abs(np.asarray(r.chi_y)) * 1e3).tolist(), 'm': (np.abs(np.asarray(r.m_y)) / 1e6).tolist()}


# ---------------------------------------------------------------- concreteproperties

def cp_section(case: Case):
    from concreteproperties.concrete_section import ConcreteSection
    from concreteproperties.material import Concrete, SteelBar
    from concreteproperties.pre import add_bar
    from concreteproperties.stress_strain_profile import (BilinearStressStrain, ConcreteServiceProfile,
                                                           SteelElasticPlastic)
    from sectionproperties.pre import Geometry
    from shapely.geometry import Polygon

    # concreteproperties: compression is positive.
    # Cracked concrete carries nothing beyond εbt2: a zero branch up to the bar strain limit.
    service = ConcreteServiceProfile(strains=[-ES_U, -ET2 * 1.001, -ET2, -ET1, 0, EC1, EC2],
                                     stresses=[0, 0, -case.rbt, -case.rbt, 0, case.rb, case.rb],
                                     ultimate_strain=EC2)
    concrete = Concrete('concrete', 2.4e-6, service,
                        ultimate_stress_strain_profile=BilinearStressStrain(case.rb, EC1, EC2),
                        flexural_tensile_strength=case.rbt, colour='lightgrey')
    steel = SteelBar('steel', 7.85e-6, SteelElasticPlastic(case.rs, case.es, ES_U), colour='grey')
    geom = Geometry(Polygon(case.outline), material=concrete)
    for b in case.bars:
        geom = add_bar(geom, b.area, steel, b.x, b.y, n=16)
    return ConcreteSection(geom, moment_centroid=(0.0, 0.0), geometric_centroid_override=True)


def cp_nm(case: Case, ns: list[float]) -> dict[str, list]:
    sec = cp_section(case)
    out = {'pos': [], 'neg': []}
    for n in ns:
        for key, theta in (('pos', math.pi / 2), ('neg', -math.pi / 2)):
            try:
                r = sec.ultimate_bending_capacity(theta=theta, n=-n * 1e3)
                out[key].append(abs(r.m_xy) / 1e6 * (1 if key == 'pos' else -1))
            except Exception:  # noqa: BLE001 — outside the domain
                out[key].append(None)
    return out


def cp_curvature(case: Case, chi_max: float) -> dict:
    sec = cp_section(case)
    r = sec.moment_curvature_analysis(theta=math.pi / 2, n=-case.n_curvature * 1e3, kappa_inc=2e-7,
                                      kappa_inc_max=chi_max / 1e3 / 60, progress_bar=False)
    return {'chi': (np.abs(np.asarray(r.kappa)) * 1e3).tolist(), 'm': (np.abs(np.asarray(r.m_xy)) / 1e6).tolist()}


# ---------------------------------------------------------------- report

def deviation(a, b, scale=None):
    """(a − b) / |b|, or / scale: the N–M curves are compared against their largest moment,
    otherwise the tip at the squash load (M → 0) blows the percentage up."""
    base = scale if scale is not None else (abs(b) if b is not None else 0)
    if a is None or b is None or base < 1e-9:
        return None
    return (a - b) / base


def run_case(case: Case) -> dict:
    assign_materials(case)
    nc, nt = cscore_axial(case)
    ns = [float(x) for x in np.linspace(nc * 0.95, nt * 0.9, 14)]
    res = {'title': case.title, 'materials': {'Rb': case.rb, 'Rbt': case.rbt, 'Eb': case.eb, 'Rs': case.rs,
                                              'Rsc_table': case.rsc, 'Es': case.es},
           'axial': {'cscore': [nc, nt], 'structuralcodes': list(sc_axial(case))}, 'n': ns}
    res['nm'] = {'cscore': cscore_nm(case, ns), 'structuralcodes': sc_nm(case, ns), 'concreteproperties': cp_nm(case, ns)}
    # Influence of Rsc ≠ Rs (table 6.14, short-term: 400 for A500), not modelled by CScore.
    rebar = app_tables()[1][case.rebar]
    if rebar['Rsc_short'] < rebar['Rs']:
        res['nm']['structuralcodes_rsc_short'] = sc_nm(case, ns, rsc=rebar['Rsc_short'])
    mc = cscore_curvature(case)
    pts = [p for p in mc['points'] if p and p['converged']]
    chi = [math.hypot(p['ky_1m'], p['kz_1m']) for p in pts]
    m = [math.hypot(p['mx_knm'], p['my_knm']) for p in pts]
    chi_max = max(chi) * 1.05 if chi else 0.02
    res['mk'] = {'n': case.n_curvature, 'cscore': {'chi': chi, 'm': m, 'status': mc['status'],
                                                   **{k: mc[k] for k in ('cracking', 'yield', 'ultimate')}},
                 'structuralcodes': sc_curvature(case, chi_max), 'concreteproperties': cp_curvature(case, chi_max)}
    return res


def summary(res: dict) -> dict:
    out = {}
    pairs = [('cscore', 'structuralcodes'), ('cscore', 'concreteproperties'),
             ('structuralcodes_rsc_short', 'structuralcodes')]
    for mine, other in pairs:
        if mine not in res['nm']:
            continue
        name = other if mine == 'cscore' else 'rsc_short_effect'
        scale = max(abs(v) for key in ('pos', 'neg') for v in res['nm'][other][key] if v is not None)
        devs = [deviation(abs(a), abs(b), scale) for key in ('pos', 'neg')
                for a, b in zip(res['nm'][mine][key], res['nm'][other][key]) if a is not None and b is not None]
        devs = [d for d in devs if d is not None]
        out[name] = {'max_abs': max(map(abs, devs)) if devs else None,
                      'mean': float(np.mean(devs)) if devs else None, 'count': len(devs)}
    mk = res['mk']
    ult = mk['cscore']['ultimate']
    if ult:
        m_ult = math.hypot(ult['mx_knm'], ult['my_knm'])
        for other in ('structuralcodes', 'concreteproperties'):
            out[f'mk_{other}_mmax'] = deviation(m_ult, max(mk[other]['m']) if mk[other]['m'] else None)
        # Moment at the curvature of the CScore yield point.
        y = mk['cscore']['yield']
        if y:
            k = math.hypot(y['ky_1m'], y['kz_1m'])
            for other in ('structuralcodes', 'concreteproperties'):
                c = mk[other]
                out[f'mk_{other}_at_yield'] = deviation(math.hypot(y['mx_knm'], y['my_knm']),
                                                        float(np.interp(k, c['chi'], c['m'])) if c['chi'] else None)
    return out


def plots(res: dict, key: str, out: Path):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    colours = {'cscore': '#2F6ED8', 'structuralcodes': '#E07A1F', 'concreteproperties': '#2E9E5B',
               'structuralcodes_rsc_short': '#C23B6B'}
    names = {'cscore': 'CScore', 'structuralcodes': 'structuralcodes', 'concreteproperties': 'concreteproperties',
             'structuralcodes_rsc_short': 'structuralcodes, Rsc = 400 (кратковр.)'}
    fig, (a, b) = plt.subplots(1, 2, figsize=(12, 5))
    for tool, data in res['nm'].items():
        for sign in ('pos', 'neg'):
            pts = [(m, n) for m, n in zip(data[sign], res['n']) if m is not None]
            if pts:
                a.plot(*zip(*pts), '-' if tool == 'cscore' else '--', color=colours[tool], lw=2 if tool == 'cscore' else 1.3,
                       marker='o' if tool == 'cscore' else None, ms=3, label=names[tool] if sign == 'pos' else None)
    a.invert_yaxis()
    a.set_xlabel('Mult, кН·м'); a.set_ylabel('N, кН (− сжатие)'); a.set_title('Диаграмма N–M')
    a.grid(alpha=.3); a.legend(fontsize=8)
    for tool in ('cscore', 'structuralcodes', 'concreteproperties'):
        c = res['mk'][tool]
        b.plot(c['chi'], c['m'], '-' if tool == 'cscore' else '--', color=colours[tool], lw=2 if tool == 'cscore' else 1.3,
               label=names[tool])
    for point in ('cracking', 'yield', 'ultimate'):
        p = res['mk']['cscore'].get(point)
        if p:
            b.plot(math.hypot(p['ky_1m'], p['kz_1m']), math.hypot(p['mx_knm'], p['my_knm']), 'o', color=colours['cscore'])
    b.set_xlabel('κ, 1/м'); b.set_ylabel('M, кН·м'); b.set_title(f"Момент–кривизна, N = {res['mk']['n']:g} кН")
    b.grid(alpha=.3); b.legend(fontsize=8)
    fig.suptitle(res['title'])
    fig.tight_layout()
    fig.savefig(out / f'{key}.png', dpi=110)
    plt.close(fig)


def pct(v):
    return '—' if v is None else f'{v * 100:+.1f} %'


def report(results: dict, tables: list[dict], out: Path):
    lines = ['# Сверка CScore с независимыми расчётами', '',
             'Сгенерировано `tools/crosscheck/crosscheck.py`. Одинаковые диаграммы СП 63 (двухлинейный бетон, '
             'упругопластическая арматура, εs,u = 0,025) заданы в трёх программах. Отклонение Mult = (CScore − другая) / '
             'наибольший Mult кривой другой программы; для M–κ — к величине другой программы.', '',
             '## Таблицы материалов: приложение и structural-materials', '']
    diff = [r for r in tables if not r['same']]
    lines.append(f"Сравнено значений: {len(tables)}, расхождений: {len(diff)}.")
    if diff:
        lines += ['', '| Материал | Величина | Приложение | structural-materials |', '|---|---|---|---|']
        lines += [f"| {r['grade']} | {r['value']} | {r['app']:g} | {r['library']:g} |" for r in diff]
    lines += ['', '## Несущая способность и момент–кривизна', '',
              '| Сечение | ΔMult (N–M): structuralcodes | ΔMult (N–M): concreteproperties | Nc / Nt: CScore и structuralcodes, кН | '
              'Mmax M–κ: sc / cp | M в точке текучести: sc / cp |', '|---|---|---|---|---|---|']
    for key, res in results.items():
        s = res['summary']
        nm = lambda t: f"макс. {pct(s[t]['max_abs'])}, ср. {pct(s[t]['mean'])}" if t in s and s[t]['max_abs'] is not None else '—'
        ax = res['axial']
        lines.append(f"| {res['title']} | {nm('structuralcodes')} | {nm('concreteproperties')} | "
                     f"{ax['cscore'][0]:.0f} / {ax['cscore'][1]:.0f}; {ax['structuralcodes'][0]:.0f} / {ax['structuralcodes'][1]:.0f} | "
                     f"{pct(s.get('mk_structuralcodes_mmax'))} / {pct(s.get('mk_concreteproperties_mmax'))} | "
                     f"{pct(s.get('mk_structuralcodes_at_yield'))} / {pct(s.get('mk_concreteproperties_at_yield'))} |")
    rsc = {k: r['summary']['rsc_short_effect'] for k, r in results.items() if 'rsc_short_effect' in r['summary']}
    if rsc:
        lines += ['', '## Влияние Rsc = 400 МПа при кратковременной нагрузке (табл. 6.14)', '',
                  'CScore строит диаграмму арматуры симметрично по Rs и не учитывает Rsc. Ниже — изменение Mult '
                  'в structuralcodes при ограничении сжатия стержней 400 МПа (A500), в долях наибольшего Mult:', '']
        lines += [f"- {results[k]['title']}: макс. {pct(v['max_abs'])}, ср. {pct(v['mean'])}" for k, v in rsc.items()]
    lines += ['']
    for key, res in results.items():
        lines += [f"### {res['title']}", '', f"![{key}]({key}.png)", '']
    (out / 'README.md').write_text('\n'.join(lines), encoding='utf-8')


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--out', type=Path, default=ROOT / 'docs/crosscheck')
    ap.add_argument('--only', nargs='*', help='ключи сечений')
    ap.add_argument('--report-only', action='store_true', help='пересобрать отчёт и графики из results.json')
    args = ap.parse_args(argv)
    if args.report_only:
        data = json.loads((args.out / 'results.json').read_text(encoding='utf-8'))
        for key, res in data['cases'].items():
            res['summary'] = summary(res)
            plots(res, key, args.out)
        (args.out / 'results.json').write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding='utf-8')
        report(data['cases'], data['tables'], args.out)
        return
    if not RUNNER.exists():
        sys.exit(f'Нет {RUNNER}: dotnet build src/CScorePileRunner -c Release')
    args.out.mkdir(parents=True, exist_ok=True)
    tables = compare_tables()
    results = {}
    for case in cases():
        if args.only and case.key not in args.only:
            continue
        print(f'… {case.title}', flush=True)
        res = run_case(case)
        res['summary'] = summary(res)
        results[case.key] = res
        plots(res, case.key, args.out)
        print('  ', json.dumps(res['summary'], ensure_ascii=False), flush=True)
    (args.out / 'results.json').write_text(json.dumps({'tables': tables, 'cases': results}, ensure_ascii=False, indent=1),
                                           encoding='utf-8')
    report(results, tables, args.out)


if __name__ == '__main__':
    main()
