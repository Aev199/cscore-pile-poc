"""Auditing sampled M/Q tuples must never yield a design PASS."""
import copy
import unittest
from workbench.profile_audit import audit


def rows(q=20.0, moments=(0.0, 20.0, 40.0), case='A', basis='ULS'):
    return [dict(z=-i, q=q, m=float(v), n=-10.0, basis=basis, case=case,
                 m_long=None) for i, v in enumerate(moments)]


class ProfileAuditTests(unittest.TestCase):
    def test_slope_plus_matches_q(self):
        r=audit(rows())
        self.assertEqual(r['profiles'][0]['status'], 'compatible')
        self.assertEqual(r['profiles'][0]['closest_convention'], 'same')
        self.assertEqual(r['profiles'][0]['relative_mismatch'], 0)
        self.assertFalse(r['usable_for_design'])

    def test_slope_minus_matches_q_without_changing_input(self):
        data=rows(moments=(0,-20,-40)); before=copy.deepcopy(data)
        result=audit(data)['profiles'][0]
        self.assertEqual(result['status'],'compatible')
        self.assertEqual(result['closest_convention'],'opposite')
        self.assertEqual(data,before)

    def test_disagreement_and_duplicates_do_not_pass(self):
        r=audit(rows(q=20,moments=(0,90,-40)))
        self.assertEqual(r['profiles'][0]['status'],'inconsistent')
        points=rows();points.append(dict(points[1]))
        self.assertEqual(audit(points)['profiles'][0]['status'],'duplicate_station')

    def test_uls_sls_and_case_not_mixed(self):
        all_rows = rows(case='A')+rows(q=-20, moments=(0,-20,-40), case='B')+rows(q=0, moments=(0,0,0), case='A', basis='SLS')
        result=audit(all_rows)
        self.assertEqual(len(result['profiles']),3)
        self.assertEqual(result['summary']['compatible'],2)
        self.assertEqual(result['summary']['zero_signal'],1)
        self.assertTrue(all(not p['usable_for_design'] for p in result['profiles']))

    def test_one_point_or_unbalanced_sign_is_not_positive(self):
        self.assertEqual(audit(rows()[:1])['profiles'][0]['status'],'insufficient')
        r=audit(rows(q=0,moments=(0,100,0)))['profiles'][0]
        self.assertEqual(r['status'],'inconsistent')

if __name__=='__main__': unittest.main()
