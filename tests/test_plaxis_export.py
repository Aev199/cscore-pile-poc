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
        code = plaxis_export.main(args, connector=lambda port, program='Output': g)
    return code, json.loads(out.getvalue())


class FakeInput:
    """PLAXIS 2D V22 Input: selection, phases with calculation types and view() returning the Output port."""

    def __init__(self, selection):
        self.selection = selection
        self.Phases = [SimpleNamespace(Name=value('InitialPhase'), Identification=value('Начальная'), DeformCalcType=value('K0 procedure')),
                       SimpleNamespace(Name=value('Phase_1'), Identification=value('Выемка'), DeformCalcType=value('Plastic')),
                       SimpleNamespace(Name=value('Phase_2'), Identification=value('Выемка'), DeformCalcType=value('Plastic')),
                       SimpleNamespace(Name=value('Phase_3'), Identification=value('Запас'), DeformCalcType=value('Safety'))]
        self.viewed = None

    def view(self, phase):
        self.viewed = phase
        return 10002


class FakeOutputSplit(FakeOutput):
    """Output after meshing: Plate_1 is split into Plate_1_1 and Plate_1_2; Plate_2 is another wall."""

    def __init__(self):
        super().__init__()
        self.plate = SimpleNamespace(Name=value('Plate_1_1'))
        self.plate2 = SimpleNamespace(Name=value('Plate_1_2'))
        self.other = SimpleNamespace(Name=value('Plate_2_1'))
        self.Plates = [self.plate, self.plate2, self.other]
        self.Phases = [SimpleNamespace(Name=value(n), Identification=value(n)) for n in ('InitialPhase', 'Phase_1', 'Phase_2', 'Phase_3')]
        self.requested = []

    def getresults(self, obj, phase, rtype, location):
        if name_of_phase(phase) == 'InitialPhase':
            raise RuntimeError('Object is not active in this phase')
        self.requested.append((obj.Name.value, phase.Name.value))
        y = [0.0, -1.0, -2.0] if obj is self.plate else [-2.0, -3.0, -4.0]
        return {'X': [5.0] * 3, 'Y': y, 'Nx2D': [-10.0] * 3, 'M2D': [1.0, 2.0, 3.0], 'Q2D': [0.5] * 3}[rtype]


def name_of_phase(phase):
    return phase.Name.value


def run_input(selection, output):
    g_i = FakeInput(selection)
    connections = {10000: g_i, 10002: output}
    out = io.StringIO()
    with redirect_stdout(out):
        code = plaxis_export.main(['input', '--port', '10000'], connector=lambda port, program='Output': connections[port])
    return code, json.loads(out.getvalue()), g_i


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


class InputTests(unittest.TestCase):
    def test_selected_plate_is_read_from_output_without_safety_phases(self):
        output = FakeOutputSplit()
        code, result, g_i = run_input([SimpleNamespace(Name=value('Plate_1'), _plx_type='Plate')], output)
        self.assertEqual(code, 0, result)
        self.assertEqual(result['selection'], ['Plate_1'])
        # Both mesh segments of Plate_1, not Plate_2.
        self.assertEqual([o['name'] for o in result['objects']], ['Plate_1_1', 'Plate_1_2'])
        self.assertEqual([p['name'] for p in result['phases']], ['Phase_1', 'Phase_2'])
        self.assertEqual(result['phases'][0]['calc_type'], 'Plastic')
        self.assertEqual([p['name'] for p in result['excluded_phases']], ['Phase_3'])
        self.assertEqual(result['inactive_phases'], ['InitialPhase'])
        self.assertNotIn('Phase_3', {ph for _, ph in output.requested})
        # Output is opened on the last non-Safety phase.
        self.assertEqual(g_i.viewed.Name.value, 'Phase_2')
        self.assertEqual(len(result['data']), 4)

    def test_selected_line_resolves_to_its_plate(self):
        line = SimpleNamespace(Name=value('Line_1'), _plx_type='Line', Plate=SimpleNamespace(Name=value('Plate_1')))
        code, result, _ = run_input([line], FakeOutputSplit())
        self.assertEqual(code, 0, result)
        self.assertEqual(result['selection'], ['Plate_1'])

    def test_empty_or_mixed_selection_is_explained(self):
        code, result, _ = run_input([], FakeOutputSplit())
        self.assertEqual(code, 1)
        self.assertIn('не выделены', result['error'])
        mixed = [SimpleNamespace(Name=value('Plate_1'), _plx_type='Plate'), SimpleNamespace(Name=value('EmbeddedBeamRow_1'), _plx_type='EmbeddedBeamRow')]
        code, result, _ = run_input(mixed, FakeOutputSplit())
        self.assertEqual(code, 1)
        self.assertIn('разных типов', result['error'])


class FakeOutputEnvelope(FakeOutputSplit):
    """Output with envelope result types of plates."""

    def __init__(self):
        super().__init__()
        names = dict(X='X', Y='Y', Nx2D='Nx2D', M2D='M2D', Q2D='Q2D')
        for base in ('Nx2D', 'M2D', 'Q2D'):
            for bound in ('Max', 'Min'):
                names[f'{base}_Envelope{bound}'] = f'{base}_Envelope{bound}'
        self.ResultTypes = SimpleNamespace(Plate=SimpleNamespace(**names), EmbeddedBeamRow=SimpleNamespace(**names))

    def getresults(self, obj, phase, rtype, location):
        self.requested.append((obj.Name.value, phase.Name.value, rtype))
        if 'Envelope' not in rtype and rtype not in ('X', 'Y'):
            raise AssertionError('per-phase result requested in the envelope route')
        y = [0.0, -1.0, -2.0] if obj is self.plate else [-2.0, -3.0, -4.0]
        sign = 1 if rtype.endswith('Max') else -1
        return {'X': [5.0] * 3, 'Y': y}.get(rtype, [sign * 10.0, sign * 20.0, sign * 30.0])


def tree_input(selection):
    """Phases: Initial → Phase_1 → Phase_2 → Safety_A; Phase_1 → Phase_3 (second branch)."""
    g_i = FakeInput(selection)
    initial = SimpleNamespace(Name=value('InitialPhase'), Identification=value('Начальная'), DeformCalcType=value('K0 procedure'), PreviousPhase=None)
    p1 = SimpleNamespace(Name=value('Phase_1'), Identification=value('Выемка'), DeformCalcType=value('Plastic'), PreviousPhase=initial)
    p2 = SimpleNamespace(Name=value('Phase_2'), Identification=value('Дно'), DeformCalcType=value('Plastic'), PreviousPhase=p1)
    fs = SimpleNamespace(Name=value('Phase_3'), Identification=value('Запас'), DeformCalcType=value('Safety'), PreviousPhase=p2)
    p4 = SimpleNamespace(Name=value('Phase_4'), Identification=value('Сейсмика'), DeformCalcType=value('Dynamic'), PreviousPhase=p1)
    g_i.Phases = [initial, p1, p2, fs, p4]
    return g_i


class ParentOnlyOutput(FakeOutputEnvelope):
    """Output that lists only the parent Plate_1 (the tree shows its segments Plate_1_1…)."""

    def __init__(self):
        super().__init__()
        self.plate = SimpleNamespace(Name=value('Plate_1'))
        self.Plates = [self.plate, SimpleNamespace(Name=value('Plate_2'))]


class SegmentTests(unittest.TestCase):
    def test_selected_segments_are_read_through_their_parent(self):
        output = ParentOnlyOutput()
        segments = [SimpleNamespace(Name=value(f'Plate_1_{i}'), _plx_type='Plate') for i in range(1, 36)]
        code, result, _ = run_input(segments, output)
        self.assertEqual(code, 0, result)
        self.assertEqual([o['name'] for o in result['objects']], ['Plate_1'])
        self.assertEqual(len(result['selection']), 35)

    def test_exact_segments_win_over_parent(self):
        output = FakeOutputEnvelope()
        output.Plates.insert(0, SimpleNamespace(Name=value('Plate_1')))
        code, result, _ = run_input([SimpleNamespace(Name=value('Plate_1_1'), _plx_type='Plate')], output)
        self.assertEqual(code, 0, result)
        self.assertEqual([o['name'] for o in result['objects']], ['Plate_1_1'])

    def test_parent_and_segments_are_not_read_twice(self):
        output = FakeOutputEnvelope()
        output.Plates.insert(0, SimpleNamespace(Name=value('Plate_1')))
        code, result, _ = run_input([SimpleNamespace(Name=value('Plate_1'), _plx_type='Plate')], output)
        self.assertEqual(code, 0, result)
        self.assertEqual([o['name'] for o in result['objects']], ['Plate_1'])

    def test_missing_selection_lists_what_output_has(self):
        segments = [SimpleNamespace(Name=value('Plate_7_1'), _plx_type='Plate')]
        code, result, _ = run_input(segments, ParentOnlyOutput())
        self.assertEqual(code, 1)
        self.assertIn('Output видит (Plate): Plate_1, Plate_2', result['error'])


class EnvelopeTests(unittest.TestCase):
    def run_tree(self, output, g_i):
        out = io.StringIO()
        with redirect_stdout(out):
            code = plaxis_export.main(['input', '--port', '10000'], connector=lambda port, program='Output': {10000: g_i, 10002: output}[port])
        return code, json.loads(out.getvalue())

    def test_envelope_per_branch_without_safety(self):
        output = FakeOutputEnvelope()
        output.Phases.append(SimpleNamespace(Name=value('Phase_4'), Identification=value('Phase_4')))
        g_i = tree_input([SimpleNamespace(Name=value('Plate_1'), _plx_type='Plate')])
        code, result = self.run_tree(output, g_i)
        self.assertEqual(code, 0, result)
        self.assertEqual(result['mode'], 'envelope')
        self.assertEqual(result['columns'], plaxis_export.ENVELOPE_COLUMNS)
        # Two branches: Phase_2 (Safety is its child, not part of it) and Phase_4.
        self.assertEqual([p['name'] for p in result['phases']], ['Phase_2', 'Phase_4'])
        self.assertEqual(result['phases'][0]['covers'], ['InitialPhase', 'Phase_1', 'Phase_2'])
        self.assertEqual(result['phases'][1]['covers'], ['InitialPhase', 'Phase_1', 'Phase_4'])
        self.assertEqual(result['result_types']['m_max'], 'M2D_EnvelopeMax')
        self.assertEqual(result['data'][0]['nodes'][1], [5.0, -1.0, -20.0, 20.0, -20.0, 20.0, -20.0, 20.0])
        # Only the leaves are read: 2 branches × 2 segments.
        self.assertEqual(len(result['data']), 4)
        self.assertNotIn('Phase_3', {r[1] for r in output.requested})
        self.assertNotIn('Phase_1', {r[1] for r in output.requested})

    def test_safety_inside_a_branch_falls_back_to_phases(self):
        output = FakeOutputEnvelope()
        g_i = tree_input([SimpleNamespace(Name=value('Plate_1'), _plx_type='Plate')])
        # A plastic phase continues after the Safety phase: its envelope would include the Safety results.
        g_i.Phases[4].PreviousPhase = g_i.Phases[3]
        output.getresults = FakeOutputSplit.getresults.__get__(output)
        code, result = self.run_tree(output, g_i)
        self.assertEqual(code, 0, result)
        self.assertNotIn('mode', result)
        self.assertEqual(result['columns'], ['x', 'y', 'n', 'm', 'q'])

    def test_no_envelope_types_falls_back_to_phases(self):
        code, result, _ = run_input([SimpleNamespace(Name=value('Plate_1'), _plx_type='Plate')], FakeOutputSplit())
        self.assertEqual(code, 0, result)
        self.assertEqual(result['columns'], ['x', 'y', 'n', 'm', 'q'])


if __name__ == '__main__':
    unittest.main()
