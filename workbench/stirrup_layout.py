"""Proposed (not verified) stirrup positions for diaphragm walls.

Every physical pitch band begins with a proposed tie at its upper boundary.
The tie grid is illustrative until first-tie offsets and detailing are approved.
s (m) increases downwards from the wall top. Never a design PASS.
"""
from bisect import bisect_left, bisect_right
import math

MAX_PROPOSED_LINKS = 100_000


def proposed_positions(pitch_bands):
    if not isinstance(pitch_bands, list) or not 1 <= len(pitch_bands) <= 201:
        raise ValueError('Нужны физические участки поперечной арматуры')
    positions = []
    prev_end = None
    for band in pitch_bands:
        if not isinstance(band, dict):
            raise ValueError('Неверное описание участка хомутов')
        s0 = band.get('start_s_m')
        s1 = band.get('end_s_m')
        pitch_mm = band.get('spacing_mm')
        if any(type(v) not in (int, float) or not math.isfinite(v)
               for v in (s0, s1, pitch_mm)):
            raise ValueError('Неопределённые отметки или шаг хомутов')
        step = pitch_mm / 1000.0
        if s0 < 0 or s1 <= s0 or not 0 < pitch_mm <= 2000:
            raise ValueError('Неверные границы или шаг поперечного армирования')
        if prev_end is not None and abs(s0 - prev_end) > 1e-7:
            raise ValueError('Физические участки имеют разрыв или перекрытие')
        n = max(1, math.ceil((s1 - s0) / step - 1e-9))
        if n + len(positions) > MAX_PROPOSED_LINKS:
            raise ValueError('Слишком много хомутов в предложенной раскладке')
        for j in range(n):
            point = s0 + j * step
            if point < s1 - 1e-9:
                positions.append(round(point, 9))
        prev_end = s1
    if not positions or abs(positions[0]) > 1e-9 or len(set(positions)) != len(positions):
        raise ValueError('Повторные или неполные отметки хомутов')
    return positions


def count_in_projection(positions, station_s, length_m, direction):
    """Tie at station excluded, tie at far end included (both directions).

    This convention is only for placement diagnostics, not normative steel area.
    """
    if direction not in (-1, 1):
        raise ValueError('Направление наклонного сечения должно быть ±1')
    if any(type(v) not in (int, float) or not math.isfinite(v)
           for v in (station_s, length_m)) or length_m <= 0:
        raise ValueError('Неверная стоянка или длина наклонного сечения')
    endpoint = station_s + direction * length_m
    if direction == 1:
        return bisect_right(positions, endpoint + 1e-9) - bisect_right(positions, station_s + 1e-9)
    return bisect_left(positions, station_s - 1e-9) - bisect_left(positions, endpoint - 1e-9)


def proposal_summary(pitch_bands):
    positions = proposed_positions(pitch_bands)
    gaps = [b-a for a,b in zip(positions, positions[1:])]
    return {
        'status': 'UNVERIFIED_PROPOSAL',
        'assumption': 'first_link_at_start_of_every_pitch_band',
        'verified_for_design': False,
        'link_count_per_cage': len(positions),
        'first_s_m': positions[0],
        'last_s_m': positions[-1],
        'maximum_center_to_center_gap_mm': round(max(gaps, default=0.0)*1000, 5),
        'note': 'Proposed locations only; offsets, first/last clearances, cages, '
                'link shape and anchorage must be confirmed on drawings.',
    }
