"""Geometry identities, not tests against an invented resistance formula."""
import math
import unittest
from tools.research.spiral_geometry import circular_efficiency,helix_projection


class SpiralGeometryIdentities(unittest.TestCase):
    def test_published_symmetric_example(self):
        self.assertAlmostEqual(circular_efficiency(-.4,.4,.45),.8449505920335708,places=12)

    def test_full_diameter_integral_is_pi_over_four(self):
        self.assertAlmostEqual(circular_efficiency(-1,1,1),math.pi/4,places=12)

    def test_geometry_is_scale_and_direction_invariant(self):
        c=circular_efficiency(-.1,.3,.35)
        self.assertAlmostEqual(c,circular_efficiency(-100,300,350),places=12)
        self.assertAlmostEqual(c,circular_efficiency(.1,-.3,.35),places=12)

    def test_independent_midpoint_quadrature(self):
        lo,hi,r=-.1,.3,.35;n=20000
        quadrature=sum(math.sqrt(1-((lo+(i+.5)*(hi-lo)/n)/r)**2) for i in range(n))/n
        self.assertAlmostEqual(circular_efficiency(lo,hi,r),quadrature,places=8)

    def test_invalid_or_missing_chord_is_never_clamped_into_a_coefficient(self):
        for a,b,r in [(0,0,1),(-2,.5,1),(-.5,2,1),(0,1,0),(0,float('nan'),1)]:
            with self.subTest(a=a,b=b,r=r),self.assertRaises(ValueError):
                circular_efficiency(a,b,r)

    def test_helix_projection_limits(self):
        self.assertEqual(helix_projection(0,.7),1)
        self.assertLess(helix_projection(.3,.7),helix_projection(.2,.7))
        self.assertAlmostEqual(helix_projection(.2,.7),helix_projection(200,700),places=12)


if __name__=='__main__':unittest.main()
