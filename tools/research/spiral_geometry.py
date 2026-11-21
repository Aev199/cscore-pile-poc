"""Geometric coefficient analysis only. No concrete/steel resistance calculation.

Orr et al. (2010), eqs. 6 and 9. Chord positions must be supplied by a
section-state analysis. This module is intentionally NOT used by the app.
"""
import math


def circular_efficiency(y_tension,y_compression,radius):
    """Exact mean of the transverse tangent projection between two chords."""
    vals=(y_tension,y_compression,radius)
    if any(type(v) not in (int,float) or not math.isfinite(v) for v in vals):
        raise ValueError('Finite chord coordinates and radius required')
    lo,hi=sorted((y_tension,y_compression))
    if radius<=0 or hi-lo<=1e-12*max(radius,1e-12):
        raise ValueError('Distinct tension/compression chords and positive radius required')
    if lo < -radius or hi > radius:
        raise ValueError('Chord outside the spiral: published real-valued integral not applicable')
    def primitive(u):
        return .5*(u*math.sqrt(max(0.,1-u*u))+math.asin(u))
    return radius/(hi-lo)*(primitive(hi/radius)-primitive(lo/radius))


def helix_projection(pitch,winding_diameter):
    if (any(type(v) not in (int,float) or not math.isfinite(v) for v in (pitch,winding_diameter))
            or pitch<0 or winding_diameter<=0):
        raise ValueError('Nonnegative pitch and positive winding diameter required')
    return 1/math.hypot(1,pitch/(math.pi*winding_diameter))
