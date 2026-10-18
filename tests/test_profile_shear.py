"""Contract gates for an exploratory whole-phase CScore SampledProfile run."""
import copy
import unittest

from workbench import model
from workbench.profile_shear import build_profiles, validate_profile_results


def project():
    p=model.default_project()
    p['materials_confirmed']=True
    p['eccentricity_confirmed']=True
    p['transverse']['mode']='screen'
    p['profile_options']={'orientation':'same','axes_confirmed':True}
    p['rows']=[]
    for s in range(5):
        for case, m0 in (('A',20),('B',80)):
            p['rows'].append(dict(z=-float(s),case=case,basis='ULS',
                                 n=-100,m=m0+20*s,q=20,m_long=None))
    return p


def mocked_profile(req, case, util=.6):
    return dict(id=case,status='research_profile_below_one' if util<=1 else 'research_profile_exceeds_one',
        utilization=util,verified_for_design=False,support_model='none',
        critical_shear_s_m=0.5,critical_shear_c_m=1.0,
        critical_shear_direction=1,critical_shear_eta=util,
        reinforcement_model='cscore_native_uniform_pitch',
        uniform_pitch_mm=req['pitch_bands'][0]['spacing_mm'])


class PhaseProfileContracts(unittest.TestCase):
    def test_two_phases_have_separate_joint_tuples_and_width_scales_once(self):
        p=project();orig=copy.deepcopy(p)
        rows=model.normalized_rows(p)
        result=build_profiles(rows,3,'same',True)
        self.assertEqual([x['id'] for x in result['profiles']],['A','B'])
        a=result['profiles'][0]['samples']
        self.assertEqual(a[0],dict(s_m=0,n_kn=-300,m_knm=60,q_kn=60))
        self.assertEqual(a[-1],dict(s_m=4,n_kn=-300,m_knm=300,q_kn=60))
        self.assertEqual(p,orig)
        self.assertEqual(result['support_model'],'none')

    def test_sign_never_guessed_or_changed(self):
        p=project();rows=model.normalized_rows(p)
        with self.assertRaisesRegex(ValueError,'Подтвердите'):
            build_profiles(rows,3,'same',False)
        with self.assertRaisesRegex(ValueError,'направлением'):
            build_profiles(rows,3,'opposite',True)
        for row in rows:row['q'] *= -1
        data=build_profiles(rows,3,'opposite',True)
        self.assertEqual(data['profiles'][0]['samples'][0]['q_kn'],60)
        self.assertEqual(rows[0]['q'],-20)

    def test_mismatch_and_duplicate_stations_rejected(self):
        p=project();rows=model.normalized_rows(p)
        rows[1]['m']+=500
        with self.assertRaisesRegex(ValueError,'не согласованы'):
            build_profiles(rows,3,'same',True)
        rows=model.normalized_rows(p)
        rows.append(copy.deepcopy(rows[0]))
        with self.assertRaisesRegex(ValueError,'не согласованы'):
            build_profiles(rows,3,'same',True)

    def test_missing_or_false_approval_rejected(self):
        p=project();inp=build_profiles(model.normalized_rows(p),3,'same',True)
        cases=[dict(id=case,status='research_profile_below_one',utilization=.5,
                    support_model='none',verified_for_design=False)
               for case in ('A','B')]
        self.assertEqual(len(validate_profile_results(inp,cases)),2)
        with self.assertRaisesRegex(ValueError,'все сочетания'):
            validate_profile_results(inp,cases[:1])
        cases[0]['verified_for_design']=True
        with self.assertRaisesRegex(ValueError,'нормативный'):
            validate_profile_results(inp,cases)
        cases[0]['verified_for_design']=False
        cases[0]['status']='research_profile_exceeds_one'
        with self.assertRaisesRegex(ValueError,'не соответствует'):
            validate_profile_results(inp,cases)

    def test_native_zero_capacity_is_failure_without_fabricated_ratio(self):
        req={'profiles':[{'id':'A'}], 'pitch_bands':[{'spacing_mm':200}]}
        row=dict(id='A',status='research_profile_exceeds_one',utilization=None,
                 reason='native_zero_capacity',governing_formula='8.55',applied=20,allowable=0,
                 support_model='none',verified_for_design=False)
        self.assertEqual(validate_profile_results(req,[row]),[row])
        for key,value in [('allowable',1),('applied',0),('status','research_profile_below_one')]:
            with self.assertRaises(ValueError):
                validate_profile_results(req,[{**row,key:value}])

    def test_background_run_is_independent_of_NM_design_verdict(self):
        p=project()
        seen=[]
        def engine(request):
            seen.append(request)
            result=[]
            for phase in request['shear_profile']['profiles']:
                result.append(mocked_profile(request['shear_profile'],phase['id']))
            return {'schema_version':1,'cases':[{
                'id':request['candidates'][0]['id'],
                'loads':[dict(id=l['id'],converged=True,factor=2,utilization=.5,error=None)
                         for l in request['loads']],
                'shear_screening':[dict(id=l['id'],status='indeterminate',
                     utilization=None,verified_for_design=False) for l in request['loads']],
                'shear_profiles':result
            }]}
        result=model.screen_background_profile(p,engine=engine)
        self.assertEqual(result['verified_for_design'],False)
        self.assertEqual(result['design_status'],'NOT_VERIFIED')
        self.assertEqual(result['reinforcement'],'background_only')
        self.assertEqual(result['proposed_stirrup_layout']['status'],'UNVERIFIED_PROPOSAL')
        self.assertFalse(result['proposed_stirrup_layout']['verified_for_design'])
        self.assertTrue(all(x['status']=='proposed_layout_count_only'
                            for x in result['critical_projection_links']))
        self.assertTrue(all(x['actual_layout_confirmed'] is False
                            for x in result['critical_projection_links']))
        self.assertEqual(len(result['profile_results']),2)
        self.assertEqual(len(seen),1)
        self.assertEqual(seen[0]['shear_profile']['profiles'][0]['samples'][1]['q_kn'],60)
        self.assertEqual(seen[0]['shear_screening']['cage_count'],3)

    def test_old_custom_transition_result_is_rejected(self):
        req=build_profiles(model.normalized_rows(project()),3,'same',True)
        req['pitch_bands']=[dict(start_s_m=0,end_s_m=4,spacing_mm=200)]
        cases=[mocked_profile(req,c) for c in ('A','B')]
        validate_profile_results(req,cases)
        for field,value in (('reinforcement_model','constant_background_longitudinal_projection_neighborhood_pitch'),
                            ('uniform_pitch_mm',300),('pitch_windows',[{}])):
            bad=copy.deepcopy(cases);bad[0][field]=value
            with self.assertRaisesRegex(ValueError,'постоянного шага'):
                validate_profile_results(req,bad)
        bad=copy.deepcopy(cases);bad[0]['critical_shear_direction']=0
        with self.assertRaisesRegex(ValueError,'геометрии критического'):
            validate_profile_results(req,bad)

    def test_json_contract_uses_declared_csharp_fields(self):
        from pathlib import Path
        import re
        cs=Path('src/CScorePileRunner/Program.cs').read_text(encoding='utf-8')
        def fields(dto):
            body=cs.split('internal sealed class '+dto+chr(10),1)[1]
            body=body.split('internal sealed class ',1)[0]
            return {line.split('JsonPropertyName("',1)[1].split('"',1)[0]
                    for line in body.splitlines() if 'JsonPropertyName("' in line}
        p=project()
        normalized=model.normalized_rows(p)
        profile=build_profiles(normalized,3,'same',True)
        self.assertEqual(set(profile)|{'pitch_bands'}, fields('ShearProfileBatch'))
        self.assertEqual(fields('ShearPitchBand'),
                         {'start_s_m','end_s_m','spacing_mm'})
        self.assertEqual(set(profile['profiles'][0]),fields('ShearPhase'))
        self.assertEqual(set(profile['profiles'][0]['samples'][0]),fields('ShearSample'))
        self.assertIn('new SampledProfile(',cs)
        self.assertIn('supportAtStart: false, supportAtEnd: false',cs)

    def test_variable_pitch_is_rejected_before_engine(self):
        p=project();p['transverse_zones']=[dict(top=0,bottom=-2,spacing_mm=300)]
        with self.assertRaisesRegex(ValueError,'CScore.*постоянный'):
            model.screen_background_profile(p,engine=lambda _:self.fail('engine called'))

    def test_explicit_ties_change_only_layout_diagnostics_not_Q_strength(self):
        p=project()
        positions=list(range(50,4000,200))
        p['transverse_layout']={'mode':'specified','positions_mm':positions,
                                 'geometry_confirmed':True}
        seen=[]
        def engine(req):
            seen.append(req)
            return {'schema_version':1,'cases':[{
                'id':req['candidates'][0]['id'],
                'loads':[dict(id=v['id'],converged=True,factor=2,
                    utilization=.5,error=None) for v in req['loads']],
                'shear_screening':[dict(id=v['id'],status='indeterminate',
                    utilization=None,verified_for_design=False) for v in req['loads']],
                'shear_profiles':[mocked_profile(req['shear_profile'],x['id'])
                    for x in req['shear_profile']['profiles']]
            }]}
        result=model.screen_background_profile(p,engine=engine)
        layout=result['proposed_stirrup_layout']
        self.assertEqual(layout['status'],'SPECIFIED_GEOMETRY_ONLY')
        self.assertEqual(layout['link_count_per_cage'],len(positions))
        self.assertTrue(layout['actual_layout_confirmed'])
        self.assertFalse(layout['verified_for_design'])
        self.assertTrue(all(x['status']=='specified_layout_count_only'
                            for x in result['critical_projection_links']))
        self.assertTrue(all(x['actual_layout_confirmed']
                            for x in result['critical_projection_links']))
        self.assertFalse(result['verified_for_design'])
        self.assertNotIn('transverse_layout',seen[0]['shear_profile'])
        # Engineer-confirmed positions are a separate geometry observation,
        # NOT a secret change to upstream CScore's nominal qsw(s).
        p['transverse_layout']['geometry_confirmed']=False
        other=model.screen_background_profile(p,engine=engine)
        self.assertFalse(other['proposed_stirrup_layout']['actual_layout_confirmed'])
        self.assertEqual([x['utilization'] for x in result['profile_results']],
                         [x['utilization'] for x in other['profile_results']])

    def test_explicit_tie_layout_rejects_overspacing_before_engine(self):
        p=project()
        p['transverse_layout']={'mode':'specified','positions_mm':[50,250,1000],
                                 'geometry_confirmed':True}
        with self.assertRaises(ValueError):
            model.screen_background_profile(p,engine=lambda _:self.fail('engine called'))

    def test_background_run_never_uses_unconfirmed_axes(self):
        p=project();p['profile_options']['axes_confirmed']=False
        with self.assertRaisesRegex(ValueError,'Подтвердите'):
            model.screen_background_profile(p,engine=lambda _:self.fail('engine called'))


if __name__=='__main__':unittest.main()
