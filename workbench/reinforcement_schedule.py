"""Compact DRAFT section schedule for diaphragm-wall working drawings.

No normative PASS, no bent-bar lengths, no quantity takeoff, no implied
stirrup centerline count. Each interval is a design-intent record requiring
engineer check before creation of a reinforcement drawing or specification.
"""
from __future__ import annotations
import csv
import io
import math


DEFAULT_PITCHES_MM = (100, 150, 200, 300)


def allowed_pitches(values):
    if values is None:
        values = list(DEFAULT_PITCHES_MM)
    if not isinstance(values, list) or not 1 <= len(values) <= 15:
        raise ValueError('Нужно задать от 1 до 15 вариантов шага Q')
    result = []
    for value in values:
        if type(value) not in (int, float) or not math.isfinite(value) or not 0 < value <= 2000:
            raise ValueError('Шаг Q должен быть положительным числом, мм')
        if value in result:
            raise ValueError('Повторяющиеся варианты шага Q')
        result.append(value)
    return sorted(result)


def schedule(project, rows):
    if project['kind'] != 'wall':
        raise ValueError('Черновик ведомости пока реализован для стены в грунте')
    top = max(r['z'] for r in rows)
    bottom = min(r['z'] for r in rows)
    cuts = {top, bottom}
    for z in project.get('zones', []):
        cuts.update((z['top'], z['bottom']))
    for z in project.get('transverse_zones', []):
        cuts.update((z['top'], z['bottom']))
    marks = sorted(cuts, reverse=True)
    background = project['background']
    diameters = project['diameters']
    base_pitch = project.get('transverse', {}).get('spacing_mm', 200)
    q_diameter = project.get('transverse', {}).get('diameter_mm', 10)
    out = []
    for a, b in zip(marks, marks[1:]):
        midpoint = (a+b)/2
        tiers = [1, 1]
        for zone in project.get('zones', []):
            if zone['bottom'] <= midpoint <= zone['top']:
                tiers = [max(tiers[i], zone['tiers'][i]) for i in range(2)]
        pitch = base_pitch
        for zone in project.get('transverse_zones', []):
            if zone['bottom'] <= midpoint <= zone['top']:
                pitch = zone['spacing_mm']
        entry = {
            'top_m': a, 'bottom_m': b,
            'cages': project['wall']['cage_count'],
            'face_minus_bars_per_cage': (background[0]-1)*tiers[0]+1,
            'face_minus_diameter_mm': diameters[0],
            'face_plus_bars_per_cage': (background[1]-1)*tiers[1]+1,
            'face_plus_diameter_mm': diameters[1],
            'links_diameter_mm': q_diameter,
            'links_pitch_mm': pitch,
        }
        # Merge adjacent physically identical records to keep a short schedule.
        if out and out[-1]['bottom_m'] == a and all(
                out[-1][k] == v for k, v in entry.items()
                if k not in ('top_m', 'bottom_m')):
            out[-1]['bottom_m'] = b
        else:
            out.append(entry)
    return {
        'status': 'DRAFT_NOT_VERIFIED',
        'kind': 'wall',
        'title': project.get('title', 'Расчёт'),
        'allowed_steps_mm': allowed_pitches(project.get('q_allowed_steps_mm')),
        'units': {'elevation': 'm', 'length': 'mm'},
        'rows': out,
        'verified_for_design': False,
        'note': 'Черновик участков для КЖ; расчёт Q, анкеровка, '
                'гибы, марки и ведомость расхода стали не подтверждены.',
    }


def to_csv(data):
    if data['status'] != 'DRAFT_NOT_VERIFIED':
        raise ValueError('Неподдерживаемый статус ведомости')
    stream = io.StringIO(newline='')
    stream.write('\ufeff')
    writer = csv.writer(stream, delimiter=';')
    writer.writerow(['Верх, м', 'Низ, м', 'Каркасов',
                     'y−: стержней/каркас', 'y−: Ø, мм',
                     'y+: стержней/каркас', 'y+: Ø, мм',
                     'Хомут Ø, мм', 'Шаг хомутов, мм', 'Статус'])
    for row in data['rows']:
        writer.writerow([row['top_m'], row['bottom_m'], row['cages'],
                         row['face_minus_bars_per_cage'], row['face_minus_diameter_mm'],
                         row['face_plus_bars_per_cage'], row['face_plus_diameter_mm'],
                         row['links_diameter_mm'], row['links_pitch_mm'],
                         'ЧЕРНОВИК, НЕ ПРОВЕРЕНО'])
    return stream.getvalue()
