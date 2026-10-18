"""Regression gates: application must not invent engineering calculations."""
import copy
import json
from pathlib import Path
import unittest
import pile_rebar as pile
from workbench import model

class CScoreOnly(unittest.TestCase):
    def test_legacy_sls_method_rejected(self):
        p=json.loads(Path('examples/verification/P800_sls_preselection_example.json').read_text())
        p['sls_probe']['preliminary_method']='arbat_conservative'
        with self.assertRaisesRegex(ValueError,'native_refined'):
            pile.validate_sls_probe(p['sls_probe'])

    def test_no_python_accidental_eccentricity_solver(self):
        p=json.loads(Path('examples/demo_bns.json').read_text())
        p['eccentricity']={'mode':'sp63_stat_determinate','static_scheme':'statically_determinate',
                           'effective_length_mm':3000}
        with self.assertRaisesRegex(ValueError,'CScore'):
            pile.prepare_design_loads(p)

    def test_variable_pitch_does_not_call_engine(self):
        from test_profile_shear import project
        p=project()
        p['transverse_zones']=[{'top':0,'bottom':-2,'spacing_mm':300}]
        with self.assertRaisesRegex(ValueError,'CScore.*постоянн'):
            model.screen_background_profile(p,engine=lambda _:self.fail('engine called'))

    def test_native_runner_has_no_custom_strength_transforms(self):
        cs=Path('src/CScorePileRunner/Program.cs').read_text()
        for removed in ('StationBandProfile','split_at_pitch_boundaries_and_3h0_influence',
                        'short_only_supplemental_full_load_solver','widthPreMm',
                        'widthPsiOne','trialWidth','screeningShortWidth'):
            self.assertFalse(removed in cs,removed)
        self.assertIn('cscore_native_uniform_pitch',cs)
