"""Safety tests: stirrup coordinates are hypothetical until detailed and signed."""
import unittest

from workbench.stirrup_layout import (
    proposed_positions, count_in_projection, proposal_summary,
)


class ProposedStirrupLayoutTests(unittest.TestCase):
    def test_count_at_directional_projection_boundaries(self):
        bands=[dict(start_s_m=0,end_s_m=1,spacing_mm=250),
               dict(start_s_m=1,end_s_m=2,spacing_mm=500)]
        positions=proposed_positions(bands)
        self.assertEqual(positions,[0,0.25,0.5,0.75,1,1.5])
        self.assertEqual(count_in_projection(positions,0.75,0.25,+1),1)
        self.assertEqual(count_in_projection(positions,1,0.5,+1),1)
        self.assertEqual(count_in_projection(positions,1.5,0.5,-1),1)
        self.assertEqual(count_in_projection(positions,1,0.25,-1),1)
        self.assertEqual(count_in_projection(positions,0,0.25,+1),1)
        self.assertEqual(count_in_projection(positions,0.5,0.24,+1),0)

    def test_layout_is_always_unverified_and_band_start_is_assumed(self):
        result=proposal_summary([dict(start_s_m=0,end_s_m=2,spacing_mm=200)])
        self.assertEqual(result['link_count_per_cage'],10)
        self.assertEqual(result['first_s_m'],0)
        self.assertFalse(result['verified_for_design'])
        self.assertEqual(result['status'],'UNVERIFIED_PROPOSAL')
        self.assertIn('first_link_at_start_of_every_pitch_band',result['assumption'])

    def test_reject_bad_layout_and_excessive_density(self):
        cases=[
            [dict(start_s_m=0,end_s_m=2,spacing_mm=0)],
            [dict(start_s_m=0,end_s_m=1,spacing_mm=200),
             dict(start_s_m=1.1,end_s_m=2,spacing_mm=200)],
            [dict(start_s_m=0,end_s_m=2,spacing_mm=0.001)],
            [dict(start_s_m=1,end_s_m=2,spacing_mm=200)],
        ]
        for bands in cases:
            with self.subTest(bands=bands), self.assertRaises(ValueError):
                proposed_positions(bands)

    def test_invalid_projection_parameters_fail_closed(self):
        pos=proposed_positions([dict(start_s_m=0,end_s_m=3,spacing_mm=200)])
        for direction in (0,2,None):
            with self.assertRaises(ValueError):
                count_in_projection(pos,1.0,0.5,direction)
        with self.assertRaises(ValueError):
            count_in_projection(pos,1.0,0,-1)
        with self.assertRaises(ValueError):
            count_in_projection(pos,float('nan'),1,+1)


if __name__=='__main__':
    unittest.main()
