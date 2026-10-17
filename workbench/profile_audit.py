"""Diagnostic consistency audit of paired M/Q profiles; NEVER a design verdict.

All samples belong to one case and limit-state basis. M is kN*m or kN*m/m,
Q is kN or kN/m and z is metres. We use s=top-z (downwards), with both
possible sign conventions; no force values are modified.
"""
from __future__ import annotations

from collections import defaultdict
import math


def audit(rows):
    """Return phase-specific M'≈±Q diagnostics for already normalized rows.

    This is NOT verification of supports, loads, equilibrium, interpolation or
    inclined sections. Reporting the closest sign NEVER flips forces silently.
    """
    grouped = defaultdict(list)
    for row in rows:
        grouped[(row['basis'], row['case'])].append(row)
    reports = []
    for (basis, case), samples in sorted(grouped.items()):
        seq = sorted(samples, key=lambda r: -r['z'])
        positions = [r['z'] for r in seq]
        report = {'basis': basis, 'case': case, 'points': len(seq),
                  'span_m': (positions[0] - positions[-1]) if positions else 0.0,
                  'status': 'insufficient', 'closest_convention': 'unknown',
                  'relative_mismatch': None, 'both_sign_mismatch': None,
                  'max_step_m': None, 'usable_for_design': False}
        if len(seq) < 2:
            reports.append(report)
            continue
        steps = [seq[i]['z']-seq[i+1]['z'] for i in range(len(seq)-1)]
        report['max_step_m'] = max(steps)
        if any(step <= 1e-9 for step in steps):
            report['status'] = 'duplicate_station'
            reports.append(report)
            continue
        plus = minus = weight = signal = 0.0
        for a, b, ds in zip(seq, seq[1:], steps):
            dm = b['m']-a['m']
            dq = (a['q']+b['q'])*0.5*ds
            if not all(math.isfinite(x) for x in (dm, dq, ds)):
                raise ValueError('Нечисловые усилия при оценке согласованности M/Q')
            plus += abs(dm-dq)
            minus += abs(dm+dq)
            weight += max(abs(dm), abs(dq), 1.0)
            signal += abs(dm)+abs(dq)
        if signal < 1e-8:
            report['status'] = 'zero_signal'
            reports.append(report)
            continue
        p, n = plus/weight, minus/weight
        report['both_sign_mismatch'] = {'same': round(p, 6), 'opposite': round(n, 6)}
        report['relative_mismatch'] = round(min(p, n), 6)
        # Do not infer the sign for a poor fit or near-tied sign hypotheses.
        if min(p, n) > 0.12:
            report['status'] = 'inconsistent'
        elif abs(p-n) < 0.05:
            report['status'] = 'sign_ambiguous'
        else:
            report['closest_convention'] = 'same' if p < n else 'opposite'
            report['status'] = 'compatible'
        reports.append(report)
    counts = {status: sum(r['status'] == status for r in reports)
              for status in ('compatible', 'inconsistent', 'sign_ambiguous',
                             'duplicate_station', 'zero_signal', 'insufficient')}
    return {'scope': 'diagnostic_only_no_shear_approval',
            'profiles': reports,
            'summary': counts,
            'usable_for_design': False,
            'notes': 'M derivative compared to both +/-Q along downward s; no sign is applied. '
                     'PLAXIS axes, profile continuity, reactions and supports need engineering review.'}
