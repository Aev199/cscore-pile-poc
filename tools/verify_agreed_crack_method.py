#!/usr/bin/env python3
"""Numerical regression of the agreed CScore-based estimate and product report.

Historical values are research references, not certification of ARBAT equivalence.
"""
import argparse
import copy
import json
import math
from pathlib import Path
import subprocess
import sys

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from tools import cross_section_sls_matrix as matrix
from workbench.connection import validate_report


def run(runner):
    # T09/T10: partly sustained actions, SP63 8.2.6 split into sustained and total states.
    cases=[('R300x500', 'T01', 'long', 1.0316302052621296),
           ('R400x600', 'T02', 'long', 1.7392021983328885),
           ('R400x600', 'T07', 'short', 1.4314045937550512),
           ('C600', 'T09', 'short', 1.1458448068514773),
           ('C600', 'T10', 'long', 0.5923435130783482)]
    for prefix,case,duration,expected in cases:
        section=next(s for s in matrix.section_definitions('arbat_stated') if s['id'].startswith(prefix))
        request=matrix.make_request(section,'arbat_conservative')
        request['sls_probe']['loads']=[l for l in request['sls_probe']['loads'] if l['id'].startswith(case)]
        request['connection_context']={'second_order_mode':'section_only'}
        outputs={}
        for mode in matrix.METHODS:
            req=copy.deepcopy(request);req['sls_probe']['preliminary_method']=mode
            command=['dotnet',str(runner)] if runner.suffix.lower()=='.dll' else [str(runner)]
            p=subprocess.run(command,input=json.dumps(req),capture_output=True,text=True,encoding='utf-8',timeout=120)
            if p.returncode:raise RuntimeError(p.stderr)
            result=json.loads(p.stdout)['cases'][0]
            probe=result['sls_probe'][0]
            if mode=='native_refined' and 'n_long_kn' in req['sls_probe']['loads'][0]:
                # The author's solver takes one N; a separate sustained N fails closed.
                assert probe['status']=='indeterminate' and probe['error'].endswith('native_refined_requires_single_axial_force'),probe
                continue
            assert probe['status']=='research_calculated',probe
            report=validate_report(req,result['connection_report'])
            row=next(r for r in report['checks'] if r['check_id']=='crack_width_'+duration)
            assert math.isclose(row['utilization'],probe['preliminary_utilization_'+duration],abs_tol=1e-10)
            outputs[mode]=probe
            if mode=='arbat_conservative':
                assert row['engine_method']=='Application.ArbatConservativeWithCScore'
                assert row['status'] in ('preliminary_below_limit','preliminary_exceeds_limit','engineering_review')
                assert math.isclose(row['utilization'],expected,rel_tol=1e-6),row
                print(f'{prefix}/{case}: agreed {duration} K={row["utilization"]:.6f}; native K={row["details"]["native_utilization"]:.6f}')
            else:assert row['engine_method']=='CScore.CrackWidthSolver.Compute'
        conservative,native=outputs['arbat_conservative'],outputs.get('native_refined')
        if conservative['preliminary_basis'].startswith('sp63_8_2_6_'):
            # Separate sustained N is outside the native single-N contract.
            for key in ('long','short'):
                assert conservative['preliminary_utilization_'+key]+1e-12>=conservative['utilization_'+key]
            continue
        for key in ('width_long_mm','width_short_mm','utilization_long','utilization_short','mcrc_knm'):
            assert conservative[key]==native[key],key
    print('Agreed crack method: numeric references, product report and unchanged native option passed')


if __name__=='__main__':
    cli=argparse.ArgumentParser();cli.add_argument('--runner',type=Path,required=True)
    run(cli.parse_args().runner.resolve())
