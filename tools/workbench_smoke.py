#!/usr/bin/env python3
"""Real CScore integration for UI project → selection → edited reinforcement."""
import argparse
import copy
import json
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from workbench import model


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--runner',required=True,type=Path)
    parser.add_argument('--output-dir',type=Path,default=Path('results/workbench'))
    args=parser.parse_args();args.output_dir.mkdir(parents=True,exist_ok=True)
    for kind in ('wall','pile'):
        p=model.default_project();p.update(kind=kind,materials_confirmed=True,eccentricity_confirmed=True,max_tier=2)
        p['force_scale']='per_metre' if kind=='wall' else 'per_member'
        p['rows']=[r for r in p['rows'] if r['z'] in (0,-2)]
        result=model.design(p,runner=args.runner,progress=print)
        (args.output_dir/f'{kind}.json').write_text(json.dumps(result,indent=2,ensure_ascii=False),encoding='utf-8')
        assert len(result['stations'])==2
        assert result['all_checked'],f'{kind}: preliminary checks not passed; inspect artifact'
        assert all(s['required_areas'] and s['adopted']['accepted'] for s in result['stations'])
        assert result['has_sls'] and result['design_status']=='NOT_VERIFIED'
        p['zones']=[{'top':0,'bottom':-2,'tiers':[2,2] if kind=='wall' else [2,1]}]
        recheck=model.design(p,runner=args.runner,recheck=True,progress=print)
        (args.output_dir/f'{kind}_edited.json').write_text(json.dumps(recheck,indent=2,ensure_ascii=False),encoding='utf-8')
        assert recheck['all_checked']
        assert recheck['stations'][0]['adopted']['areas'][0]>result['stations'][0]['adopted']['areas'][0]
        print(f'{kind}: UI selection and edited reinforcement checked by real CScore')

if __name__=='__main__':main()
