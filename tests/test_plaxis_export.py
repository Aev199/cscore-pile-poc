import io
import json
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from types import SimpleNamespace

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'src', 'SectionCalc', 'Plaxis'))
import plaxis_export  # noqa: E402


def value(v):
    return SimpleNamespace(value=v)


class FakeOutput:
    """PLAXIS 2D V22 Output object model, as far as the exporter uses it."""

    def __init__(self, embedded_names=('EmbeddedBeamRows',), n_name='Nx2D'):
        self.plate = SimpleNamespace(Name=value('Plate_1'))
        self.ebr = SimpleNamespace(Name=value('EmbeddedBeamRow_1'))
        self.Plates = [self.plate]
        for name in embedded_names:
            setattr(self, name, [self.ebr])
        self.Phases = [SimpleNamespace(Name=value('Phase_1'), Identification=value('Выемка')),
                       SimpleNamespace(Name=value('Phase_2'), Identification=value('Выемка'))]
        group = lambda: SimpleNamespace(X='X', Y='Y', M2D='M2D', Q2D='Q2D', **{n_name: n_name})
        self.ResultTypes = SimpleNamespace(Plate=group(), EmbeddedBeamRow=group())
        self.GeneralInfo = SimpleNamespace(UnitForce=value('kN'), UnitLength=value('m'), Version=value('22.01'), Title=value('Котлован'))

    def getresults(self, obj, phase, rtype, location):
        assert location == 'node'
        k = 1 if phase is self.Phases[0] else 2
        return {'X': [5.0, 5.0, 5.0], 'Y': [0.0, -1.0, -2.0], 'Nx2D': [-10.0 * k, -20.0, -30.0], 'N2D': [-1.0, -2.0, -3.0],
                'M2D': [0.0, 15.0 * k, 5.0], 'Q2D': [3.0, 4.0, 5.0]}[rtype]


def run(args, g):
    out = io.StringIO()
    with redirect_stdout(out):
        code = plaxis_export.main(args, connector=lambda port: g)
    return code, json.loads(out.getvalue())


class ExporterTests(unittest.TestCase):
    def test_catalog_lists_plates_embedded_rows_phases_and_units(self):
        code, result = run(['catalog', '--port', '10001'], FakeOutput())
        self.assertEqual(code, 0)
        self.assertEqual([o['kind'] for o in result['objects']], ['Plate', 'EmbeddedBeamRow'])
        self.assertEqual(result['info']['UnitForce'], 'kN')
        self.assertEqual([p['name'] for p in result['phases']], ['Phase_1', 'Phase_2'])
        self.assertEqual(result['result_types']['Plate']['n'], 'Nx2D')

    def test_extract_returns_node_rows_per_object_and_phase(self):
        with tempfile.NamedTemporaryFile('w', suffix='.json', delete=False) as f:
            json.dump({'objects': [0], 'phases': [0, 1]}, f)
        try:
            code, result = run(['extract', '--port', '1', '--request', f.name], FakeOutput())
        finally:
            os.unlink(f.name)
        self.assertEqual(code, 0)
        self.assertEqual(result['columns'], ['x', 'y', 'n', 'm', 'q'])
        self.assertEqual(len(result['data']), 2)
        self.assertEqual(result['data'][1]['nodes'][1], [5.0, -1.0, -20.0, 30.0, 4.0])

    def test_older_result_names_are_accepted(self):
        code, result = run(['catalog', '--port', '1'], FakeOutput(embedded_names=('EmbeddedBeams',), n_name='N2D'))
        self.assertEqual(code, 0)
        self.assertEqual(result['result_types']['EmbeddedBeamRow']['n'], 'N2D')

    def test_errors_are_json_with_exit_code(self):
        with tempfile.NamedTemporaryFile('w', suffix='.json', delete=False) as f:
            json.dump({'objects': [0, 0], 'phases': [0]}, f)
        try:
            code, result = run(['extract', '--port', '1', '--request', f.name], FakeOutput())
        finally:
            os.unlink(f.name)
        self.assertEqual(code, 1)
        self.assertIn('Неверный номер', result['error'])

        def refuse(port):
            raise plaxis_export.ExportError('Нет связи с PLAXIS Output')
        out = io.StringIO()
        with redirect_stdout(out):
            self.assertEqual(plaxis_export.main(['catalog', '--port', '1'], connector=refuse), 1)
        self.assertIn('Нет связи', json.loads(out.getvalue())['error'])


if __name__ == '__main__':
    unittest.main()
