"""Read-only export of PLAXIS 2D results for the section application.

Run with the Python that ships with PLAXIS (it contains plxscripting):
    python plaxis_export.py input --port 10000 [--output-port 10001]
        the plates / embedded beam rows selected in Input, results of every
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
import json
import math
import os
import sys

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


class ExportError(Exception):
    pass


def text(obj, key):
    try:
        return str(getattr(obj, key).value)
    except Exception:
        return None


def name_of(obj):
    return text(obj, 'Name') or text(obj, 'Identification') or str(obj)


def first(obj, names):
    for name in names:
        try:
            return getattr(obj, name), name
        except Exception:
            pass
    return None, None


def calc_type(phase):
    for key in ('DeformCalcType', 'CalculationType', 'CalcType'):
        value = text(phase, key)
        if value:
            return value
    return None


def is_safety(phase):
    kind = calc_type(phase)
    return kind is not None and 'safety' in kind.lower()


PREFIXES = (('Plate', ('Plate',)), ('EmbeddedBeamRow', ('EmbeddedBeamRow', 'EmbeddedBeam')))


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


ENVELOPE_COLUMNS = ['x', 'y', 'n_min', 'n_max', 'm_min', 'm_max', 'q_min', 'q_max']


def envelope_types(group):
    """Envelope result types of a structure group: {'n_min': (type, name), ...}, or None if Output has none."""
    try:
        listed = [n for n in dir(group) if 'envelope' in n.lower()]
    except Exception:
        listed = []
    found = {}
    for key in ('n', 'm', 'q'):
        for bound in ('Max', 'Min'):
            hit = None
            for base in RESULTS[key]:
                guesses = [n for n in listed if n.lower().startswith(base.lower()) and bound.lower() in n.lower()]
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
        if prev is not None and prev is not phase:
            return prev
    return None


def matches(output_name, input_name):
    # Staged construction splits a structure: Plate_1 becomes Plate_1_1, Plate_1_2, ... Output may list
    # the segments or only their parent Plate_1; either side may be the selected one.
    return (output_name == input_name or output_name.startswith(input_name + '_')
            or input_name.startswith(output_name + '_'))


def moment_sign(output, objects, phase):
    """
    Which face positive M stretches, from the deflected shape: x = x0 + Ux(y); where Ux'' > 0 the wall bends
    towards +x and its left (smaller x) face is stretched. +1: M > 0 stretches the right face (larger x).
    Returns {'sign', 'agreement' 0…1, 'phase'} or None.
    """
    group, _ = first(output.g.ResultTypes, ('Plate',))
    ux_type, _ = first(group, ('Ux',)) if group is not None else (None, None)
    if ux_type is None:
        return None
    rows = {}
    for _, obj, types in objects:
        try:
            ys = output.g.getresults(obj, phase, types['y'], 'node')
            us = output.g.getresults(obj, phase, ux_type, 'node')
            ms = output.g.getresults(obj, phase, types['m'], 'node')
        except Exception:
            continue
        for y, u, m in zip(ys, us, ms):
            rows.setdefault(round(float(y), 4), []).append((float(u), float(m)))
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
    return {'sign': 1 if score > 0 else -1, 'agreement': round(abs(score) / total, 3), 'phase': name_of(phase)}


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


class Output:
    def __init__(self, g):
        self.g = g
        self.objects = []          # (kind, object, {key: result type})
        self.result_names = {}     # kind -> {key: name}
        for kind, collections, groups in KINDS:
            items, _ = first(g, collections)
            group, _ = first(g.ResultTypes, groups)
            if items is None or group is None:
                continue
            types, names = {}, {}
            for key, candidates in RESULTS.items():
                types[key], names[key] = first(group, candidates)
            if any(t is None for t in types.values()):
                continue
            self.result_names[kind] = names
            for obj in items:
                self.objects.append((kind, obj, types))
        self.phases = list(g.Phases)
        if not self.objects:
            raise ExportError('В модели нет Plate или Embedded Beam Row с результатами X, Y, N, M, Q')

    def info(self):
        general = getattr(self.g, 'GeneralInfo', None)
        return {key: (text(general, key) if general is not None else None)
                for key in ('UnitForce', 'UnitLength', 'Version', 'Title')}

    def catalog(self):
        return {
            'info': self.info(),
            'objects': [{'id': i, 'kind': kind, 'name': name_of(obj)} for i, (kind, obj, _) in enumerate(self.objects)],
            'phases': [{'id': i, 'name': name_of(p), 'title': text(p, 'Identification')} for i, p in enumerate(self.phases)],
            'result_types': self.result_names,
        }

    def extract(self, object_ids, phase_ids, skip_inactive=False):
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
                nodes = [[arrays[k][i] for k in ('x', 'y', 'n', 'm', 'q')] for i in range(len(arrays['x']))]
                if not all(math.isfinite(v) for row in nodes for v in row):
                    raise ExportError(f'{name_of(obj)}, {name_of(phase)}: неконечные значения')
                data.append({'object': oi, 'phase': pi, 'nodes': nodes})
        return {
            'info': self.info(),
            'objects': [{'id': i, 'kind': self.objects[i][0], 'name': name_of(self.objects[i][1])} for i in object_ids],
            'phases': [{'id': i, 'name': name_of(self.phases[i]), 'title': text(self.phases[i], 'Identification')} for i in phase_ids],
            'columns': ['x', 'y', 'n', 'm', 'q'],
            'data': data,
        }


def from_input(g_i, connector, output_port=None):
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
        raise ExportError('В PLAXIS Input не выделены Plate или Embedded beam row. '
                          'Выделите сваю или стену (можно несколько участков одной вертикали) и повторите.')
    if len({kind for kind, _ in selected}) != 1:
        raise ExportError('Выделены элементы разных типов: выберите только Plate или только Embedded beam row')
    phases = list(g_i.Phases)
    used = [p for p in phases if not is_safety(p)]
    if not used:
        raise ExportError('В проекте нет фаз, кроме Safety')
    if output_port is None:
        try:
            output_port = int(g_i.view(used[-1]))
        except Exception as exc:
            raise ExportError(f'Input не открыл Output ({exc}). Проект должен быть рассчитан и сохранён; '
                              'либо откройте Output вручную и укажите его порт.')
    output = Output(connector(output_port))
    envelope = envelope_from_input(output, phases, used, selected)
    if envelope is not None:
        return envelope
    names = [name for _, name in selected]
    object_ids = [i for i, _, _ in select_objects(output, selected[0][0], names)]
    wanted = {name_of(p) for p in used}
    phase_ids = [i for i, p in enumerate(output.phases) if name_of(p) in wanted and not is_safety(p)]
    result = output.extract(object_ids, phase_ids, skip_inactive=True)
    with_data = {d['phase'] for d in result['data']}
    if selected[0][0] == 'Plate' and with_data:
        result['moment_sign'] = moment_sign(output, [output.objects[i] for i in object_ids], output.phases[max(with_data)])
    types = {name_of(p): calc_type(p) for p in phases}
    for ph in result['phases']:
        ph['calc_type'] = types.get(ph['name'])
    result['phases'] = [ph for ph in result['phases'] if ph['id'] in with_data]
    result['selection'] = names
    result['excluded_phases'] = [{'name': name_of(p), 'title': text(p, 'Identification'), 'calc_type': calc_type(p)}
                                 for p in phases if is_safety(p)]
    result['inactive_phases'] = [name_of(output.phases[i]) for i in phase_ids if i not in with_data]
    if not result['data']:
        raise ExportError('Выбранные элементы не активны ни в одной рассчитанной фазе')
    return result


def envelope_from_input(output, phases, used, selected):
    """
    Envelopes computed by Output: one per branch, read at its last non-Safety phase. An envelope covers the phase
    and all phases before it in its branch, so a branch with a Safety phase inside falls back to reading phases.
    Returns None for the per-phase route.
    """
    kind = selected[0][0]
    group, _ = first(output.g.ResultTypes, dict(KINDS_BY_NAME)[kind])
    types = envelope_types(group) if group is not None else None
    if types is None:
        return None
    by_name = {name_of(p): p for p in phases}
    used_names = {name_of(p) for p in used}
    parents = {name_of(previous(p)) for p in used if previous(p) is not None}
    leaves = [p for p in used if name_of(p) not in parents]
    branches = []
    for leaf in leaves:
        chain, cur = [], leaf
        while cur is not None:
            chain.append(cur)
            cur = previous(cur)
            if cur is not None and name_of(cur) not in by_name:
                break
        if any(is_safety(p) for p in chain):
            return None
        branches.append((leaf, [name_of(p) for p in reversed(chain) if name_of(p) in used_names]))
    names = [name for _, name in selected]
    objects = select_objects(output, kind, names)
    out_phases = {name_of(p): (i, p) for i, p in enumerate(output.phases)}
    data, phase_rows, inactive = [], [], []
    for leaf, covered in branches:
        if name_of(leaf) not in out_phases:
            continue
        pi, phase = out_phases[name_of(leaf)]
        got = False
        for oi, obj, t in objects:
            try:
                arrays = {'x': output.g.getresults(obj, phase, t['x'], 'node'), 'y': output.g.getresults(obj, phase, t['y'], 'node')}
                for key, (rtype, _) in types.items():
                    arrays[key] = output.g.getresults(obj, phase, rtype, 'node')
            except Exception:
                continue
            arrays = {k: [float(v) for v in a] for k, a in arrays.items()}
            if len({len(a) for a in arrays.values()}) != 1 or not arrays['x']:
                raise ExportError(f'{name_of(obj)}, {name_of(phase)}: PLAXIS вернул огибающие разной длины')
            nodes = [[arrays[c][i] for c in ENVELOPE_COLUMNS] for i in range(len(arrays['x']))]
            if not all(math.isfinite(v) for row in nodes for v in row):
                raise ExportError(f'{name_of(obj)}, {name_of(phase)}: неконечные значения огибающей')
            data.append({'object': oi, 'phase': pi, 'nodes': nodes})
            got = True
        if got:
            phase_rows.append({'id': pi, 'name': name_of(phase), 'title': text(phase, 'Identification'),
                               'calc_type': calc_type(by_name[name_of(leaf)]), 'covers': covered})
        else:
            inactive.append(name_of(phase))
    if not data:
        raise ExportError('Выбранные элементы не активны в конечных фазах расчёта')
    sign = moment_sign(output, objects, out_phases[phase_rows[0]['name']][1]) if kind == 'Plate' else None
    return {
        'moment_sign': sign,
        'info': output.info(),
        'mode': 'envelope',
        'objects': [{'id': i, 'kind': kind, 'name': name_of(obj)} for i, obj, _ in objects],
        'phases': phase_rows,
        'columns': ENVELOPE_COLUMNS,
        'data': data,
        'selection': names,
        'result_types': {k: n for k, (_, n) in types.items()},
        'excluded_phases': [{'name': name_of(p), 'title': text(p, 'Identification'), 'calc_type': calc_type(p)}
                            for p in phases if is_safety(p)],
        'inactive_phases': inactive,
    }


KINDS_BY_NAME = [(kind, groups) for kind, _, groups in KINDS]


def connect(port, program='Output'):
    try:
        from plxscripting.easy import new_server
    except ImportError:
        raise ExportError('Не найден модуль plxscripting: укажите python.exe из каталога установки PLAXIS')
    password = os.environ.get('PLAXIS_PASSWORD', '')
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
    args = parser.parse_args(argv)
    try:
        if args.command == 'input':
            json.dump(from_input(connector(args.port, 'Input'), connector, args.output_port), sys.stdout)
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
    except ExportError as exc:
        json.dump({"error": str(exc)}, sys.stdout)
        return 1


if __name__ == '__main__':
    sys.exit(main())
