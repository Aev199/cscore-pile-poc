"""Native CScore assessment is complete in reporting, separate from selection."""
import copy
import unittest
from workbench.connection import default_context, validate_context, validate_report, expected_checks, methods
from workbench import model

class ConnectionContracts(unittest.TestCase):
    def project(self):
        p=model.default_project();p.update(materials_confirmed=True,eccentricity_confirmed=True)
        p['rows']=[dict(z=z,case='U',basis='ULS',n=-100,m=30,q=10,m_long=None) for z in (0,-2)]
        return p

    def test_context_never_invents_supports_or_lengths(self):
        c=default_context()
        self.assertEqual(c['second_order_mode'],'unspecified')
        self.assertFalse(c['inclined_moment_requested'])
        self.assertEqual(c['sections'],[])
        validate_context(c)
        bad=copy.deepcopy(c);bad['second_order_mode']='cscore_member'
        with self.assertRaisesRegex(ValueError,'длин'):
            validate_context(bad)

    def test_invalid_or_duplicate_section_context_rejected(self):
        c=default_context();c['sections']=[dict(z=0,support_distance_positive_m=-1,support_distance_negative_m=-1)]
        with self.assertRaises(ValueError):validate_context(c)
        c['sections']=[dict(z=0,support_distance_positive_m=1,support_distance_negative_m=1),dict(z=0.0,support_distance_positive_m=2,support_distance_negative_m=2)]
        with self.assertRaises(ValueError):validate_context(c)

    def request(self):
        return {'loads':[{'id':'U'}], 'connection_context':default_context()}

    def report(self,req):
        return {'version':1,'verified_for_design':False,'checks':[
            dict(check_id=k,case_id=case,status='not_evaluated',utilization=None,
                 engine_method=next(iter(methods(k))),reason='missing_input',verified_for_design=False)
            for k,case in expected_checks(req)]}

    def test_missing_data_is_incomplete_and_not_a_pass(self):
        req=self.request();r=validate_report(req,self.report(req))
        self.assertEqual(r['status'],'incomplete')
        self.assertFalse(r['verified_for_design'])

    def test_partial_duplicate_unknown_or_false_approval_rejected(self):
        req=self.request();r=self.report(req)
        for bad in (dict(r,checks=r['checks'][:-1]),
                    dict(r,checks=r['checks']+[r['checks'][0]]),
                    dict(r,verified_for_design=True)):
            with self.assertRaises(ValueError):validate_report(req,bad)
        r['checks'][0].update(status='passed',utilization=None)
        with self.assertRaises(ValueError):validate_report(req,r)

    def test_failing_native_ratio_cannot_be_reported_passed(self):
        req=self.request();r=self.report(req)
        row=next(c for c in r['checks'] if c['check_id']=='normal_strength')
        row.update(status='passed',utilization=1.1,reason='')
        with self.assertRaises(ValueError):validate_report(req,r)
        row['status']='failed'
        self.assertEqual(validate_report(req,r)['status'],'failed')

    def test_sls_instability_is_a_failure_and_qualifier_cannot_claim_strength(self):
        req=self.request();req['sls_probe']={'loads':[{'id':'S'}]}
        r=self.report(req)
        row=next(c for c in r['checks'] if c['check_id']=='sls_member_stability')
        row.update(status='unstable',engine_method='CScore.Sp63.RodEtaWiring.Apply',reason='native_SLS_member_unstable')
        self.assertEqual(validate_report(req,r)['status'],'failed')
        row=next(c for c in r['checks'] if c['check_id']=='shear_inclined')
        row.update(status='passed',utilization=.5,engine_method='CScore.Sp63Shear.Sp63ShearApplicability.Evaluate')
        with self.assertRaises(ValueError):validate_report(req,r)

    def test_known_failure_survives_missing_opposite_direction(self):
        req=self.request();r=self.report(req)
        row=next(c for c in r['checks'] if c['check_id']=='shear_inclined')
        row.update(status='failed',utilization=1.03,engine_method='CScore.Sp63Shear.ShearInclinedChecker.Check',
                   details={'directions_complete':False},reason='')
        checked=validate_report(req,r)
        self.assertEqual(checked['status'],'failed');self.assertTrue(checked['has_gaps'])
        row.update(status='passed',utilization=.7)
        with self.assertRaises(ValueError):validate_report(req,r)

    def test_station_support_directions_are_not_mirrored_or_invented(self):
        from workbench.connection import request_context
        p=self.project();p['calculation_context']={**default_context(),'sections':[
            dict(z=0,support_distance_positive_m=2,support_distance_negative_m=None)]}
        a=request_context(p,0);b=request_context(p,-2)
        self.assertEqual(a['support_distance_positive_m'],2)
        self.assertIsNone(a['support_distance_negative_m'])
        self.assertIsNone(b['support_distance_positive_m'])
        self.assertIsNone(b['support_distance_negative_m'])

    def test_assessment_passes_adopted_sections_without_changing_project(self):
        p=self.project();orig=copy.deepcopy(p);requests=[]
        def engine(req):
            requests.append(req)
            return {'schema_version':1,'cases':[{
                'id':req['candidates'][0]['id'], 'connection_report':self.report(req)}]}
        result=model.check_adopted(p,engine=engine)
        self.assertEqual(p,orig)
        self.assertEqual(len(requests),2)
        self.assertEqual(requests[0]['loads'][0]['q_kn'],30)
        self.assertEqual(requests[0]['loads'][0]['n_kn'],-300)
        self.assertTrue(all(r['connection_context']['eccentricity_in_loads'] for r in requests))
        self.assertEqual(result['status'],'incomplete')
        self.assertFalse(result['verified_for_design'])
        self.assertTrue(all(x['report']['status']=='incomplete' for x in result['stations']))

if __name__=='__main__':unittest.main()
