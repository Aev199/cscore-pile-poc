#!/usr/bin/env python3
"""Real CScore integration for UI project → selection → edited reinforcement."""
import argparse
import copy
import json
import math
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
        audit=result['force_profile_audit']
        assert audit['scope']=='diagnostic_only_no_shear_approval'
        assert audit['usable_for_design'] is False
        assert len(audit['profiles'])==3

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
        if kind == 'wall':
            # True SP63 shear computations on a real CScore rectangular section.
            # They are CONSTANT-PROFILE screening only, never normative approval.
            transverse = copy.deepcopy(p)
            transverse['transverse'] = dict(mode='screen', diameter_mm=10,
                spacing_mm=200, effective_legs_per_cage=2, rsw_kpa=170000,
                constructive_confirmed=True)
            q_result = model.design(transverse, runner=args.runner, recheck=True, progress=print)
            (args.output_dir/'wall_q_screen.json').write_text(
                json.dumps(q_result, indent=2, ensure_ascii=False), encoding='utf-8')
            assert q_result['shear_scope']=='local_constant_profile_research_screen_only'
            assert q_result['shear_checked_for_design'] is False
            assert all(s['shear_screening'] for s in q_result['stations'])
            assert all(len(s['shear_screening'])==2 for s in q_result['stations'])
            all_q = [v for s in q_result['stations'] for v in s['shear_screening']]
            assert any(v['status']!='indeterminate' for v in all_q), all_q
            assert all(v['verified_for_design'] is False for v in all_q)
            # Verify actual link contribution uses ALL physical cages exactly once.
            expected_qsw = 3 * 2 * math.pi * (0.01 ** 2) / 4 * 170000 / 0.2
            resolved = [v for v in all_q if v['status'] != 'indeterminate']
            assert resolved
            assert all(math.isclose(v['active_qsw_kn_per_m'], expected_qsw, rel_tol=1e-6)
                       for v in resolved), resolved
            assert all(v.get('support_model')=='constant_profile_no_support'
                       for v in all_q if v['status']!='indeterminate')
            print('wall: real CScore Q screened; every verdict explicitly research-only')
            # Independent full-phase SampledProfile check for CONSTANT BACKGROUND.
            # Use a deliberately equilibrium-compatible M'=Q profile, not the
            # unrelated illustrative stage data of the workbench demo.
            profile_project=copy.deepcopy(transverse)
            profile_project['zones']=[]
            profile_project['profile_options']={'orientation':'same','axes_confirmed':True}
            profile_project['rows']=[
                {'z':-float(i),'case':'Q ULS','basis':'ULS','n':-100.,
                 'm':50.+20.*i,'q':20.,'m_long':None}
                for i in range(5)]
            profile_result=model.screen_background_profile(
                profile_project,runner=args.runner)
            (args.output_dir/'wall_q_phase_profile.json').write_text(
                json.dumps(profile_result,indent=2,ensure_ascii=False),encoding='utf-8')
            assert profile_result['verified_for_design'] is False
            assert profile_result['support_model']=='none'
            assert profile_result['reinforcement']=='background_only'
            assert len(profile_result['profile_results'])==1
            phase=profile_result['profile_results'][0]
            assert phase['status'] in ('research_profile_below_one',
                                       'research_profile_exceeds_one'),phase
            assert math.isfinite(phase['utilization'])
            assert phase['verified_for_design'] is False
            assert phase['sampled_stations']>2
            print('wall: full-phase SampledProfile CScore Q research diagnostic succeeded')


if __name__=='__main__':main()
