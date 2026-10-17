"""Prepare phase-specific N/M/Q sample profiles for an exploratory CScore run.

The geometry is one deliberately CONSTANT section (background only).
No supports, anchorage, bar cutoffs or cross-zone reinforcement are inferred.
This module never edits or guesses the user's original force signs.
"""
from __future__ import annotations

import math
from .profile_audit import audit


def build_profiles(normalized_rows, width_m, convention, axes_confirmed):
    if axes_confirmed is not True:
        raise ValueError('Подтвердите направление осей M/Q по исходной модели PLAXIS')
    if convention not in ('same', 'opposite'):
        raise ValueError('Явно выберите знак dM/ds = +Q или dM/ds = -Q')
    if type(width_m) not in (int, float) or not math.isfinite(width_m) or width_m <= 0:
        raise ValueError('Неверная ширина захватки')
    report = audit(normalized_rows)
    checks = [r for r in report['profiles'] if r['basis'] == 'ULS']
    if not checks:
        raise ValueError('Нет расчётных сочетаний ULS для проверки эпюр')
    invalid = [r['case'] for r in checks
               if r['status'] != 'compatible' or r['closest_convention'] != convention]
    if invalid:
        raise ValueError('M/Q не согласованы с подтверждённым направлением для: '
                         + ', '.join(invalid[:8]) + '. Проверьте оси, знаки и исходные усилия.')
    groups = {}
    for row in normalized_rows:
        if row['basis'] == 'ULS':
            groups.setdefault(row['case'], []).append(row)
    profiles = []
    sign = 1 if convention == 'same' else -1
    for case, samples in sorted(groups.items()):
        ordered = sorted(samples, key=lambda v: -v['z'])
        top = ordered[0]['z']
        if len(ordered) < 2 or any(ordered[i]['z'] <= ordered[i+1]['z']
                                    for i in range(len(ordered)-1)):
            raise ValueError(f'{case}: нужны различные отметки для одного сочетания')
        series = []
        for row in ordered:
            values = {'s_m': top - row['z'],
                      'n_kn': row['n'] * width_m,
                      'm_knm': row['m'] * width_m,
                      # The explicit, user-confirmed PLAXIS→CScore convention.
                      'q_kn': row['q'] * width_m * sign}
            if not all(math.isfinite(v) for v in values.values()):
                raise ValueError(f'{case}: недопустимые усилия')
            series.append(values)
        profiles.append({'id': case, 'samples': series})
    return {'scope': 'full_phase_profile_research_constant_background_only',
            'orientation': convention, 'axis_confirmed': True,
            'support_model': 'none',
            'profiles': profiles}


def validate_profile_results(request, response):
    """Fail closed if the engine omits a phase, invents approvals or returns NaN."""
    if not isinstance(response, list):
        raise ValueError('CScore не вернул результаты эпюр Q')
    expected = {r['id'] for r in request['profiles']}
    observed = set()
    for r in response:
        if not isinstance(r, dict) or r.get('id') not in expected or r['id'] in observed:
            raise ValueError('CScore: неизвестное или повторное сочетание Q')
        observed.add(r['id'])
        if r.get('verified_for_design') is not False or r.get('support_model') != 'none':
            raise ValueError('Недопустимый нормативный допуск Q или модель опирания')
        status = r.get('status')
        if status not in ('research_profile_below_one', 'research_profile_exceeds_one',
                          'indeterminate'):
            raise ValueError('CScore вернул неизвестный статус эпюры Q')
        ratio = r.get('utilization')
        if status == 'indeterminate':
            if ratio is not None:
                raise ValueError('При неопределённом Q коэффициент должен отсутствовать')
        else:
            if type(ratio) not in (int, float) or not math.isfinite(ratio) or ratio < 0:
                raise ValueError('Некорректный коэффициент использования Q')
            if (ratio <= 1.0) != (status == 'research_profile_below_one'):
                raise ValueError('Статус профиля Q не соответствует коэффициенту')
    if observed != expected:
        raise ValueError('CScore не вернул все сочетания Q')
    return response
