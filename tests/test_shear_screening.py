"""Contract tests: Q scales once, never becomes a false normative ULS acceptance."""
import copy
import math
import unittest

import pile_rebar as pile
import wall_rebar as wall
from workbench import model


def screening():
    return dict(mode='screen', diameter_mm=10, spacing_mm=200,
                effective_legs_per_cage=2, rsw_kpa=170000,
                constructive_confirmed=False)


class ShearScreeningContract(unittest.TestCase):
    def test_wall_q_scales_once_for_one_two_three_cages(self):
        for cages in (1, 2, 3):
            with self.subTest(cages=cages):
                p = model.default_project()
                p['wall']['cage_count'] = cages
                p['wall']['panel_width_mm'] = 3000
                p['transverse'] = screening()
                r = model.normalized_rows(p)
                inp = model.section_input(p, [x for x in r if x['z'] == 0])
                wall.validate_input(inp)
                good, _ = wall.make_candidates(inp)
                self.assertTrue(good)
                req = wall.build_engine_request(inp, good[0])
                self.assertIn('shear_screening', req)
                self.assertEqual(req['shear_screening']['cage_count'], cages)
                self.assertEqual(req['shear_screening']['effective_legs_per_cage'], 2)
                self.assertAlmostEqual(req['loads'][0]['q_kn'], 90)
                self.assertAlmostEqual(req['loads'][0]['n_kn'], -300)
                self.assertAlmostEqual(req['loads'][0]['mx_knm'], 84)
                self.assertNotIn('q_kn_per_m', req['loads'][0])

    def test_q_requires_every_uls_tuple_not_mixed_envelope(self):
        p = model.default_project()
        p['transverse'] = screening()
        row = model.normalized_rows(p)
        req = model.section_input(p, [x for x in row if x['z'] == 0])
        req['loads'][0].pop('q_kn_per_m')
        with self.assertRaisesRegex(ValueError, 'q_kn_per_m'):
            wall.validate_input(req)
        req = model.section_input(p, [x for x in row if x['z'] == 0])
        for v in req['loads']:
            self.assertEqual(v['q_kn_per_m'], 30)
        req['loads'][1]['q_kn_per_m'] = -70
        good, _ = wall.make_candidates(req)
        wire = wall.build_engine_request(req, good[0])
        self.assertEqual([v['q_kn'] for v in wire['loads']], [90, -210])

    def test_disabled_screen_backward_compatible(self):
        p = model.default_project()
        req = model.section_input(p, [r for r in model.normalized_rows(p) if r['z'] == 0])
        good, _ = wall.make_candidates(req)
        wire = wall.build_engine_request(req, good[0])
        self.assertNotIn('shear_screening', wire)
        self.assertIn('q_kn', wire['loads'][0])  # data trace preserved, not calculated
        self.assertEqual(p['transverse']['mode'], 'off')

    def test_round_pile_selection_stays_separate_from_equivalent_Q_checks(self):
        p = model.default_project()
        p['kind'] = 'pile'
        p['force_scale'] = 'per_member'
        p['transverse'] = screening()
        model.validate_project(p)
        self.assertNotIn('shear_screening',model.section_input(p,model.normalized_rows(p)))
        p['kind'] = 'wall'
        p['transverse']['effective_legs_per_cage'] = 0
        with self.assertRaises(ValueError):
            model.validate_project(p)

    def test_incomplete_or_false_approval_fails_closed(self):
        request = dict(
            schema_version=1,
            candidates=[dict(id='w',bar_count=12,bar_diameter_mm=25,
                             steel_area_mm2=1000,rotation_deg=0,clearance_mm=50)],
            loads=[dict(id='A',n_kn=-100,mx_knm=30,q_kn=500)],
            shear_screening={'enabled':True})
        raw = dict(schema_version=1,cases=[dict(id='w',loads=[
            dict(id='A',converged=True,factor=2.0,utilization=.5,error=None)])])
        with self.assertRaisesRegex(ValueError, 'Q'):
            pile.parse_runner_results(request, raw)
        item = raw['cases'][0]
        item['shear_screening'] = [dict(id='A',status='research_screen_pass',
                                        utilization=.8,verified_for_design=True)]
        with self.assertRaisesRegex(ValueError, 'нормативным допуском'):
            pile.parse_runner_results(request,raw)
        item['shear_screening'][0]['verified_for_design']=False
        rows=pile.parse_runner_results(request,raw)
        self.assertEqual(rows[0]['status'],'pass_uls')
        self.assertEqual(rows[0]['shear_status'],'research_screen_pass')
        self.assertFalse(rows[0]['shear_screening'][0]['verified_for_design'])
        item['shear_screening'][0]['status']='research_screen_fail'
        with self.assertRaisesRegex(ValueError, 'несогласованный'):
            pile.parse_runner_results(request,raw)
        item['shear_screening'][0] = dict(id='A',status='research_screen_fail',
            utilization=None,verified_for_design=False,reason='native_zero_capacity',
            governing_formula='8.55',applied=20,allowable=0)
        self.assertEqual(pile.parse_runner_results(request,raw)[0]['shear_status'],'research_screen_fail')
        item['shear_screening'][0]['allowable']=1
        with self.assertRaisesRegex(ValueError,'недостоверный'):
            pile.parse_runner_results(request,raw)
        item['shear_screening'][0] = dict(id='A',status='indeterminate',
                                          utilization=None,verified_for_design=False)
        self.assertEqual(pile.parse_runner_results(request,raw)[0]['shear_status'],
                         'indeterminate')

    def test_q_default_nonzero_does_not_make_scope_verified(self):
        p=model.default_project()
        self.assertTrue(any(abs(r['q'])>0 for r in p['rows']))
        self.assertEqual(p['transverse']['mode'],'off')
        self.assertFalse(p['transverse']['constructive_confirmed'])


if __name__=='__main__':
    unittest.main()
