"""Transverse pitch zoning: gaps, touching boundaries and weak uniform envelope."""
import copy
import unittest

from workbench import model
from workbench.transverse_zones import validate, envelope


class TransverseZonesTests(unittest.TestCase):
    def test_touching_zones_cover_whole_wall_without_base_pitch(self):
        zones=[{'top':0,'bottom':-6,'spacing_mm':300},
               {'top':-6,'bottom':-12,'spacing_mm':125}]
        data=envelope(zones,0,-12,400)
        self.assertEqual(data['uniform_pitch_mm'],300)
        self.assertFalse(data['background_used'])
        self.assertFalse(data['verified_for_design'])
        self.assertFalse(data['boundary_verified'])

    def test_gaps_use_background_pitch(self):
        data=envelope([{'top':-3,'bottom':-6,'spacing_mm':150}],0,-12,250)
        self.assertEqual(data['uniform_pitch_mm'],250)
        self.assertTrue(data['background_used'])
        self.assertEqual(data['method'],'whole_profile_uniform_maximum_spacing')

    def test_unordered_stations_do_not_change_sizing(self):
        a={'top':-6,'bottom':-12,'spacing_mm':170}
        b={'top':0,'bottom':-6,'spacing_mm':210}
        self.assertEqual(envelope([a,b],0,-12,200)['uniform_pitch_mm'],210)

    def test_no_zones_retains_single_default_pitch(self):
        self.assertEqual(envelope([],0,-12,180)['uniform_pitch_mm'],180)

    def test_rejects_overlap_outside_wall_bad_spacing_or_shape(self):
        cases=[
            [{'top':0,'bottom':-7,'spacing_mm':150},
             {'top':-6,'bottom':-12,'spacing_mm':250}],
            [{'top':1,'bottom':-4,'spacing_mm':150}],
            [{'top':0,'bottom':-13,'spacing_mm':150}],
            [{'top':-2,'bottom':-2,'spacing_mm':150}],
            [{'top':0,'bottom':-5,'spacing_mm':0}],
            [{'top':0,'bottom':-5,'spacing_mm':float('nan')}],
            [{'top':0,'bottom':-5,'spacing_mm':150,'unknown':1}],
        ]
        for zones in cases:
            with self.subTest(zones=zones), self.assertRaises(ValueError):
                validate(zones,0,-12,200)

    def test_project_validation_and_backward_compatibility(self):
        p=model.default_project()
        p.pop('transverse_zones')
        model.validate_project(p)
        p['transverse_zones']=[{'top':0,'bottom':-6,'spacing_mm':150}]
        model.validate_project(p)
        p['transverse_zones'].append({'top':-5,'bottom':-12,'spacing_mm':170})
        with self.assertRaisesRegex(ValueError,'перекрываются'):
            model.validate_project(p)

    def test_pile_cannot_acquire_wall_pitch_zones(self):
        p=model.default_project()
        p['kind']='pile'
        p['force_scale']='per_member'
        p['transverse_zones']=[{'top':0,'bottom':-5,'spacing_mm':150}]
        with self.assertRaisesRegex(ValueError,'только для стены'):
            model.validate_project(p)


if __name__=='__main__': unittest.main()
