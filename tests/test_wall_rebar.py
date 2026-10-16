import copy
import json
import math
from pathlib import Path
import unittest

import wall_rebar as w


def example():
    base = json.loads(Path('examples/verification/P800_sls_preselection_example.json').read_text())
    return {'schema_version': 1,
            'wall': {'panel_width_mm': 3000, 'thickness_mm': 800,
                     'cage_count': 3, 'cage_width_mm': 900, 'end_cover_mm': 50,
                     'cover_mm': 60, 'ties_diameter_mm': 10,
                     'min_clear_spacing_mm': 25, 'min_cage_gap_mm': 50,
                     'mesh_resolution': 41},
            'search': {face: {'bar_counts': [5], 'bar_diameters_mm': [25]}
                       for face in ('face_negative', 'face_positive')},
            'materials': base['materials'],
            'force_basis': {'uls': 'design', 'sls': 'normative'},
            'eccentricity': {'mode': 'already_in_loads'},
            'loads': [{'id': 'U1', 'n_kn_per_m': -100, 'mx_knm_per_m': 120}],
            'sls_probe': {**base['sls_probe'], 'loads': [
                {'id': 'S1', 'n_kn_per_m': -80,
                 'mx_total_knm_per_m': 100, 'mx_long_knm_per_m': 60}]}}


class WallTests(unittest.TestCase):
    def candidates(self, data=None):
        d = data or example()
        w.validate_input(d)
        return w.make_candidates(d)

    def test_density_example_and_real_spacing(self):
        good, bad = self.candidates()
        self.assertEqual(bad, [])
        c = good[0]
        f = c['face_negative']
        self.assertEqual(f['equivalent_spacing_mm'], 200)
        self.assertAlmostEqual(f['area_mm2_per_m'], 2454.369260617026)
        self.assertEqual(f['count_per_panel'], 15)
        self.assertEqual(len(c['actual_bars']), 30)
        self.assertAlmostEqual(f['actual_spacing_mm'], 213.75)
        self.assertEqual(c['cage_gap_mm'], 100)

    def test_fractional_density_never_rounds_and_faces_independent(self):
        d = example()
        d['wall']['panel_width_mm'] = 3100
        d['search']['face_positive'] = {'bar_counts': [4], 'bar_diameters_mm': [20]}
        c = self.candidates(d)[0][0]
        self.assertAlmostEqual(c['face_negative']['equivalent_spacing_mm'], 3100/15)
        self.assertAlmostEqual(c['face_negative']['area_mm2_per_m'], 15*math.pi*25**2/4/3.1)
        self.assertEqual(c['face_positive']['count_per_panel'], 12)
        req = w.build_engine_request(d, c)
        bars = req['research_rectangle']['bars']
        self.assertAlmostEqual(sum(math.pi*b['diameter_mm']**2/4 for b in bars), c['steel_area_mm2'])
        self.assertEqual({b['diameter_mm'] for b in bars}, {20,25})

    def test_wire_request_matches_strict_csharp_dtos(self):
        import re
        source=Path('src/CScorePileRunner/Program.cs').read_text(encoding='utf-8')
        def check(dto,obj):
            body=source.split('internal sealed class '+dto+'\n',1)[1].split('internal sealed class ',1)[0]
            allowed=set(re.findall(r'JsonPropertyName\("([^"\n]+)"\)',body))
            self.assertTrue(allowed,dto)
            self.assertEqual(set(obj)-allowed,set(),dto)
        d=example(); c=self.candidates(d)[0][0]
        request=w.build_engine_request(d,c)
        check('BatchInput',request)
        for key,dto in [('pile','Pile'),('materials','Materials'),('research_rectangle','ResearchRectangle'),('sls_probe','SlsProbe')]:
            check(dto,request[key])
        for obj in request['candidates']: check('Candidate',obj)
        for obj in request['loads']: check('LoadCase',obj)
        for obj in request['research_rectangle']['bars']: check('ResearchBar',obj)
        for obj in request['sls_probe']['loads']: check('SlsLoad',obj)
        for key in ('short_materials','long_materials'): check('Materials',request['sls_probe'][key])
        # Serialization must not strip the separate engineering report data.
        self.assertEqual(len(c['actual_bars']),30)
        self.assertEqual(c['face_negative']['equivalent_spacing_mm'],200)

    def test_all_forces_scaled_once_without_mutation(self):
        d = example(); before = copy.deepcopy(d)
        req = w.build_engine_request(d, self.candidates(d)[0][0])
        self.assertEqual(req['loads'], [{'id':'U1','n_kn':-300,'mx_knm':360,'my_knm':0}])
        self.assertEqual(req['sls_probe']['loads'][0],
                         {'id':'S1','n_kn':-240,'mx_total_knm':300,'mx_long_knm':180,
                          'my_total_knm':0,'my_long_knm':0})
        self.assertEqual(req['sls_execution_policy'], 'uls_pass_only')
        self.assertEqual(d, before)

    def test_invalid_dimensions_counts_force_basis_and_unknown_components(self):
        for key,value in [('cage_count',0),('cage_count',4),('cage_count',True),
                          ('panel_width_mm',float('nan')),('cage_width_mm',1100),
                          ('thickness_mm',100),('cover_mm',-1)]:
            d=example(); d['wall'][key]=value
            with self.subTest(key=key,value=value), self.assertRaises(w.InputError):
                w.validate_input(d)
        for key,value in [('my_knm_per_m',10),('n_kn',10),('shear_kn_per_m',20)]:
            d=example(); d['loads'][0][key]=value
            with self.subTest(key=key), self.assertRaises(w.InputError): w.validate_input(d)
        d=example(); d['force_basis']['sls']='design'
        with self.assertRaises(w.InputError): w.validate_input(d)

    def test_crowded_bars_are_rejected_not_sent_to_solver(self):
        d=example(); d['search']['face_negative']['bar_counts']=[40]
        good,bad=self.candidates(d)
        self.assertEqual(good,[])
        self.assertIn('просвет',bad[0]['reason'])

    def test_small_clearance_between_adjacent_cages_is_rejected(self):
        d=example()
        d['wall'].update(panel_width_mm=2800,min_cage_gap_mm=0)
        good,bad=self.candidates(d)
        self.assertEqual(good,[])
        self.assertIn('между каркасами',bad[0]['reason'])

    def test_rejected_or_unknown_uls_never_selects(self):
        d=example(); c=self.candidates(d)[0][0]
        for converged, util, gate in [(True,2,'skipped_uls_fail'),(False,None,'skipped_uls_indeterminate')]:
            def engine(req):
                return {'schema_version':1,'cases':[{'id':c['id'], 'loads':[
                    {'id':'U1','converged':converged,'utilization':util,
                     'factor':0.5 if converged else None,'error':None}],
                     'sls_gate':gate,'sls_probe':None}]}
            result=w.calculate(d,engine)
            self.assertIsNone(result['selected_uls_candidate'])
            self.assertIsNone(result['selected_preliminary_uls_sls_research_candidate'])
            self.assertIsNone(result['cases'][0]['sls_probe'])

    def test_missing_results_fail_closed(self):
        with self.assertRaises(w.InputError):
            w.calculate(example(),lambda req:{'schema_version':1,'cases':[]})

    def test_uls_and_sls_selection_and_governing_ids(self):
        d=example()
        d['search']['face_negative']['bar_counts']=[6,5]
        def engine(req):
            return {'schema_version':1,'cases':[{'id':req['candidates'][0]['id'],
                'loads':[{'id':'U1','converged':True,'factor':2,'utilization':.5,'error':None}],
                'sls_gate':'calculated_uls_pass','sls_probe':[{
                    'id':'S1','status':'research_calculated','error':None,
                    'verified_for_design':False,'preliminary_is_final_approval':False,
                    'preliminary_method':'arbat_conservative',
                    'preliminary_decision':'preliminary_below_limit',
                    'preliminary_utilization_long':.6,'utilization_long':.5,
                    'preliminary_utilization_short':.7,'utilization_short':.6,
                    'preliminary_governing_utilization':.7}]}]}
        result=w.calculate(d,engine)
        chosen='3c_neg5d25_pos5d25'
        self.assertEqual(result['selected_uls_candidate'],chosen)
        self.assertEqual(result['selected_preliminary_uls_sls_research_candidate'],chosen)
        self.assertEqual(len(result['cases'][0]['actual_bars']),30)
        self.assertEqual(result['cases'][0]['face_negative']['equivalent_spacing_mm'],200)
        self.assertEqual(result['cases'][0]['governing_uls_id'],'U1')
        self.assertEqual(result['cases'][0]['governing_sls_id'],'S1')
        self.assertEqual(result['design_status'],'NOT_VERIFIED')

    def test_empty_geometry_does_not_call_engine(self):
        d=example(); d['search']['face_negative']['bar_counts']=[40]
        def forbidden(req): self.fail('Invalid geometry sent to engine')
        result=w.calculate(d,forbidden)
        self.assertEqual(result['cases'],[])
        self.assertEqual(len(result['rejected_geometry']),1)
        self.assertIsNone(result['selected_uls_candidate'])

    def test_two_cages_and_single_cage_centering(self):
        for count in (1,2,3):
            d=example(); d['wall']['cage_count']=count
            good,bad=self.candidates(d)
            self.assertEqual(bad,[])
            xs=[b['x_mm'] for b in good[0]['actual_bars']]
            self.assertAlmostEqual(min(xs),-max(xs))
            self.assertEqual(len(xs),count*10)

    def test_research_eccentricity_disables_selection(self):
        d=example(); del d['sls_probe']; d['eccentricity']['mode']='research_none'
        def engine(req):
            return {'schema_version':1,'cases':[{'id':req['candidates'][0]['id'],
                    'loads':[{'id':'U1','converged':True,'factor':2,'utilization':.5,'error':None}]}]}
        result=w.calculate(d,engine)
        self.assertEqual(result['cases'][0]['status'],'pass_uls')
        self.assertIsNone(result['selected_uls_candidate'])

if __name__ == '__main__': unittest.main()
