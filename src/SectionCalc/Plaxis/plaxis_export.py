"""Read-only export of PLAXIS 2D / 3D results for the section application.

Run with the Python that ships with PLAXIS (it contains plxscripting):
    python plaxis_export.py input --port 10000 [--output-port 10001]
        the plates / embedded beam rows selected in Input (PLAXIS 3D: the
        embedded beams / beams, each one a pile of a group), results of every
        phase except Safety, Output opened by Input (g_i.view);
    python plaxis_export.py catalog --port 10001
    python plaxis_export.py extract --port 10001 --request request.json
        the same directly from an open Output.
The password of the remote scripting servers is taken from the environment
variable PLAXIS_PASSWORD. The result is JSON on stdout; errors are JSON
{"error": "..."} with exit code 1. Values are in the project units of PLAXIS;
the application converts them.
"""
import argparse
import faulthandler
import json
import math
import os
import sys
import threading
import time

STARTED = time.time()


def log(message):
    """Progress for the application: one line on stderr (stdout carries the JSON result)."""
    sys.stderr.write(f'[{time.time() - STARTED:6.1f} s] {message}\n')
    sys.stderr.flush()

KINDS = (
    # kind, collections, result type groups
    ('Plate', ('Plates',), ('Plate',)),
    ('EmbeddedBeamRow', ('EmbeddedBeamRows', 'EmbeddedBeams'), ('EmbeddedBeamRow', 'EmbeddedBeam')),
)
# Candidate result type names, in order of preference.
RESULTS = {
    'x': ('X',),
    'y': ('Y',),
    'n': ('Nx2D', 'N2D', 'N'),
    'm': ('M2D', 'M'),
    'q': ('Q2D', 'Q'),
}
# PLAXIS 3D: piles as embedded beams or beams, forces per pile in the local axes 1 (along), 2, 3.
KINDS3D = (
    ('EmbeddedBeam', ('EmbeddedBeams',), ('EmbeddedBeam',)),
    ('Beam', ('Beams',), ('Beam',)),
)
RESULTS3D = {
    'x': ('X',), 'y': ('Y',), 'z': ('Z',),
    'n': ('N',), 'm2': ('M2',), 'm3': ('M3',), 'q12': ('Q12',), 'q13': ('Q13',),
}


class ExportError(Exception):
    pass


def text(obj, key):
    try:
        return str(getattr(obj, key).value)
    except Exception:
        return None


def name_of(obj):
    return text(obj, 'Name') or text(obj, 'Identification') or str(obj)


def limited(fn, seconds, what):
    """
    A remote call that PLAXIS may never answer: run it in a thread and give up after the time limit,
    so that the export reports the step instead of hanging.
    """
    box = {}

    def run():
        try:
            box['value'] = fn()
        except Exception as exc:
            box['error'] = exc
    worker = threading.Thread(target=run, daemon=True)
    worker.start()
    worker.join(seconds)
    if worker.is_alive():
        raise TimeoutError(f'PLAXIS не ответил за {seconds} с: {what}')
    if 'error' in box:
        raise box['error']
    return box.get('value')


def output_phase_names(output, wanted):
    """
    Output phases of the wanted Input names: {name: (index, phase)}. Every name is read with a time limit and
    logged; the search stops when all wanted phases are found.
    """
    found, stuck = {}, 0
    for i, phase in enumerate(output.phases):
        try:
            name = limited(lambda: name_of(phase), 20, f'имя фазы Output №{i}')
            stuck = 0
        except TimeoutError as exc:
            log(str(exc))
            stuck += 1
            if stuck >= 2:
                raise ExportError(f'PLAXIS Output не отвечает на чтение фаз (фаза №{i}). Журнал: стек вызовов Python в нём '
                                  'показывает, где остановилось чтение.')
            continue
        log(f'Output: фаза №{i} — {name}')
        if name in wanted:
            found[name] = (i, phase)
            if len(found) == len(wanted):
                break
    return found


def first(obj, names):
    for name in names:
        try:
            return getattr(obj, name), name
        except Exception:
            pass
    return None, None


CALC_KEYS = ('DeformCalcType', 'CalculationType', 'CalcType', 'DeformCalculationType')


def raw_calc_types(phase):
    """Every readable form of the calculation type: {key: text}; PLAXIS versions differ in names and enum values."""
    found = {}
    for key in CALC_KEYS:
        try:
            attr = getattr(phase, key)
        except Exception:
            continue
        for form in (lambda: attr.value, lambda: str(attr)):
            try:
                value = str(form())
            except Exception:
                continue
            if value and value != 'None':
                found[key] = value
                break
    return found


def calc_type(phase):
    found = raw_calc_types(phase)
    # A Safety type in any form wins (PLAXIS 3D may give it under another key or as a longer text).
    for value in found.values():
        if 'safety' in value.lower():
            return 'Safety'
    return next(iter(found.values()), None)


def is_safety(phase):
    kind = calc_type(phase)
    if kind is not None and 'safety' in kind.lower():
        return True
    # Safety (φ-c reduction) is calculated with incremental multipliers ΣMsf; plastic phases with staged construction.
    try:
        loading = str(phase.Deform.LoadingType.value).lower()
    except Exception:
        loading = ''
    return 'multiplier' in loading and 'staged' not in loading


PREFIXES = (('Plate', ('Plate',)), ('EmbeddedBeamRow', ('EmbeddedBeamRow', 'EmbeddedBeam')), ('Beam', ('Beam',)))


def feature_of(obj):
    """(kind, name) of a selected plate / embedded beam row, also when its line is selected."""
    plx = str(getattr(obj, '_plx_type', '') or '')
    name = name_of(obj)
    for kind, prefixes in PREFIXES:
        if plx in prefixes or any(name.startswith(p + '_') for p in prefixes):
            return kind, name
    for kind, prefixes in PREFIXES:
        for attr in prefixes:
            try:
                feature = getattr(obj, attr)
            except Exception:
                continue
            if feature is not None and not isinstance(feature, (str, int, float, bool)):
                return kind, name_of(feature)
    return None


COORDS = ('x', 'y', 'z')


def envelope_columns(results):
    """['x', 'y', 'n_min', 'n_max', 'm_min', ...]: coordinates, then the bounds of every force."""
    return ([k for k in results if k in COORDS]
            + [f'{k}_{b}' for k in results if k not in COORDS for b in ('min', 'max')])


ENVELOPE_COLUMNS = envelope_columns(RESULTS)


def envelope_types(group, results=RESULTS):
    """Envelope result types of a structure group: {'n_min': (type, name), ...}, or None if Output has none."""
    try:
        listed = [n for n in dir(group) if 'envelope' in n.lower()]
    except Exception:
        listed = []
    found = {}
    for key in [k for k in results if k not in COORDS]:
        for bound in ('Max', 'Min'):
            hit = None
            for base in results[key]:
                guesses = [n for n in listed if n.lower().startswith(base.lower() + '_') and bound.lower() in n.lower()]
                guesses += [n for n in listed if n.lower().startswith(base.lower()) and bound.lower() in n.lower()
                            and not n[len(base):len(base) + 1].isdigit()]
                guesses += [f'{base}_Envelope{bound}', f'{base}Envelope{bound}', f'{base}_{bound}Envelope', f'{base}{bound}Envelope']
                for name in guesses:
                    rtype, _ = first(group, [name])
                    if rtype is not None:
                        hit = (rtype, name)
                        break
                if hit:
                    break
            if hit is None:
                return None
            found[f'{key}_{bound.lower()}'] = hit
    return found


def previous(phase):
    for key in ('PreviousPhase', 'Previous'):
        try:
            prev = getattr(phase, key)
        except Exception:
            continue
        # The initial phase refers to itself; plxscripting returns a new proxy each time, so compare names.
        if prev is not None and prev is not phase and name_of(prev) != name_of(phase):
            return prev
    return None


class PhaseRow:
    """Input phase read once: Input is not asked again after it has opened Output (PLAXIS 3D Input may be busy then)."""

    def __init__(self, phase):
        self.name = name_of(phase)
        self.title = text(phase, 'Identification')
        self.raw = raw_calc_types(phase)
        self.safety = is_safety(phase)
        self.calc_type = 'Safety' if self.safety else calc_type(phase)
        prev = previous(phase)
        self.previous = name_of(prev) if prev is not None else None

    def info(self):
        return {'name': self.name, 'title': self.title, 'calc_type': self.calc_type}


def branches_of(rows):
    """[(leaf, [phase names of its branch from the start])] of the non-Safety phases; None if a branch has Safety."""
    by_name = {r.name: r for r in rows}
    used = [r for r in rows if not r.safety]
    parents = {r.previous for r in used if r.previous}
    result = []
    for leaf in [r for r in used if r.name not in parents]:
        chain, cur = [], leaf
        while cur is not None and cur.name not in {c.name for c in chain}:
            chain.append(cur)
            cur = by_name.get(cur.previous) if cur.previous else None
        if any(r.safety for r in chain):
            log(f'Safety внутри ветки {leaf.name} — чтение по фазам')
            return None
        result.append((leaf, [r.name for r in reversed(chain)]))
    return result


def matches(output_name, input_name):
    # Staged construction splits a structure: Plate_1 becomes Plate_1_1, Plate_1_2, ... Output may list
    # the segments or only their parent Plate_1; either side may be the selected one.
    return (output_name == input_name or output_name.startswith(input_name + '_')
            or input_name.startswith(output_name + '_'))


def moment_sign(output, objects, phase):
    """
    Which face positive M stretches, from the deflected shape: x = x0 + Ux(y); where Ux'' > 0 the wall bends
    towards +x and its left (smaller x) face is stretched. +1: M > 0 stretches the right face (larger x).
    Returns {'sign', 'agreement' 0…1, 'phase', 'excavation' 'right' / 'left' / None} or None.
    """
    group, _ = first(output.g.ResultTypes, ('Plate',))
    ux_type, _ = first(group, ('Ux',)) if group is not None else (None, None)
    if ux_type is None:
        return None
    rows, peak = {}, 0.0
    for _, obj, types in objects:
        try:
            ys = output.g.getresults(obj, phase, types['y'], 'node')
            us = output.g.getresults(obj, phase, ux_type, 'node')
            ms = output.g.getresults(obj, phase, types['m'], 'node')
        except Exception:
            continue
        for y, u, m in zip(ys, us, ms):
            rows.setdefault(round(float(y), 4), []).append((float(u), float(m)))
            if abs(float(u)) > abs(peak):
                peak = float(u)
    pts = sorted((y, sum(u for u, _ in v) / len(v), sum(m for _, m in v) / len(v)) for y, v in rows.items())
    score = total = 0.0
    for i in range(1, len(pts) - 1):
        (y0, u0, _), (y1, u1, m1), (y2, u2, _) = pts[i - 1], pts[i], pts[i + 1]
        if y1 - y0 <= 0 or y2 - y1 <= 0:
            continue
        curvature = 2 * ((u2 - u1) / (y2 - y1) - (u1 - u0) / (y1 - y0)) / (y2 - y0)
        score += m1 * -curvature
        total += abs(m1 * curvature)
    if total <= 0:
        return None
    # A retaining wall moves towards the excavation: the side of the largest displacement.
    excavation = None if peak == 0 else ('right' if peak > 0 else 'left')
    return {'sign': 1 if score > 0 else -1, 'agreement': round(abs(score) / total, 3), 'phase': name_of(phase),
            'excavation': excavation}


def select_objects(output, kind, names):
    """Output objects of the selection: exact names if Output lists them, otherwise parents or segments."""
    same_kind = [(i, obj, t) for i, (k, obj, t) in enumerate(output.objects) if k == kind]
    exact = [o for o in same_kind if name_of(o[1]) in names]
    found = exact or [o for o in same_kind if any(matches(name_of(o[1]), n) for n in names)]
    # A parent already contains its segments: keep the outermost objects only.
    found = [o for o in found if not any(name_of(o[1]).startswith(name_of(p[1]) + '_') for p in found if p is not o)]
    if not found:
        listed = [name_of(obj) for _, obj, _ in same_kind]
        shown = ', '.join(listed[:15]) + (' …' if len(listed) > 15 else '')
        raise ExportError('В Output не найдены выбранные элементы: ' + ', '.join(names[:5]) + (' …' if len(names) > 5 else '')
                          + f'. Output видит ({kind}): ' + (shown or 'ничего'))
    return found


def is_3d(g):
    """PLAXIS 3D Output: the structure result groups have a Z coordinate."""
    for _, _, groups in KINDS3D + KINDS:
        group, _ = first(g.ResultTypes, groups)
        if group is not None and first(group, ('Z',))[0] is not None:
            return True
    return False


def resolve(output, selected):
    """
    Output objects of the Input selection: [(kind, [(id, object, types)], pile or None)].
    PLAXIS 2D: the selection is one wall / pile row (several segments of one vertical); PLAXIS 3D: every
    selected embedded beam / beam is a pile of a group, its segments in Output are joined.
    """
    kind = selected[0][0]
    if not output.is3d:
        return kind, [(o, None) for o in select_objects(output, kind, [name for _, name in selected])]
    if kind == 'Plate':
        raise ExportError('PLAXIS 3D: стены (Plate) пока не поддерживаются — выделите сваи (Embedded beam или Beam)')
    if kind == 'EmbeddedBeamRow':
        kind = 'EmbeddedBeam'
    found, seen = [], set()
    for _, name in selected:
        for o in select_objects(output, kind, [name]):
            if o[0] in seen:
                raise ExportError(f'Элемент {name_of(o[1])} Output относится к нескольким выделенным сваям')
            seen.add(o[0])
            found.append((o, name))
    return kind, found


class Output:
    def __init__(self, g):
        self.g = g
        self.objects = []          # (kind, object, {key: result type})
        self.result_names = {}     # kind -> {key: name}
        self.is3d = is_3d(g)
        self.results = RESULTS3D if self.is3d else RESULTS
        self.columns = list(self.results)
        self.envelope_columns = envelope_columns(self.results)
        for kind, collections, groups in (KINDS3D if self.is3d else KINDS):
            items, _ = first(g, collections)
            group, _ = first(g.ResultTypes, groups)
            if items is None or group is None:
                continue
            types, names = {}, {}
            for key, candidates in self.results.items():
                types[key], names[key] = first(group, candidates)
            if any(t is None for t in types.values()):
                continue
            self.result_names[kind] = names
            for obj in items:
                self.objects.append((kind, obj, types))
        self.phases = list(g.Phases)
        if not self.objects:
            raise ExportError('В модели нет свай (Embedded beam / Beam) с результатами X, Y, Z, N, M2, M3, Q12, Q13' if self.is3d
                              else 'В модели нет Plate или Embedded Beam Row с результатами X, Y, N, M, Q')

    def info(self):
        general = getattr(self.g, 'GeneralInfo', None)
        info = {key: (text(general, key) if general is not None else None)
                for key in ('UnitForce', 'UnitLength', 'Version', 'Title')}
        info['Dimension'] = '3D' if self.is3d else '2D'
        return info

    def object_row(self, i, piles=None):
        kind, obj, _ = self.objects[i]
        row = {'id': i, 'kind': kind, 'name': name_of(obj)}
        if piles and i in piles:
            row['pile'] = piles[i]
        return row

    def catalog(self):
        return {
            'info': self.info(),
            'objects': [{'id': i, 'kind': kind, 'name': name_of(obj)} for i, (kind, obj, _) in enumerate(self.objects)],
            'phases': [{'id': i, 'name': name_of(p), 'title': text(p, 'Identification')} for i, p in enumerate(self.phases)],
            'result_types': self.result_names,
        }

    def extract(self, object_ids, phase_ids, skip_inactive=False, piles=None):
        if not object_ids or not phase_ids:
            raise ExportError('Не выбраны элементы или фазы')
        for ids, items, what in ((object_ids, self.objects, 'элемента'), (phase_ids, self.phases, 'фазы')):
            if len(set(ids)) != len(ids) or any(type(i) is not int or not 0 <= i < len(items) for i in ids):
                raise ExportError('Неверный номер ' + what)
        data = []
        for pi in phase_ids:
            phase = self.phases[pi]
            for oi in object_ids:
                kind, obj, types = self.objects[oi]
                log(f'{name_of(phase)}: {name_of(obj)}')
                arrays = {}
                try:
                    for key, rtype in types.items():
                        arrays[key] = [float(v) for v in self.g.getresults(obj, phase, rtype, 'node')]
                except Exception as exc:
                    # Not active in this phase: Output has no results for it.
                    if skip_inactive:
                        continue
                    raise ExportError(f'{name_of(obj)}, {name_of(phase)}: нет результата ({exc})')
                if skip_inactive and not arrays['x']:
                    continue
                sizes = {len(a) for a in arrays.values()}
                if len(sizes) != 1 or not arrays['x']:
                    raise ExportError(f'{name_of(obj)}, {name_of(phase)}: PLAXIS вернул массивы разной длины или пустые')
                nodes = [[arrays[k][i] for k in self.columns] for i in range(len(arrays['x']))]
                if not all(math.isfinite(v) for row in nodes for v in row):
                    raise ExportError(f'{name_of(obj)}, {name_of(phase)}: неконечные значения')
                data.append({'object': oi, 'phase': pi, 'nodes': nodes})
        return {
            'info': self.info(),
            'objects': [self.object_row(i, piles) for i in object_ids],
            'phases': [{'id': i, 'name': name_of(self.phases[i]), 'title': text(self.phases[i], 'Identification')} for i in phase_ids],
            'columns': self.columns,
            'data': data,
        }


def from_input(g_i, connector, output_port=None, per_phase=False):
    """Selected structures of Input, results of all non-Safety phases read from Output."""
    selected = []
    try:
        items = list(g_i.selection)
    except Exception:
        items = []
    for obj in items:
        found = feature_of(obj)
        if found and found not in selected:
            selected.append(found)
    if not selected:
        raise ExportError('В PLAXIS Input не выделены Plate, Embedded beam (row) или Beam. '
                          'Выделите стену или сваю (можно несколько участков одной вертикали; в 3D — группу свай) и повторите.')
    if len({kind for kind, _ in selected}) != 1:
        raise ExportError('Выделены элементы разных типов: выберите только Plate, только Embedded beam (row) или только Beam')
    log('Input: выделено ' + ', '.join(name for _, name in selected))
    phases = list(g_i.Phases)
    rows = [PhaseRow(p) for p in phases]
    used = [p for p, r in zip(phases, rows) if not r.safety]
    log(f'Input: фаз {len(phases)}, из них Safety {len(phases) - len(used)}; дерево: '
        + ', '.join(f'{r.previous or "—"}→{r.name}' + (' (Safety)' if r.safety else '') for r in rows))
    log('Input: типы расчёта: ' + '; '.join(f'{r.name} {r.raw or "?"}' for r in rows))
    if not used:
        raise ExportError('В проекте нет фаз, кроме Safety')
    if output_port is None:
        log(f'Input: открытие Output на фазе {name_of(used[-1])}')
        try:
            output_port = int(g_i.view(used[-1]))
        except Exception as exc:
            raise ExportError(f'Input не открыл Output ({exc}). Проект должен быть рассчитан и сохранён; '
                              'либо откройте Output вручную и укажите его порт.')
    log(f'Output: подключение к порту {output_port}')
    output = Output(connector(output_port))
    log(f'Output: {"3D" if output.is3d else "2D"}, конструкций {len(output.objects)}, фаз {len(output.phases)}')
    envelope = None if per_phase else envelope_from_input(output, rows, selected)
    if envelope is not None:
        return envelope
    names = [name for _, name in selected]
    kind, resolved = resolve(output, selected)
    object_ids = [o[0] for o, _ in resolved]
    piles = {o[0]: pile for o, pile in resolved if pile is not None}
    wanted = {r.name for r in rows if not r.safety}
    phase_ids = sorted(i for i, _ in output_phase_names(output, wanted).values())
    log(f'Чтение по фазам: элементов {len(object_ids)}, фаз {len(phase_ids)}')
    result = output.extract(object_ids, phase_ids, skip_inactive=True, piles=piles)
    with_data = {d['phase'] for d in result['data']}
    if kind == 'Plate' and with_data:
        result['moment_sign'] = moment_sign(output, [output.objects[i] for i in object_ids], output.phases[max(with_data)])
    types = {r.name: r.calc_type for r in rows}
    for ph in result['phases']:
        ph['calc_type'] = types.get(ph['name'])
    result['phases'] = [ph for ph in result['phases'] if ph['id'] in with_data]
    result['selection'] = names
    result['excluded_phases'] = [r.info() for r in rows if r.safety]
    result['inactive_phases'] = [name_of(output.phases[i]) for i in phase_ids if i not in with_data]
    if not result['data']:
        raise ExportError('Выбранные элементы не активны ни в одной рассчитанной фазе')
    return result


def envelope_from_input(output, rows, selected):
    """
    Envelopes computed by Output: one per branch, read at its last non-Safety phase. An envelope covers the phase
    and all phases before it in its branch, so a branch with a Safety phase inside falls back to reading phases.
    Returns None for the per-phase route.
    """
    kind, resolved = resolve(output, selected)
    group, _ = first(output.g.ResultTypes, dict(KINDS_BY_NAME)[kind])
    types = envelope_types(group, output.results) if group is not None else None
    if types is None:
        log('Огибающих в Output нет — чтение по фазам')
        return None
    log('Огибающие: ' + ', '.join(n for _, n in types.values()))
    branches = branches_of(rows)
    if branches is None:
        return None
    log('Ветки фаз: ' + '; '.join(f'{leaf.name} ({len(chain)} фаз)' for leaf, chain in branches))
    names = [name for _, name in selected]
    objects = [o for o, _ in resolved]
    piles = {o[0]: pile for o, pile in resolved if pile is not None}
    columns = output.envelope_columns
    out_phases = output_phase_names(output, {leaf.name for leaf, _ in branches})
    data, phase_rows, inactive = [], [], []
    for leaf, covered in branches:
        if leaf.name not in out_phases:
            log(f'Фаза {leaf.name} не найдена в Output')
            continue
        pi, phase = out_phases[leaf.name]
        got = False
        for k, (oi, obj, t) in enumerate(objects):
            log(f'Огибающая {leaf.name}: элемент Output №{oi} ({k + 1} из {len(objects)})')
            started = time.time()
            try:
                arrays = {c: output.g.getresults(obj, phase, t[c], 'node') for c in COORDS if c in t}
                for key, (rtype, _) in types.items():
                    arrays[key] = output.g.getresults(obj, phase, rtype, 'node')
            except Exception as exc:
                log(f'  нет результата ({exc})')
                continue
            arrays = {k: [float(v) for v in a] for k, a in arrays.items()}
            # Extremes per element and branch: to compare with Output.
            span = ', '.join(f'{key} {min(arrays[key + "_min"]):.0f}…{max(arrays[key + "_max"]):.0f}'
                             for key in output.results if key not in COORDS and key + '_min' in arrays and arrays[key + '_min'])
            zs = arrays.get('z') or arrays.get('y') or [0.0]
            log(f'  {name_of(obj)}: {len(arrays["x"])} узлов, отм. {max(zs):.2f}…{min(zs):.2f}, {span} ({time.time() - started:.1f} с)')
            if len({len(a) for a in arrays.values()}) != 1 or not arrays['x']:
                raise ExportError(f'{name_of(obj)}, {name_of(phase)}: PLAXIS вернул огибающие разной длины')
            nodes = [[arrays[c][i] for c in columns] for i in range(len(arrays['x']))]
            if not all(math.isfinite(v) for row in nodes for v in row):
                raise ExportError(f'{name_of(obj)}, {name_of(phase)}: неконечные значения огибающей')
            data.append({'object': oi, 'phase': pi, 'nodes': nodes})
            got = True
        if got:
            phase_rows.append({'id': pi, 'name': leaf.name, 'title': leaf.title, 'calc_type': leaf.calc_type, 'covers': covered})
        else:
            inactive.append(name_of(phase))
    if not data:
        raise ExportError('Выбранные элементы не активны в конечных фазах расчёта')
    sign = moment_sign(output, objects, out_phases[phase_rows[0]['name']][1]) if kind == 'Plate' else None
    return {
        'moment_sign': sign,
        'info': output.info(),
        'mode': 'envelope',
        'objects': [output.object_row(i, piles) for i, _, _ in objects],
        'phases': phase_rows,
        'columns': columns,
        'data': data,
        'selection': names,
        'result_types': {k: n for k, (_, n) in types.items()},
        'excluded_phases': [r.info() for r in rows if r.safety],
        'inactive_phases': inactive,
    }


KINDS_BY_NAME = [(kind, groups) for kind, _, groups in KINDS + KINDS3D]


def connect(port, program='Output'):
    try:
        from plxscripting.easy import new_server
    except ImportError:
        raise ExportError('Не найден модуль plxscripting: укажите python.exe из каталога установки PLAXIS')
    password = os.environ.get('PLAXIS_PASSWORD', '')
    log(f'{program}: подключение к порту {port}')
    try:
        _, g = new_server('localhost', port, password=password)
        list(g.Phases)
    except Exception as exc:
        raise ExportError(f'Нет связи с PLAXIS {program} на порту {port}: {exc}. '
                          f'В {program}: Expert → Configure remote scripting server — запустите сервер с этим портом и паролем.')
    return g


def main(argv=None, connector=connect):
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=('input', 'catalog', 'extract'))
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--output-port', type=int)
    parser.add_argument('--request')
    parser.add_argument('--per-phase', action='store_true', help='read every phase instead of the Output envelopes')
    args = parser.parse_args(argv)
    try:
        if args.command == 'input':
            json.dump(from_input(connector(args.port, 'Input'), connector, args.output_port, args.per_phase), sys.stdout)
            return 0
        output = Output(connector(args.port))
        if args.command == 'catalog':
            result = output.catalog()
        else:
            with open(args.request, encoding='utf-8') as f:
                request = json.load(f)
            result = output.extract(request.get('objects'), request.get('phases'))
        json.dump(result, sys.stdout)  # ASCII: independent of the Windows console code page
        return 0
    except (ExportError, TimeoutError) as exc:
        json.dump({"error": str(exc)}, sys.stdout)
        return 1


if __name__ == '__main__':
    # A stuck PLAXIS call: the Python stack goes to the log every 20 s, so that the step is known.
    try:
        faulthandler.dump_traceback_later(20, repeat=True, file=sys.stderr)
    except (AttributeError, ValueError, OSError):
        pass
    sys.exit(main())
