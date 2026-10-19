#!/usr/bin/env python3
"""Run the seven ARBAT 21.1.9.3 reports (7 sections x 10 combinations) through the
product's manual single-section check and compare every printed utilization.

No resistance formulas or fitted values: each combination is one call of
workbench.model.check_adopted(single_section=True), as the manual UI does.
Input: the original RTF files (text is extracted with pandoc, cp1251 restored).
Geometry convention: ARBAT a1 = face to longitudinal bar surface (repo convention).
The 400x400 two-row layout is not expressible in the manual form; its explicit
bar coordinates are passed to the same runner request.
"""
import argparse, csv, json, re, subprocess, sys
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]; sys.path.insert(0, str(ROOT))
from workbench import model
import wall_rebar as wall

TIE = 8.0
LABELS = {'normal': 'Прочность по предельному моменту сечения',
          'crack_short': 'Ширина раскрытия трещин (кратковременная)',
          'crack_long': 'Ширина раскрытия трещин (длительная)',
          'n_ncr': 'Продольная сила при учете прогиба при гибкости L0/i>14'}
CHECKS = {'normal': 'normal_member_strength', 'crack_short': 'crack_width_short', 'crack_long': 'crack_width_long'}


def num(s): return float(s.replace(',', '.'))


def rtf_text(path):
    raw = subprocess.run(['pandoc', '-f', 'rtf', '-t', 'plain', '--wrap=none', str(path)],
                         capture_output=True, text=True, check=True).stdout
    # pandoc maps the cp1251 code page bytes to Latin-1 code points.
    text = ''.join(bytes([ord(ch)]).decode('cp1251') if 0x80 <= ord(ch) <= 0xff else ch for ch in raw)
    return re.sub(r'\n\s*\n+', '\n', text)


def parse(text):
    head = {k: float(m.group(1)) for k in ('b', 'h', 'D', 'a₁') if (m := re.search(k + r' = (\d+) мм', text))}
    m = re.search(r'S₁ - (\d+)Ж(\d+)', text); head['n1'], head['d'] = int(m.group(1)), float(m.group(2))
    m = re.search(r'S₂ - (\d+)Ж', text); head['n2'] = int(m.group(1)) if m else None
    head['two_rows'] = 'второй ряд' in text
    cases = []
    for block in re.split(r'\n\s*кН\s+кН\*м[^\n]*\n', text)[1:]:
        f = block.split('\n', 1)[0].split()
        c = dict(case=int(f[0]), n=num(f[1]), my=num(f[2]), qz=num(f[3]), mz=num(f[4]), gamma=num(f[7]), psi=num(f[8]), ratios={})
        for line in block.split('\n')[2:]:
            if line.startswith('Коэффициент использования') or line.strip().startswith('---'):
                break
            parts = [x for x in re.split(r'\s{2,}', line.strip()) if x]
            if len(parts) >= 2 and re.fullmatch(r'\d+(,\d+)?', parts[-1]):
                c['ratios'][parts[-2]] = num(parts[-1])
        cases.append(c)
    return head, cases


def project(head, c, sls_raw=False):
    p = json.loads((ROOT / 'examples/verification/member_length15_manual.json').read_text(encoding='utf-8'))
    a, d = head['a₁'], head['d']
    cover = a - TIE
    p['title'] = 'ARBAT 21.1.9.3 reproduction'
    if 'D' in head:
        p.update(kind='pile', force_scale='per_member', pile_background=head['n1'], pile_diameter=d)
        p['pile'].update(diameter_mm=head['D'], cover_mm=cover, ties_diameter_mm=TIE)
        p['transverse']['diameter_mm'] = TIE
        k = 1
    else:
        p.update(kind='wall', background=[head['n1'], head['n2']], diameters=[d, d])
        p['wall'].update(panel_width_mm=head['b'], thickness_mm=head['h'], cover_mm=cover, end_cover_mm=cover,
                         ties_diameter_mm=TIE, cage_width_mm=head['b'] - 2 * cover, min_clear_spacing_mm=20)
        k = 1000 / head['b']  # the wall form takes actions per metre and multiplies by panel width once
    n, mx, my = c['n'] * k, c['my'] * k, c['mz'] * k
    p['rows'] = [dict(z=0, case='U', basis='ULS', n=n, m=mx, my=my, q=0, m_long=None),
                 dict(z=0, case='S', basis='SLS', n=n, m=mx, my=my, q=0, m_long=mx * c['psi'], my_long=my * c['psi'])]
    p['calculation_context'].update(second_order_mode='cscore_member', l0_x_m=3, l0_y_m=3, psi=c['psi'])
    p['member_load_preparation'] = dict(mode='sp63', length_m=3, structural_scheme='statically_determinate')
    p['eccentricity_confirmed'] = False
    if sls_raw:  # diagnostic: raw actions, no ea and no eta (affects SLS of compressed cases)
        p['member_load_preparation'] = {'mode': 'source'}; p['eccentricity_confirmed'] = True
        p['calculation_context']['second_order_mode'] = 'section_only'
    return p


def engine_for(head, runner):
    def engine(req):
        if head['two_rows'] and 'research_rectangle' in req:
            d = head['d']; surf = head['a₁']
            ex, ey = head['b'] / 2 - surf - d / 2, head['h'] / 2 - surf - d / 2
            ey2 = ey - d - 20  # 20 mm clear distance between rows, as stated in the report
            req['research_rectangle']['bars'] = [dict(x_mm=x, y_mm=y, diameter_mm=d) for y in (-ey, -ey2, ey2, ey) for x in (-ex, ex)]
        return wall.invoke_engine(runner, req)
    return engine


def run(head, c, runner, sls_raw=False):
    r = model.check_adopted(project(head, c, sls_raw), engine=engine_for(head, runner), runner=runner, single_section=True)
    checks = r['stations'][0]['report']['checks']
    out = {}
    for key, cid in CHECKS.items():
        rows = [k for k in checks if k['check_id'] == cid]
        vals = [k for k in rows if k['utilization'] is not None]
        best = max(vals, key=lambda k: k['utilization']) if vals else None
        out[key] = dict(value=best and best['utilization'], status='/'.join(sorted({k['status'] for k in rows})),
                        native=best and (best.get('details') or {}).get('native_utilization'))
    ncr = [abs(c['n']) / min(k['details']['ncr_x_kn'], k['details']['ncr_y_kn']) for k in checks
           if k['check_id'] == 'member_stability' and (k.get('details') or {}).get('ncr_x_kn')]
    out['n_ncr'] = dict(value=max(ncr) if ncr and c['n'] < 0 else None, status='', native=None)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--rtf-dir', required=True, type=Path)
    ap.add_argument('--runner', required=True, type=Path)
    ap.add_argument('--out', default=ROOT / 'results/arbat-seven-full', type=Path)
    a = ap.parse_args(); a.out.mkdir(parents=True, exist_ok=True)
    rows, reference = [], []
    for path in sorted(a.rtf_dir.glob('*.rtf')):
        head, cases = parse(rtf_text(path))
        reference.append(dict(file=path.name, head=head, cases=cases))
        for c in cases:
            ours = run(head, c, a.runner)
            raw = run(head, c, a.runner, sls_raw=True) if c['n'] < 0 else None
            for key, label in LABELS.items():
                ref, o = c['ratios'].get(label), ours[key]
                if ref is None and not o['value']:
                    continue
                dev = lambda v: None if not ref or v is None else round((v - ref) / ref * 100, 2)
                rows.append(dict(file=path.name, case=c['case'], n=c['n'], my=c['my'], mz=c['mz'], psi=c['psi'], check=key,
                                 arbat=ref, product=o['value'] and round(o['value'], 4), dev_pct=dev(o['value']), status=o['status'],
                                 native=o['native'] and round(o['native'], 4), native_dev_pct=dev(o['native']),
                                 sls_raw=round(raw[key]['value'], 4) if raw and key.startswith('crack') and raw[key]['value'] is not None else None,
                                 sls_raw_dev_pct=dev(raw[key]['value']) if raw and key.startswith('crack') else None))
            print(path.name, c['case'], flush=True)
    (a.out / 'arbat-reference.json').write_text(json.dumps(reference, ensure_ascii=False, indent=1), encoding='utf-8')
    with open(a.out / 'comparison.csv', 'w', newline='', encoding='utf-8-sig') as fh:
        w = csv.DictWriter(fh, fieldnames=list(rows[0]), delimiter=';'); w.writeheader(); w.writerows(rows)


if __name__ == '__main__':
    main()
