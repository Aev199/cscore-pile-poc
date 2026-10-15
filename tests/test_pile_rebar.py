import json
import math
import pathlib
import unittest
from unittest.mock import patch
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import pile_rebar as app


class PileRebarTests(unittest.TestCase):
    def setUp(self):
        self.data = json.loads((ROOT/'examples/demo_bns.json').read_text(encoding='utf-8'))

    def test_demo_schema_and_candidates(self):
        app.validate_input(self.data)
        ok, bad = app.make_candidates(self.data)
        self.assertGreater(len(ok), 0)
        self.assertGreater(len(ok) + len(bad), 10)
        self.assertTrue(all(x['clearance_mm'] >= self.data['pile']['min_clear_spacing_mm'] for x in ok))
        self.assertTrue(all(x['steel_area_mm2'] > 0 for x in ok))

    def request(self):
        return app.build_engine_request(self.data, app.make_candidates(self.data)[0][:2])

    def mock(self, req, factor=1.2, util=None):
        util = 1 / factor if util is None else util
        return {'schema_version':1,
                'cases':[{'id':c['id'], 'loads':[{'id':l['id'], 'converged':True,
                           'factor':factor,'utilization':util,'error':None} for l in req['loads']]}
                         for c in req['candidates']]}

    def test_no_convergence_never_passes(self):
        req = self.request()
        raw = self.mock(req)
        raw['cases'][0]['loads'][1] = {'id':req['loads'][1]['id'],'converged':False,
                                      'factor':None,'utilization':None,'error':'no_convergence'}
        result = app.parse_runner_results(req, raw)
        self.assertEqual('indeterminate', result[0]['status'])
        self.assertEqual('pass_uls', result[1]['status'])

    def test_error_flag_with_converged_is_indeterminate(self):
        req = self.request()
        raw = self.mock(req)
        raw['cases'][0]['loads'][0]['error'] = 'unexpected_solver_warning'
        result = app.parse_runner_results(req, raw)
        self.assertEqual('indeterminate', result[0]['status'])

    def test_failure_is_not_ignored(self):
        req = self.request()
        raw = self.mock(req)
        raw['cases'][0]['loads'][2]['factor'] = 0.7
        raw['cases'][0]['loads'][2]['utilization'] = 1/0.7
        result = app.parse_runner_results(req, raw)
        self.assertEqual('fail_uls', result[0]['status'])

    def test_missing_case_blocks_result(self):
        req = self.request()
        raw = self.mock(req)
        raw['cases'][0]['loads'].pop()
        with self.assertRaisesRegex(app.InputError,'неполный'):
            app.parse_runner_results(req,raw)

    def test_nonfinite_or_inconsistent_cscore_output_is_indeterminate(self):
        req = self.request()
        raw = self.mock(req)
        raw['cases'][0]['loads'][0]['utilization'] = math.nan
        raw['cases'][1]['loads'][0]['factor'] = 1.2
        raw['cases'][1]['loads'][0]['utilization'] = 0.1
        result = app.parse_runner_results(req,raw)
        self.assertEqual(['indeterminate','indeterminate'],[x['status'] for x in result])

    def test_pass_requires_all_loads(self):
        req = self.request()
        raw = self.mock(req)
        result = app.parse_runner_results(req, raw)
        self.assertTrue(all(row['status'] == 'pass_uls' for row in result))

    def test_too_much_rebar_is_rejected_by_geometry(self):
        self.data['search']['bar_counts'] = [100]
        self.data['search']['bar_diameters_mm'] = [40]
        ok, bad = app.make_candidates(self.data)
        self.assertFalse(ok)
        self.assertTrue(bad)

    def test_input_rejects_missing_force(self):
        self.data['loads'][0].pop('my_knm')
        with self.assertRaises(app.InputError):
            app.validate_input(self.data)

    def test_input_rejects_duplicate_force_id(self):
        self.data['loads'][1]['id'] = self.data['loads'][0]['id']
        with self.assertRaises(app.InputError):
            app.validate_input(self.data)

    def test_geometric_area_exact(self):
        self.data['search']={'bar_counts':[8], 'bar_diameters_mm':[25], 'rotation_deg':[0]}
        candidate = app.make_candidates(self.data)[0][0]
        self.assertAlmostEqual(candidate['steel_area_mm2'],8*math.pi*25**2/4,places=6)


if __name__ == '__main__':
    unittest.main()
