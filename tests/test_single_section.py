"""Single-section input must never fabricate a second depth or select bars."""
import unittest
from workbench import model
import test_connection_cycle


class SingleSectionTests(unittest.TestCase):
    def project(self):
        p=model.default_project()
        p.update(materials_confirmed=True, eccentricity_confirmed=True, max_tier=1)
        p['rows']=[dict(z=0,case='ULS',basis='ULS',n=-100,m=30,q=10,m_long=None)]
        return p

    def test_one_station_is_explicitly_scoped(self):
        p=self.project()
        with self.assertRaises(ValueError):model.validate_project(p)
        self.assertEqual(len(model.validate_project(p,single_section=True)),1)
        p['rows'].append(dict(p['rows'][0],z=-1))
        with self.assertRaises(ValueError):model.validate_project(p,single_section=True)

    def test_single_section_rejects_reinforcement_zones_and_search(self):
        p=self.project();p['zones']=[dict(top=0,bottom=-1,tiers=[2,2])]
        with self.assertRaises(ValueError):model.validate_project(p,single_section=True)
        p['zones']=[];p['max_tier']=2
        with self.assertRaises(ValueError):model.validate_project(p,single_section=True)

    def test_single_station_reaches_existing_engine_once_for_wall_and_pile(self):
        for kind in ('wall','pile'):
            p=self.project();p['kind']=kind
            if kind=='pile':p['force_scale']='per_member'
            requests=[]
            def engine(req):
                requests.append(req)
                return dict(schema_version=1,cases=[dict(id=req['candidates'][0]['id'],
                    connection_report=test_connection_cycle.ConnectionContracts().report(req))])
            result=model.check_adopted(p,engine=engine,single_section=True)
            self.assertEqual(len(requests),1)
            self.assertEqual(len(requests[0]['candidates']),1)
            self.assertEqual([s['z'] for s in result['stations']],[0])
            self.assertFalse(result['verified_for_design'])

    def test_manual_rectangle_uses_the_displayed_physical_cage_coordinates(self):
        p=self.project()
        p['wall'].update(panel_width_mm=400,thickness_mm=600,cage_count=1,
                         cage_width_mm=316,end_cover_mm=42,cover_mm=42,ties_diameter_mm=8)
        p['background']=[4,4];p['diameters']=[25,25]
        requests=[]
        def engine(req):
            requests.append(req)
            return dict(schema_version=1,cases=[dict(id=req['candidates'][0]['id'],
                connection_report=test_connection_cycle.ConnectionContracts().report(req))])
        model.check_adopted(p,engine=engine,single_section=True)
        bars=requests[0]['research_rectangle']['bars']
        self.assertEqual(len(bars),8)
        self.assertAlmostEqual(min(b['x_mm'] for b in bars),-137.5)
        self.assertAlmostEqual(max(b['x_mm'] for b in bars),137.5)
        self.assertEqual({b['y_mm'] for b in bars},{-237.5,237.5})

    def test_second_moment_is_converted_once_and_reaches_cscore(self):
        p=self.project();p['wall']['panel_width_mm']=400
        p['wall'].update(cage_count=1,cage_width_mm=300)
        p['rows'][0]['my']=50
        requests=[]
        def engine(req):
            requests.append(req)
            return dict(schema_version=1,cases=[dict(id=req['candidates'][0]['id'],
                connection_report=test_connection_cycle.ConnectionContracts().report(req))])
        model.check_adopted(p,engine=engine,single_section=True)
        self.assertEqual(requests[0]['loads'][0]['my_knm'],20)

    def test_automatic_eccentricity_requires_real_length_and_rejects_double_application(self):
        p=self.project()
        p['member_load_preparation']={'mode':'sp63','structural_scheme':'statically_indeterminate'}
        with self.assertRaisesRegex(ValueError,'длину'):model.validate_project(p,single_section=True)
        p['member_load_preparation']['length_m']=15
        with self.assertRaisesRegex(ValueError,'повторно'):model.validate_project(p,single_section=True)
        p['eccentricity_confirmed']=False
        model.validate_project(p,single_section=True)
