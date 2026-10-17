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
        if request.get('pitch_bands') and status != 'indeterminate':
            physical=request['pitch_bands']
            windows=r.get('pitch_windows')
            if (not isinstance(windows,list) or not 1<=len(windows)<=400
                    or r.get('reinforcement_model')!='constant_background_longitudinal_projection_neighborhood_pitch'):
                raise ValueError('CScore: отсутствует разбиение наклонных сечений по участкам')
            cursor=physical[0]['start_s_m']
            for win in windows:
                if (not isinstance(win,dict) or win.get('boundary_verified') is not False
                        or win.get('verified_for_design') is not False):
                    raise ValueError('CScore: недостоверный статус перехода хомутов')
                lo=win.get('start_s_m');hi=win.get('end_s_m')
                if (type(lo) not in (int,float) or type(hi) not in (int,float)
                        or not math.isfinite(lo) or not math.isfinite(hi)
                        or abs(lo-cursor)>1e-6 or hi<=lo+1e-9):
                    raise ValueError('CScore: разрыв, перекрытие или неверные границы расчётных участков Q')
                band=next((b for b in physical
                           if lo>=b['start_s_m']-1e-6
                           and hi<=b['end_s_m']+1e-6),None)
                if band is None:
                    raise ValueError('CScore: окно Q пересекает границу физического шага')
                pitch=win.get('adopted_pitch_mm')
                if (type(pitch) not in (int,float) or not math.isfinite(pitch)
                        or abs(pitch-band['spacing_mm'])>1e-6):
                    raise ValueError('CScore: принятый шаг Q не соответствует проекту')
                influence=win.get('influence_each_side_m')
                neighboring=win.get('governing_neighbor_pitch_mm')
                if (type(influence) not in (int,float) or not math.isfinite(influence)
                        or influence<=0 or type(neighboring) not in (int,float)
                        or not math.isfinite(neighboring)):
                    raise ValueError('CScore: недопустимый учёт соседних участков Q')
                effective=max(b['spacing_mm'] for b in physical
                              if b['end_s_m'] > lo-influence+1e-9
                              and b['start_s_m'] < hi+influence-1e-9)
                if not math.isclose(neighboring,effective,rel_tol=1e-7,abs_tol=1e-6):
                    raise ValueError('CScore: неверный шаг соседних участков Q')
                possible=[other['spacing_mm'] for other in expected_bands
                          if other['end_s_m']>=band['start_s_m']-influence-1e-9
                          and other['start_s_m']<=band['end_s_m']+influence+1e-9]
                if not possible or not math.isclose(neighboring,max(possible),
                                                   rel_tol=1e-9,abs_tol=1e-6):
                    raise ValueError('CScore: неверно учтены переходы между участками Q')
                zone_eta=win.get('utilization')
                if type(zone_eta) not in (int,float) or not math.isfinite(zone_eta) or zone_eta<0:
                    raise ValueError('CScore: отсутствует достоверный коэффициент участка Q')
                if win.get('status') != ('research_zone_below_one'
                                        if zone_eta<=1 else 'research_zone_exceeds_one'):
                    raise ValueError('CScore: несогласованный статус участка Q')
                if win.get('method') not in ('split_at_pitch_boundaries_and_3h0_influence',):
                    raise ValueError('CScore: неподтверждённый метод проверки границ Q')
                cursor=hi
            if abs(cursor-physical[-1]['end_s_m'])>1e-6:
                raise ValueError('CScore: не охвачена вся длина стены при проверке Q')
            combined_max=max(w['utilization'] for w in windows)
            overall_eta=r.get('utilization')
            if (type(overall_eta) not in (int,float) or not math.isfinite(overall_eta)
                    or not math.isclose(overall_eta,combined_max,rel_tol=1e-7,abs_tol=1e-7)):
                raise ValueError('CScore: итог Q не равен худшему участку')
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
