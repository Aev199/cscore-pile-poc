"""Application delegates accidental eccentricity to the native CScore runner."""
import json
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
import unittest
import pile_rebar as app

class EccentricityTests(unittest.TestCase):
    def setUp(self):
        self.data=json.loads(Path('examples/demo_bns.json').read_text())
        self.data['loads']=[dict(id='A',n_kn=-1000,mx_knm=100,my_knm=0)]

    def test_auto_cannot_run_without_cscore(self):
        with self.assertRaisesRegex(app.InputError,'CScore runner'):
            app.prepare_design_loads(self.data)

    def test_no_double_count_when_already_in_loads(self):
        self.data['eccentricity']={'mode':'already_in_loads'}
        loads,audit,info=app.prepare_design_loads(self.data)
        self.assertEqual(loads,self.data['loads'])
        self.assertEqual(audit[0]['delta_mx_knm'],0)
        self.assertTrue(info['eccentricity_accounted_for'])

    def test_research_or_missing_confirmation_cannot_select(self):
        for setting in ({'mode':'research_none'},None):
            self.data['eccentricity']=setting
            self.assertFalse(app.prepare_design_loads(self.data)[2]['eccentricity_accounted_for'])

    def test_native_preparation_is_used_without_python_recalculation(self):
        original=self.data['loads'][0]
        effective={**original,'mx_knm':123.456}
        payload={'loads':[effective],'audit':[dict(id='A',original=original,effective=effective)],'ea_mm':23.456}
        def invoke(args,**kw):
            self.assertEqual(args[-1],'--prepare-loads')
            wire=json.loads(kw['input'])
            self.assertEqual(wire['loads'],self.data['loads'])
            self.assertNotIn('ea_mm',wire)
            return SimpleNamespace(returncode=0,stdout=json.dumps(payload),stderr='')
        with patch.object(app.subprocess,'run',side_effect=invoke):
            loads,audit,info=app.prepare_design_loads(self.data,Path('native.exe'))
        self.assertEqual(loads,[effective]);self.assertEqual(info['ea_mm'],23.456)
        self.assertEqual(self.data['loads'][0]['mx_knm'],100)

    def test_native_failure_does_not_fall_back_to_python(self):
        with patch.object(app.subprocess,'run',return_value=SimpleNamespace(returncode=2,stderr='no convergence')):
            with self.assertRaisesRegex(app.InputError,'CScore'):
                app.prepare_design_loads(self.data,Path('native.exe'))
