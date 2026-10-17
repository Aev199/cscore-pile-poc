import copy
import unittest
from workbench import model as m


class WorkbenchTests(unittest.TestCase):
    def test_parse_and_convert_line_forces_once(self):
        rows=m.parse_table('z;case;basis;n;m;q;m_long\n0;A;ULS;-100;50;10;\n-2;A;ULS;-200;-30;20;')
        p=m.default_project();p.update(kind='pile',spacing_m=2,force_scale='per_metre',rows=rows)
        out=m.normalized_rows(p)
        self.assertEqual(out[0]['n'],-200)
        self.assertEqual(out[0]['m'],100)
        self.assertEqual(rows[0]['n'],-100)
        p['force_scale']='per_member'
        self.assertEqual(m.normalized_rows(p)[0]['n'],-100)

    def test_missing_phase_station_is_not_filled_or_ignored(self):
        p=m.default_project()
        p['rows']=[{'z':z,'case':case,'basis':'ULS','n':0,'m':30,'q':0,'m_long':None}
                   for z,case in [(0,'A'),(-2,'A'),(0,'B')]]
        with self.assertRaisesRegex(ValueError,'отмет'):m.validate_project(p)

    def test_no_envelope_force_mixing(self):
        p=m.default_project(); rows=m.normalized_rows(p)
        z=rows[0]['z']; req=m.section_input(p,[r for r in rows if r['z']==z])
        self.assertEqual(len(req['loads']),2)
        self.assertNotEqual(req['loads'][0]['n_kn_per_m'],req['loads'][1]['n_kn_per_m'])
        self.assertEqual([x['mx_knm_per_m'] for x in req['loads']],
                         [r['m'] for r in rows if r['z']==z and r['basis']=='ULS'])

    def test_zone_boundaries_and_background(self):
        stations=[{'z':0,'tiers':[1,1]},{'z':-2,'tiers':[2,1]},{'z':-4,'tiers':[2,1]},
                  {'z':-6,'tiers':[1,1]}]
        zones=m.suggest_zones(stations)
        self.assertEqual(zones,[{'top':0,'bottom':-6,'tiers':[2,1]}])
        self.assertEqual(m.tiers_at(-3,zones),[2,1])
        self.assertEqual(m.tiers_at(-7,zones),[1,1])
        with self.assertRaises(ValueError):m.validate_zones([{'top':0,'bottom':-4,'tiers':[2,1]},
                                                          {'top':-2,'bottom':-6,'tiers':[1,2]}],0,-6)

    def test_nested_cages_and_pile_counts(self):
        p=m.default_project()
        self.assertEqual(m.counts(p,[1,1]),[4,4])
        self.assertEqual(m.counts(p,[2,3]),[7,10])
        p['kind']='pile'
        self.assertEqual(m.counts(p,[3,1]),[24])

    def test_invalid_and_duplicate_rows(self):
        for text in ['z;case;basis;n;m;q;m_long\n0;A;ULS;nan;1;0;',
                     'z;case;basis;n;m;q;m_long\n0;A;SLS;0;1;0;']:
            with self.assertRaises(ValueError):m.parse_table(text)
        p=m.default_project();p['force_scale']='per_member'
        with self.assertRaises(ValueError):m.normalized_rows(p)

    def test_loaded_project_rejects_invalid_zone_geometry(self):
        p=m.default_project();p['zones']=[{'top':2,'bottom':-4,'tiers':[2,1]}]
        with self.assertRaises(ValueError):m.validate_project(p)

    def test_failed_engine_does_not_propose_reinforcement(self):
        p=m.default_project();p['materials_confirmed']=True
        def engine(req):
            return {'schema_version':1,'cases':[{'id':c['id'],
                'loads':[{'id':l['id'],'converged':False,'factor':None,'utilization':None,'error':'no convergence'}
                         for l in req['loads']], 'sls_gate':'skipped_uls_indeterminate','sls_probe':None}
                for c in req['candidates']]}
        r=m.design(p,engine=engine)
        self.assertTrue(all(x['tiers'] is None for x in r['stations']))
        self.assertEqual(r['zones'],[])
        self.assertFalse(r['all_checked'])

if __name__=='__main__':unittest.main()
