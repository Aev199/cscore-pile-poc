#!/usr/bin/env python3
"""Research geometry hypotheses against recorded ARBAT Q results; no own resistance law.

Neither the reference transverse model nor its spiral applicability is established.
The native CScore probe performs every capacity calculation. No coefficient fitting.
"""
import argparse
import csv
import json
import math
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
import pile_rebar


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--dotnet', default='dotnet')
    ap.add_argument('--probe', type=Path, required=True)
    ap.add_argument('--runner', type=Path, required=True)
    ap.add_argument('--out', type=Path, required=True)
    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    reference = json.loads((ROOT/'examples/verification/arbat_matched/arbat_reference_20261015.json').read_text())
    original = json.loads((ROOT/'docs/research/arbat-shear-data/original_report_factors.json').read_text())
    original_factors = {case['id']: case for group in original['groups'] for case in group['nonzero_q_cases']}
    jobs = []
    for name, group in reference['groups'].items():
        data = json.loads((ROOT/f'examples/verification/arbat_matched/{name}_gamma09_sensitivity.json').read_text())
        # Correct bar-axis radii for the original bar-surface a1=70 mm reports.
        data['pile']['cover_mm'] = 58
        candidates, _ = pile_rebar.make_candidates(data)
        if len(candidates) != 1:
            raise ValueError('Expected one calibrated cage')
        engine = pile_rebar.build_engine_request(data, candidates)
        r = group['diameter_mm']/2000
        a_input = group['a1_mm']/1000
        a_center = a_input+candidates[0]['bar_diameter_mm']/2000
        variants = []
        # a70 is an input-interpretation diagnostic, not the correct bar-centre offset.
        for tag, a in [('a70_input', a_input), ('a_center', a_center)]:
            square = math.sqrt(math.pi)*(r-a)
            for geom, b, h in [('arbat_square', square, square),
                               ('vniigs', 1.77*r, 1.77*r-a)]:
                for legs in (1, 2):
                    variants.append(dict(name=f'{geom}_{tag}_legs{legs}', b_m=b, h0_m=h, legs=legs))
        jobs.append(dict(group=name, engine=engine, reference=group['cases'], variants=variants))
    request = args.out/'arbat_shear_jobs.json'
    request.write_text(json.dumps(jobs, ensure_ascii=False, indent=2)+'\n')
    proc = subprocess.run([args.dotnet, str(args.probe.resolve()), str(args.runner.resolve()), str(request.resolve())],
                          capture_output=True, text=True, encoding='utf8', check=True)
    rows = json.loads(proc.stdout)
    if len(rows) != 128:
        raise ValueError('Incomplete native hypothesis matrix')
    (args.out/'arbat_shear_native.json').write_text(json.dumps(rows, ensure_ascii=False, indent=2)+'\n')
    flat = []
    for row in rows:
        native, ref = row['native_ratio'], row['arbat']
        factors = original_factors.get(row['id'])
        if factors is not None and factors['shear_inclined_utilization'] != ref:
            raise ValueError('Original RTF and previous reference disagree')
        strip_ref = factors['shear_strip_utilization'] if factors else None
        status = 'missing_reference' if ref is None else 'native_invalid' if native is None else 'numeric_comparison_only'
        rel = (native/ref-1)*100 if ref is not None and ref > 0 and native is not None else None
        flat.append({k: row[k] for k in ('group','id','variant','b_m','h0_m','legs','n_kn','m_knm','q_kn','arbat','native_ratio')} |
                    dict(status=status, relative_difference_percent=rel,
                         arbat_strip=strip_ref, native_strip_ratio=row['native_strip_ratio']) | (row.get('variables') or {}))
    fields = list(dict.fromkeys(k for row in flat for k in row))
    with (args.out/'arbat_shear_hypotheses.csv').open('w', encoding='utf8', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=fields, lineterminator='\n')
        writer.writeheader()
        writer.writerows(flat)
    print(f'{len(rows)} native diagnostic evaluations; no design approval or spiral validation')


if __name__ == '__main__':
    main()
