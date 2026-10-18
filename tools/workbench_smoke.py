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
from tools.sp63_q_handcheck import handcheck


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--runner',required=True,type=Path)
    parser.add_argument('--output-dir',type=Path,default=Path('results/workbench'))
    args=parser.parse_args();args.output_dir.mkdir(parents=True,exist_ok=True)
    # Native eccentricity preparation must preserve load provenance and signs.
    from pile_rebar import prepare_design_loads
    original={'pile':{'diameter_mm':800},
              'eccentricity':{'mode':'sp63_stat_determinate','static_scheme':'statically_determinate',
                              'effective_length_mm':3000,'zero_moment_axis':'my_negative'},
              'loads':[{'id':'compression','n_kn':-1000,'mx_knm':100,'my_knm':0},
                       {'id':'tension','n_kn':100,'mx_knm':20,'my_knm':0},
                       {'id':'centric','n_kn':-1000,'mx_knm':0,'my_knm':0}]}
    prepared,audit,settings=prepare_design_loads(original,args.runner)
    assert math.isclose(settings['ea_mm'],26.666666666666668,abs_tol=1e-8)
    assert math.isclose(prepared[0]['mx_knm'],126.66666666666667,abs_tol=1e-8)
    assert prepared[1]==original['loads'][1]
    assert math.isclose(prepared[2]['my_knm'],-26.666666666666668,abs_tol=1e-8)
    assert audit[0]['original']==original['loads'][0]
    print('eccentricity: native CScore preparation verified; compression, tension, explicit zero-moment axis')
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
        for station in result['stations']:
            for sls in station['adopted'].get('sls_probe') or []:
                if sls['status']=='research_calculated':
                    assert sls['preliminary_method']=='native_refined'
                    assert sls['preliminary_width_long_mm']==sls['width_long_mm']
                    assert sls['preliminary_width_short_mm']==sls['width_short_mm']
                    assert sls['preliminary_utilization_long']==sls['utilization_long']
                    assert sls['preliminary_utilization_short']==sls['utilization_short']
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
            # Independently hand-check the three reference formulas against the
            # actual pinned CScore result and geometric/material quantities.
            # These cases have N<0, so phi_n applies to the strip as well.
            for v in resolved:
                assert v['critical_c_m'] is not None, v
                hand=handcheck(b=v['b_m'],h0=v['h0_m'],rb=v['rb_kpa'],
                    rbt=v['rbt_kpa'],phi_n=v['phi_n'],
                    qsw=v['active_qsw_kn_per_m'],sw=.2,
                    c=v['critical_c_m'],q=v['q_kn'],
                    phi_applies_to_strip=True)
                for key, expected in (('qb_kn',hand['qb_kn']),
                                      ('qsw_kn',hand['qsw_kn'])):
                    assert math.isclose(v[key],expected,rel_tol=1e-6,abs_tol=1e-6), (key,v,hand)
                expected_eta=max(hand['eta_inclined'],hand['eta_strip'])
                assert math.isclose(v['utilization'],expected_eta,rel_tol=1e-6,
                                    abs_tol=1e-6),(v,hand)
            print('wall: CScore 8.55-8.59 Q independently hand-checked in kN/m/kPa')
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
                for i in range(13)]
            profile_project['transverse_zones']=[
                {'top':0.,'bottom':-12.,'spacing_mm':200.}]
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
            assert phase['critical_shear_direction'] in (-1,1)
            assert math.isfinite(phase['critical_shear_c_m'])
            assert phase['critical_shear_c_m'] > 0
            assert math.isfinite(phase['critical_shear_s_m'])
            proposed=profile_result['proposed_stirrup_layout']
            assert proposed['status']=='UNVERIFIED_PROPOSAL'
            assert proposed['link_count_per_cage'] > 0
            assert proposed['verified_for_design'] is False
            projection=profile_result['critical_projection_links'][0]
            assert projection['verified_for_design'] is False
            assert projection['actual_layout_confirmed'] is False
            assert projection['status'] in ('proposed_layout_count_only',
                                             'outside_sampled_profile'),projection
            if projection['status']=='proposed_layout_count_only':
                assert isinstance(projection['ties_crossing_per_cage'],int)
                assert projection['ties_crossing_per_cage'] >= 0
            assert phase['reinforcement_model']=='cscore_native_uniform_pitch'
            assert phase['uniform_pitch_mm']==200
            assert not phase.get('pitch_windows')
            variable=copy.deepcopy(profile_project)
            variable['transverse_zones']=[{'top':0.,'bottom':-4.,'spacing_mm':150.}]
            try:
                model.screen_background_profile(variable,runner=args.runner)
            except ValueError as exc:
                assert 'CScore' in str(exc) and 'постоянный' in str(exc)
            else:
                raise AssertionError('Variable pitch must not use an adapter strength model')
            # Check engineer-specified coordinates on exactly the same CScore
            # phase: real tie geometry must NOT silently modify its Q strength.
            confirmed_profile=copy.deepcopy(profile_project)
            actual_mm=list(range(50,12000,200))
            confirmed_profile['transverse_layout']={
                'mode':'specified','positions_mm':actual_mm,
                'geometry_confirmed':True}
            measured=model.screen_background_profile(confirmed_profile,runner=args.runner)
            assert measured['proposed_stirrup_layout']['status']=='SPECIFIED_GEOMETRY_ONLY'
            assert measured['proposed_stirrup_layout']['actual_layout_confirmed'] is True
            assert measured['proposed_stirrup_layout']['verified_for_design'] is False
            assert measured['proposed_stirrup_layout']['link_count_per_cage']==len(actual_mm)
            assert measured['critical_projection_links'][0]['actual_layout_confirmed']
            assert measured['critical_projection_links'][0]['status'] in (
                'specified_layout_count_only','outside_sampled_profile')
            assert math.isclose(measured['profile_results'][0]['utilization'],
                                phase['utilization'],rel_tol=1e-9)
            print('wall: explicit stirrup coordinate geometry checked; no hidden change to CScore Q strength')
            print('wall: native uniform-pitch CScore profile; variable-pitch calculation rejected')
            print('wall: full-phase SampledProfile CScore Q research diagnostic succeeded')


if __name__=='__main__':main()
