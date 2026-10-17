"""Independent arithmetic check of SP 63.13330 (8.55)-(8.59).

Research benchmark, not a design solver. Units: kN, m, kPa. Geometry,
working bar legs, supports and detailing are caller responsibilities.
No dependency on pinned CScore, so regression tests can catch drift.
"""
import math


def handcheck(*, b, h0, rb, rbt, phi_n, qsw, sw, c, q,
              phi_applies_to_strip=True):
    vals=(b,h0,rb,rbt,phi_n,qsw,sw,c,q)
    if any(type(x) not in (int,float) or not math.isfinite(x) for x in vals):
        raise ValueError('All SP63 reference inputs must be finite numbers')
    if min(b,h0,rb,rbt,phi_n,sw,c) <= 0 or qsw < 0:
        raise ValueError('Invalid geometry, material, phi_n, spacing or qsw')
    if type(phi_applies_to_strip) is not bool:
        raise ValueError('phi_applies_to_strip must be boolean')
    # For constant Q, a single inclined section is checked at projection C.
    # Code branches transcribed independently from the SP63 expression, with
    # the geometric caps of the concrete and stirrup projections.
    max_spacing = rbt*b*h0*h0 / abs(q) if abs(q)>1e-12 else math.inf
    spacing_ok = sw <= max_spacing + 1e-12
    weak_links = 0 < qsw < .25*rbt*b
    if spacing_ok and weak_links:
        qb_numerator = 4 * 1.5 * h0*h0 * qsw
    else:
        qb_numerator = 1.5*rbt*b*h0*h0
    concrete_floor = .5*rbt*b*h0
    concrete_ceiling = 2.5*rbt*b*h0
    qb = phi_n*min(concrete_ceiling,max(concrete_floor,qb_numerator/c))
    csw = min(2*h0,max(h0,c))
    q_links = .75*qsw*csw if spacing_ok else 0.0
    strip = (phi_n if phi_applies_to_strip else 1.0)*.3*rb*b*h0
    return {
        'qb_kn': qb,
        'qsw_kn': q_links,
        'strip_kn': strip,
        'inclined_capacity_kn': qb+q_links,
        'eta_inclined': abs(q)/(qb+q_links),
        'eta_strip': abs(q)/strip,
        'spacing_ok': spacing_ok,
        'weak_links_formula': spacing_ok and weak_links,
    }
