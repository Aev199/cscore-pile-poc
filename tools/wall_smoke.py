#!/usr/bin/env python3
"""Real-engine wall integration checks. No simulated numerical results."""
import argparse
import copy
import json
import math
from pathlib import Path
import sys

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import wall_rebar as w


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--runner',required=True,type=Path)
    parser.add_argument('--output-dir',type=Path,default=Path('results/wall_smoke'))
    args=parser.parse_args()
    args.output_dir.mkdir(parents=True,exist_ok=True)
    base=w.read_json(Path(__file__).resolve().parents[1]/'examples/wall_cages.json')
    for face in w.FACES:
        base['search'][face]={'bar_counts':[5],'bar_diameters_mm':[25]}
    base['loads']=[{'id':'POS','n_kn_per_m':0,'mx_knm_per_m':120},
                   {'id':'NEG','n_kn_per_m':0,'mx_knm_per_m':-120}]
    base['sls_probe']['loads']=[{'id':'S1','n_kn_per_m':0,
                               'mx_total_knm_per_m':100,'mx_long_knm_per_m':100}]

    def calc(name,data):
        inp=args.output_dir/f'{name}_input.json'
        inp.write_text(json.dumps(data,indent=2),encoding='utf-8')
        return w.run(inp,args.runner,args.output_dir/f'{name}.json',args.output_dir/f'{name}.csv')

    full=calc('panel_3m',base)
    narrow=copy.deepcopy(base)
    narrow['wall'].update(panel_width_mm=1000,cage_count=1)
    strip=calc('strip_1m',narrow)
    for res in (full,strip):
        c=res['cases'][0]
        assert c['status']=='pass_uls',c
        assert c['sls_gate']=='calculated_uls_pass' and len(c['sls_probe'])==1,c
        assert c['design_status']=='NOT_VERIFIED'
        assert math.isclose(c['loads'][0]['utilization'],c['loads'][1]['utilization'],rel_tol=.005)
    u1,u3=strip['cases'][0]['max_utilization'],full['cases'][0]['max_utilization']
    assert math.isclose(u1,u3,rel_tol=.005),(u1,u3)

    asym=copy.deepcopy(base)
    asym['search']['face_positive']={'bar_counts':[3],'bar_diameters_mm':[20]}
    ar=calc('asymmetric',asym)['cases'][0]
    assert all(l['decision']!='indeterminate' for l in ar['loads']), ar
    assert not math.isclose(ar['loads'][0]['utilization'],ar['loads'][1]['utilization'],rel_tol=.01),ar

    overload=copy.deepcopy(base)
    overload['loads']=[{'id':'OVERLOAD','n_kn_per_m':0,'mx_knm_per_m':50000}]
    rejected=calc('overload',overload)
    c=rejected['cases'][0]
    assert c['status'] in ('fail_uls','indeterminate'),c
    assert c['sls_probe'] is None and c['sls_gate'].startswith('skipped_uls_'),c
    assert rejected['selected_preliminary_uls_sls_research_candidate'] is None
    print(f'Wall real-engine checks passed; ULS 1m={u1:.8f}, 3m={u3:.8f}. SLS remains NOT_VERIFIED.')

if __name__=='__main__': main()
