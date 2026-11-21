"""Hand arithmetic reference cases: SP63 shear calculations, not design approval."""
import math
import unittest

from tools.sp63_q_handcheck import handcheck


class IndependentQReferenceTests(unittest.TestCase):
    def params(self, **overrides):
        data=dict(b=1.0,h0=.5,rb=12000.0,rbt=1000.0,
                  phi_n=1.0,qsw=300.0,sw=.2,c=.75,q=400.0)
        data.update(overrides)
        return handcheck(**data)

    def test_normal_branch_fixed_hand_values(self):
        x=self.params()
        # 1.5*1000*1*0.5^2/.75 = 500 kN
        self.assertAlmostEqual(x['qb_kn'],500)
        # 0.75*300*.75 = 168.75 kN
        self.assertAlmostEqual(x['qsw_kn'],168.75)
        self.assertAlmostEqual(x['inclined_capacity_kn'],668.75)
        self.assertAlmostEqual(x['eta_inclined'],400/668.75)
        self.assertAlmostEqual(x['strip_kn'],1800)
        self.assertTrue(x['spacing_ok'])
        self.assertFalse(x['weak_links_formula'])

    def test_special_weak_links_branch_is_not_linear_in_inverse_pitch(self):
        x=self.params(qsw=100)
        # Alternative Qb numerator = 4*1.5*h0^2*qsw = 150
        # 150/.75=200 < normative minimum concrete=250.
        self.assertAlmostEqual(x['qb_kn'],250)
        self.assertAlmostEqual(x['qsw_kn'],56.25)
        self.assertAlmostEqual(x['inclined_capacity_kn'],306.25)
        self.assertTrue(x['weak_links_formula'])
        self.assertNotAlmostEqual(x['qb_kn'],500)

    def test_no_stirrups_and_excessive_spacing_are_explicit(self):
        bare=self.params(qsw=0)
        self.assertAlmostEqual(bare['qb_kn'],500)
        self.assertEqual(bare['qsw_kn'],0)
        spaced=self.params(qsw=300,sw=1.0)
        # Max usable spacing rbt*b*h0^2/Q=.625 m.
        self.assertFalse(spaced['spacing_ok'])
        self.assertAlmostEqual(spaced['qb_kn'],500)
        self.assertEqual(spaced['qsw_kn'],0)
        self.assertAlmostEqual(spaced['eta_inclined'],400/500)

    def test_projections_and_compression_strip(self):
        short=self.params(c=.2,phi_n=1.1)
        self.assertAlmostEqual(short['qb_kn'],1375)
        self.assertAlmostEqual(short['qsw_kn'],112.5)
        self.assertAlmostEqual(short['strip_kn'],1980)
        long=self.params(c=2.5)
        self.assertAlmostEqual(long['qb_kn'],250)
        self.assertAlmostEqual(long['qsw_kn'],225)

    def test_invalid_numeric_inputs_are_not_smoothed(self):
        for key,value in (('b',0),('qsw',-1),('sw',0),
                          ('rbt',float('nan')),('phi_n',float('inf'))):
            with self.subTest(key=key),self.assertRaises(ValueError):
                self.params(**{key:value})


if __name__=='__main__':
    unittest.main()
