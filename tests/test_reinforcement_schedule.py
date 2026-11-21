"""Pragmatic KZh draft schedule: exact intervals, no invented normative PASS."""
import csv
import io
import unittest

from workbench import model
from workbench.reinforcement_schedule import allowed_pitches, to_csv


class DraftScheduleTests(unittest.TestCase):
    def test_mixed_longitudinal_and_transverse_boundaries(self):
        p=model.default_project()
        p['zones']=[{'top':0,'bottom':-6,'tiers':[2,1]}]
        p['transverse_zones']=[{'top':-3,'bottom':-9,'spacing_mm':150}]
        d=model.reinforcement_schedule(p)
        self.assertEqual(d['status'],'DRAFT_NOT_VERIFIED')
        self.assertFalse(d['verified_for_design'])
        self.assertEqual([(v['top_m'],v['bottom_m'],
                           v['face_minus_bars_per_cage'],
                           v['face_plus_bars_per_cage'],v['links_pitch_mm'])
                          for v in d['rows']],[
            (0,-3,7,4,200),(-3,-6,7,4,150),
            (-6,-9,4,4,150),(-9,-12,4,4,200)])
        self.assertTrue(all(v['cages']==3 for v in d['rows']))
        self.assertEqual(d['allowed_steps_mm'],[100,150,200,300])

    def test_defaults_merge_to_one_working_region(self):
        p=model.default_project()
        result=model.reinforcement_schedule(p)
        self.assertEqual(len(result['rows']),1)
        self.assertEqual((result['rows'][0]['top_m'],result['rows'][0]['bottom_m']),
                         (0,-12))
        p.pop('q_allowed_steps_mm')
        self.assertEqual(model.reinforcement_schedule(p)['allowed_steps_mm'],
                         [100,150,200,300])

    def test_zone_touches_do_not_duplicate_or_gap_and_csv_is_draft(self):
        p=model.default_project()
        p['transverse_zones']=[
            {'top':0,'bottom':-4,'spacing_mm':150},
            {'top':-4,'bottom':-8,'spacing_mm':200}]
        result=model.reinforcement_schedule(p)
        self.assertEqual([(v['top_m'],v['bottom_m']) for v in result['rows']],
                         [(0,-4),(-4,-12)])
        csvtext=to_csv(result)
        self.assertTrue(csvtext.startswith('\ufeff'))
        reader=list(csv.reader(io.StringIO(csvtext.lstrip('\ufeff')),delimiter=';'))
        self.assertEqual(len(reader),3)
        self.assertTrue(all(r[-1]=='ЧЕРНОВИК, НЕ ПРОВЕРЕНО' for r in reader[1:]))

    def test_catalog_custom_and_validation(self):
        self.assertEqual(allowed_pitches([300,100,150]),[100,150,300])
        p=model.default_project()
        p['q_allowed_steps_mm']=[100,125,150,200]
        self.assertEqual(model.reinforcement_schedule(p)['allowed_steps_mm'],
                         [100,125,150,200])
        for wrong in ([],[100,100],[0,100],['100'],[float('nan')],[2500]):
            with self.subTest(value=wrong):
                p['q_allowed_steps_mm']=wrong
                with self.assertRaises(ValueError):model.validate_project(p)

    def test_pile_does_not_get_a_wall_schedule(self):
        p=model.default_project()
        p['kind']='pile'
        p['force_scale']='per_member'
        with self.assertRaisesRegex(ValueError,'для стены'):
            model.reinforcement_schedule(p)


if __name__=='__main__':
    unittest.main()
