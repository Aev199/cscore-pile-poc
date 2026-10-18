"""Transverse-link spacing zones for a vertical diaphragm wall.

Geometry and adopted pitch only. No resistance calculation or pitch-envelope
model is implemented here; native CScore profile calls require constant pitch.
"""
from __future__ import annotations

import math


def _finite(value, label):
    if type(value) not in (int, float) or not math.isfinite(value):
        raise ValueError(f'{label}: требуется конечное число')
    return float(value)


def validate(zones, top, bottom, base_pitch):
    """Nonoverlapping [bottom,top] pitch overrides; gaps use base pitch."""
    t=_finite(top,'Верх стены')
    b=_finite(bottom,'Низ стены')
    base=_finite(base_pitch,'Основной шаг хомутов')
    if t <= b or not 0 < base <= 2000:
        raise ValueError('Некорректные пределы стены или основной шаг хомутов')
    if not isinstance(zones,list) or len(zones)>100:
        raise ValueError('Не более 100 участков поперечного армирования')
    sorted_zones=[]
    for i,zone in enumerate(zones):
        if not isinstance(zone,dict) or set(zone)!= {'top','bottom','spacing_mm'}:
            raise ValueError(f'Участок Q {i+1}: неверный формат')
        zt=_finite(zone['top'],'Верх участка Q')
        zb=_finite(zone['bottom'],'Низ участка Q')
        pitch=_finite(zone['spacing_mm'],'Шаг хомутов')
        if not b <= zb < zt <= t or not 0 < pitch <= 2000:
            raise ValueError(f'Участок Q {i+1}: границы или шаг недопустимы')
        sorted_zones.append({'top':zt,'bottom':zb,'spacing_mm':pitch})
    sorted_zones.sort(key=lambda v:-v['top'])
    for left,right in zip(sorted_zones,sorted_zones[1:]):
        if right['top']>left['bottom']+1e-9:
            raise ValueError('Участки поперечного армирования перекрываются')
    return sorted_zones


def pitch_bands(zones, top, bottom, base_pitch):
    """Partition the entire wall into physical constant-pitch bands.

    s = top - z (metres), positive downwards. Continuous adjacent bands cover
    every depth exactly, with the base pitch only in gaps. Intersections at
    boundaries remain drawing geometry. The CScore adapter rejects variable
    pitch rather than constructing a separate resistance model.
    """
    ordered=validate(zones,top,bottom,base_pitch)
    result=[]
    cursor=0.0
    length=float(top)-float(bottom)
    for zone in ordered:
        start=float(top)-zone['top']
        end=float(top)-zone['bottom']
        if start>cursor+1e-9:
            result.append({'start_s_m':cursor,'end_s_m':start,
                           'spacing_mm':float(base_pitch)})
        result.append({'start_s_m':start,'end_s_m':end,
                       'spacing_mm':zone['spacing_mm']})
        cursor=end
    if length>cursor+1e-9:
        result.append({'start_s_m':cursor,'end_s_m':length,
                       'spacing_mm':float(base_pitch)})
    if not result or abs(result[0]['start_s_m'])>1e-8 or abs(result[-1]['end_s_m']-length)>1e-8:
        raise ValueError('Поперечная арматура не покрывает длину стены')
    for a,b in zip(result,result[1:]):
        if abs(a['end_s_m']-b['start_s_m'])>1e-8:
            raise ValueError('Разрыв в участках поперечной арматуры')
    return result
