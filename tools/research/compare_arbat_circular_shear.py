#!/usr/bin/env python3
"""Research geometry hypotheses against recorded ARBAT Q results; no own resistance law.

Neither the reference transverse model nor its spiral applicability is established.
The native CScore probe performs every capacity calculation. No coefficient fitting.
"""
import argparse
import csv
import json
import hashlib
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
    ap.add_argument('--strain-audit', action='store_true', help='Compare existing vs physical SP63 reference strains; native resistance unchanged')
    ap.add_argument('--include-concrete', action='store_true',
                    help='Add native qsw=0 controls; zero legs is a diagnostic, not an absent physical cage')
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
                for legs in ((0, 1, 2) if args.include_concrete else (1, 2)):
                    variants.append(dict(name=f'{geom}_{tag}_legs{legs}', b_m=b, h0_m=h, legs=legs))
        jobs.append(dict(group=name, engine=engine, reference=group['cases'], variants=variants, strain_audit=args.strain_audit))
    request = args.out/'arbat_shear_jobs.json'
    request.write_text(json.dumps(jobs, ensure_ascii=False, indent=2)+'\n')
    proc = subprocess.run([args.dotnet, str(args.probe.resolve()), str(args.runner.resolve()), str(request.resolve())],
                          capture_output=True, text=True, encoding='utf8', check=True)
    rows = json.loads(proc.stdout)
    expected = 16 * 4 * (3 if args.include_concrete else 2) * (2 if args.strain_audit else 1)
    if len(rows) != expected:
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
        flat[-1]['transverse_model'] = 'concrete_only' if row['legs'] == 0 else 'straight_bar_idealization_not_helix'
        if args.strain_audit:
            flat[-1].update({k:row[k] for k in ('metadata_variant','native_eb0','native_ebt0','native_ab',
                'native_as','native_es','native_rb','native_rbt','native_phi')})
    fields = list(dict.fromkeys(k for row in flat for k in row))
    with (args.out/'arbat_shear_hypotheses.csv').open('w', encoding='utf8', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=fields, lineterminator='\n')
        writer.writeheader()
        writer.writerows(flat)
    if args.include_concrete:
        # Read only native ratios: this summary does not calculate a resistance.
        boundary_proc = subprocess.run([args.dotnet, str(args.probe.resolve()), '--phi-boundaries'],
                                       capture_output=True, text=True, check=True)
        points = json.loads(boundary_proc.stdout)
        if len(points) != 13 or not all(p['agrees'] for p in points):
            raise ValueError('The probe DLL does not contain the reviewed phiN correction')
        summary = []
        selected = [r for r in rows if r['metadata_variant'] == 'adapter_current'
                    and '_a_center_' in r['variant']]
        for case_id in sorted({r['id'] for r in selected}):
            item = {'id': case_id, 'scope': 'local_Q_diagnostic_not_design_verdict', 'models': {}}
            for geometry in ('arbat_square', 'vniigs'):
                variants = [r for r in selected if r['id'] == case_id
                            and r['variant'].startswith(geometry+'_a_center_')]
                if {r['legs'] for r in variants} != {0, 1, 2} or len(variants) != 3:
                    raise ValueError('Incomplete concrete/bar comparison')
                item['models'][geometry] = {}
                for r in sorted(variants, key=lambda r:r['legs']):
                    ratios = (r['native_ratio'], r['native_strip_ratio'])
                    valid = all(type(v) in (int,float) and math.isfinite(v) and v >= 0 for v in ratios)
                    item['models'][geometry][str(r['legs'])] = {
                        'inclined_utilization': ratios[0], 'strip_utilization': ratios[1],
                        'local_q_below_one': all(v <= 1 for v in ratios) if valid else None}
                    if r['legs'] == 0 and r['variables'].get('Qsw') != 0:
                        raise ValueError('Native concrete control unexpectedly includes transverse resistance')
            item['q_kn'] = variants[0]['q_kn']
            if item['q_kn'] == 0:
                item['classification'] = 'zero_Q_not_evidence'
            else:
                concrete = [m['0']['local_q_below_one'] for m in item['models'].values()]
                item['classification'] = ('native_result_invalid' if None in concrete else
                    'concrete_below_one_in_both_models' if all(concrete) else
                    'concrete_depends_on_geometry' if any(concrete) else
                    'concrete_exceeds_one_in_both_models')
            summary.append(item)
        lock = json.loads((ROOT/'config/opencs.lock.json').read_text())
        manifest = {'base_commit':lock['commit'], 'patches':lock['patches'],
                    'cscore_dll_sha256':hashlib.sha256((args.runner.parent/'CScore.dll').read_bytes()).hexdigest(),
                    'runner_dll_sha256':hashlib.sha256(args.runner.read_bytes()).hexdigest(),
                    'probe_dll_sha256':hashlib.sha256(args.probe.read_bytes()).hexdigest(),
                    'native_evaluations':len(rows), 'phi_points':points,
                    'scope':'research_only_no_independent_helix_or_geometry_validation', 'cases':summary}
        (args.out/'shear_dependency_summary.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n')
    print(f'{len(rows)} native diagnostic evaluations; no design approval or spiral validation')


if __name__ == '__main__':
    main()
