"""Read-only export of PLAXIS 2D Output results for the section application.

Run with the Python that ships with PLAXIS (it contains plxscripting):
    python plaxis_export.py catalog --port 10001
    python plaxis_export.py extract --port 10001 --request request.json
The password of the Output remote scripting server is taken from the
environment variable PLAXIS_PASSWORD. The result is JSON on stdout; errors are
JSON {"error": "..."} with exit code 1. Values are returned in the project
units of PLAXIS; the application converts them.
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

    def extract(self, object_ids, phase_ids):
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
                for key, rtype in types.items():
                    try:
                        arrays[key] = [float(v) for v in self.g.getresults(obj, phase, rtype, 'node')]
                    except Exception as exc:
                        raise ExportError(f'{name_of(obj)}, {name_of(phase)}: нет результата {key.upper()} ({exc})')
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


def connect(port):
    try:
        from plxscripting.easy import new_server
    except ImportError:
        raise ExportError('Не найден модуль plxscripting: укажите python.exe из каталога установки PLAXIS')
    password = os.environ.get('PLAXIS_PASSWORD', '')
    try:
        _, g = new_server('localhost', port, password=password)
        list(g.Phases)
    except Exception as exc:
        raise ExportError(f'Нет связи с PLAXIS Output на порту {port}: {exc}. '
                          'Откройте результаты в Output и включите Expert → Configure remote scripting server.')
    return g


def main(argv=None, connector=connect):
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=('catalog', 'extract'))
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--request')
    args = parser.parse_args(argv)
    try:
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
