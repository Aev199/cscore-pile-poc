"""A partial eccentricity response must never become a complete member check."""
import copy,json,unittest
from types import SimpleNamespace
from unittest.mock import patch
from workbench.member_loads import prepare


class MemberPreparationContract(unittest.TestCase):
    def request(self):
        return {'research_rectangle':{'height_mm':600,'width_mm':400},
                'loads':[dict(id='U',n_kn=-1000,mx_knm=0,my_knm=0,q_kn=100)],
                'connection_context':{'eccentricity_in_loads':False}}

    def response(self,pairs):
        return dict(schema_version=1,method='CScore.Sp63.Normal.Sp63MemberContext',
                    ea_x_mm=25,ea_y_mm=25,loads=[dict(id=f'U{i}',source_id='ULS:U',
                    n_kn=-1000,mx_total_knm=x*25,my_total_knm=y*25,
                    mx_long_knm=x*25,my_long_knm=y*25,sign_x=x,sign_y=y)
                    for i,(x,y) in enumerate(pairs)])

    def call(self,request,response):
        with patch('workbench.member_loads.subprocess.run',return_value=SimpleNamespace(
                returncode=0,stdout=json.dumps(response),stderr='')):
            return prepare(request,dict(mode='sp63',length_m=15,structural_scheme='statically_indeterminate'),'runner')

    def test_missing_or_duplicate_signs_rejected_before_request_mutates(self):
        for pairs in ([(-1,-1)],[(-1,-1),(-1,1),(1,-1)],
                      [(-1,-1),(-1,1),(1,-1),(1,-1)]):
            with self.subTest(pairs=pairs):
                req=self.request();before=copy.deepcopy(req)
                with self.assertRaisesRegex(ValueError,'знак'):self.call(req,self.response(pairs))
                self.assertEqual(req,before)

    def test_all_four_signs_are_kept_and_external_forces_preserved(self):
        req=self.request();self.call(req,self.response([(-1,-1),(-1,1),(1,-1),(1,1)]))
        self.assertEqual(len(req['loads']),4)
        self.assertTrue(req['connection_context']['eccentricity_in_loads'])
        self.assertTrue(all(l['q_kn']==100 and l['source_mx_knm']==0 and l['source_my_knm']==0 for l in req['loads']))
