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


class BentWallOutput(FakeOutputEnvelope):
    """A cantilever bending towards +x: Ux = c·y² near the top → Ux'' > 0, the left face is stretched;
    M2D is negative there, so positive M stretches the right face."""

    def __init__(self, m_sign):
        super().__init__()
        self.m_sign = m_sign
        names = vars(self.ResultTypes.Plate).copy()
        names['Ux'] = 'Ux'
        self.ResultTypes = SimpleNamespace(Plate=SimpleNamespace(**names))

    def getresults(self, obj, phase, rtype, location):
        y = [-0.5 * i for i in range(9)] if obj is self.plate else [-4.0 - 0.5 * i for i in range(1, 9)]
        if rtype == 'Ux':
            return [0.001 * (v + 8) ** 2 for v in y]
        if rtype == 'M2D':
            return [self.m_sign * -10.0 for _ in y]
        if rtype == 'Y':
            return y
        if rtype == 'X':
            return [5.0] * len(y)
        return [1.0] * len(y)


class MomentSignTests(unittest.TestCase):
    def test_sign_follows_the_deflected_shape(self):
        for m_sign in (1, -1):
            out = io.StringIO()
            g_i = tree_input([SimpleNamespace(Name=value('Plate_1'), _plx_type='Plate')])
            output = BentWallOutput(m_sign)
            output.Phases.append(SimpleNamespace(Name=value('Phase_4'), Identification=value('Phase_4')))
            with redirect_stdout(out):
                code = plaxis_export.main(['input', '--port', '10000'], connector=lambda port, program='Output': {10000: g_i, 10002: output}[port])
            result = json.loads(out.getvalue())
            self.assertEqual(code, 0, result)
            self.assertEqual(result['moment_sign']['sign'], m_sign)
            self.assertGreater(result['moment_sign']['agreement'], 0.9)
            # The wall moves towards +x: the excavation is on the right.
            self.assertEqual(result['moment_sign']['excavation'], 'right')


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


class ProxyPhase:
    """plxscripting proxy: every attribute access returns a new object; InitialPhase.PreviousPhase is itself."""

    def __init__(self, name, previous, calc='Plastic'):
        self.Name, self.Identification, self.DeformCalcType = value(name), value(name), value(calc)
        self._previous = previous

    @property
    def PreviousPhase(self):
        prev = self._previous() if self._previous else ProxyPhase(self.Name.value, None, self.DeformCalcType.value)
        return ProxyPhase(prev.Name.value, prev._previous, prev.DeformCalcType.value)


class SelfReferenceTests(unittest.TestCase):
    def test_initial_phase_referring_to_itself_does_not_hang(self):
        initial = ProxyPhase('InitialPhase', None, 'K0 procedure')
        p1 = ProxyPhase('Phase_1', lambda: initial)
        p2 = ProxyPhase('Phase_2', lambda: p1)
        g_i = FakeInput([SimpleNamespace(Name=value('Plate_1'), _plx_type='Plate')])
        g_i.Phases = [initial, p1, p2]
        output = FakeOutputEnvelope()
        out = io.StringIO()
        with redirect_stdout(out):
            code = plaxis_export.main(['input', '--port', '10000'], connector=lambda port, program='Output': {10000: g_i, 10002: output}[port])
        result = json.loads(out.getvalue())
        self.assertEqual(code, 0, result)
        self.assertEqual(result['phases'][0]['covers'], ['InitialPhase', 'Phase_1', 'Phase_2'])

    def test_input_is_not_asked_after_it_opened_output(self):
        class BusyInput(FakeInput):
            def view(self, phase):
                self.viewed = phase
                for p in self.Phases:
                    p.__class__ = BusyPhase
                return 10002

        class BusyPhase(ProxyPhase):
            def __getattribute__(self, key):
                if not key.startswith('__') and key != '__class__':
                    raise AssertionError('Input asked after it opened Output: ' + key)
                return object.__getattribute__(self, key)

        initial = ProxyPhase('InitialPhase', None, 'K0 procedure')
        p1 = ProxyPhase('Phase_1', lambda: initial)
        g_i = BusyInput([SimpleNamespace(Name=value('Plate_1'), _plx_type='Plate')])
        g_i.Phases = [initial, p1]
        for per_phase in ([], ['--per-phase']):
            for p in g_i.Phases:
                p.__class__ = ProxyPhase
            output = FakeOutputEnvelope() if not per_phase else FakeOutputSplit()
            out = io.StringIO()
            with redirect_stdout(out):
                code = plaxis_export.main(['input', '--port', '10000', *per_phase],
                                          connector=lambda port, program='Output': {10000: g_i, 10002: output}[port])
            result = json.loads(out.getvalue())
            self.assertEqual(code, 0, result)


class SafetyDetectionTests(unittest.TestCase):
    def test_safety_in_other_forms(self):
        class Enum:
            @property
            def value(self):
                raise RuntimeError('no value')

            def __str__(self):
                return 'DeformCalcType.Safety'
        by_str = SimpleNamespace(Name=value('Phase_9'), DeformCalcType=Enum())
        by_loading = SimpleNamespace(Name=value('Phase_20'), DeformCalcType=value(3),
                                     Deform=SimpleNamespace(LoadingType=value('Incremental multipliers')))
        plastic = SimpleNamespace(Name=value('Phase_1'), DeformCalcType=value(1),
                                  Deform=SimpleNamespace(LoadingType=value('Staged construction')))
        self.assertTrue(plaxis_export.is_safety(by_str))
        self.assertTrue(plaxis_export.is_safety(by_loading))
        self.assertFalse(plaxis_export.is_safety(plastic))
        self.assertEqual(plaxis_export.PhaseRow(by_loading).calc_type, 'Safety')
        # PLAXIS 3D: the type as a number, 7 — Safety.
        self.assertTrue(plaxis_export.is_safety(SimpleNamespace(Name=value('Phase_18'), DeformCalcType=value(7))))
        self.assertFalse(plaxis_export.is_safety(SimpleNamespace(Name=value('Phase_1'), DeformCalcType=value(4))))
        self.assertEqual(plaxis_export.calc_type(SimpleNamespace(DeformCalcType=value(3))), 'Initial (3)')


class BusyServerTests(unittest.TestCase):
    def test_busy_input_is_reported_instead_of_hanging(self):
        import time
        import types
        easy = types.ModuleType('plxscripting.easy')
        easy.new_server = lambda *a, **k: time.sleep(30)
        package = types.ModuleType('plxscripting')
        package.easy = easy
        saved = {k: sys.modules.get(k) for k in ('plxscripting', 'plxscripting.easy')}
        sys.modules.update({'plxscripting': package, 'plxscripting.easy': easy})
        old = dict(plaxis_export.CONNECT_SECONDS)
        plaxis_export.CONNECT_SECONDS['Input'] = 0.2
        try:
            with self.assertRaises(plaxis_export.ExportError) as caught:
                plaxis_export.connect(10000, 'Input')
            self.assertIn('не отвечает', str(caught.exception))
            self.assertIn('Configure remote scripting server', str(caught.exception))
        finally:
            plaxis_export.CONNECT_SECONDS.update(old)
            for k, v in saved.items():
                if v is None:
                    sys.modules.pop(k, None)
                else:
                    sys.modules[k] = v


class Fake3DOutput(FakeOutputSplit):
    """PLAXIS 3D Output: embedded beams EmbeddedBeam_1 (two segments) and EmbeddedBeam_2 at another place."""

    FORCES = ('N', 'M2', 'M3', 'Q12', 'Q13')

    def __init__(self, envelopes=True):
        super().__init__()
        del self.Plates
        self.p1a = SimpleNamespace(Name=value('EmbeddedBeam_1_1'))
        self.p1b = SimpleNamespace(Name=value('EmbeddedBeam_1_2'))
        self.p2 = SimpleNamespace(Name=value('EmbeddedBeam_2_1'))
        self.p3 = SimpleNamespace(Name=value('EmbeddedBeam_3_1'))
        self.EmbeddedBeams = [self.p1a, self.p1b, self.p2, self.p3]
        names = {n: n for n in ('X', 'Y', 'Z') + self.FORCES}
        if envelopes:
            for base in self.FORCES:
                for bound in ('Max', 'Min'):
                    names[f'{base}_Envelope{bound}'] = f'{base}_Envelope{bound}'
        self.ResultTypes = SimpleNamespace(EmbeddedBeam=SimpleNamespace(**names), Plate=SimpleNamespace(X='X', Y='Y', Z='Z'))
        self.Phases.append(SimpleNamespace(Name=value('Phase_4'), Identification=value('Phase_4')))

    def getresults(self, obj, phase, rtype, location):
        if name_of_phase(phase) == 'InitialPhase':
            raise RuntimeError('Object is not active in this phase')
        self.requested.append((obj.Name.value, phase.Name.value, rtype))
        z = [0.0, -1.0, -2.0] if obj is not self.p1b else [-2.0, -3.0, -4.0]
        k = 2.0 if obj is self.p2 else 1.0
        base = rtype.split('_')[0]
        values = {'X': [3.0 * k] * 3, 'Y': [1.0] * 3, 'Z': z, 'N': [-100.0 * k] * 3, 'M2': [30.0 * k, 40.0 * k, 0.0],
                  'M3': [40.0 * k, 30.0 * k, 0.0], 'Q12': [3.0] * 3, 'Q13': [4.0] * 3}[base]
        if 'Envelope' in rtype:
            return [v * (1.0 if rtype.endswith('Max') == (v >= 0) else 0.5) for v in values]
        return values


class WholeModel3DOutput(Fake3DOutput):
    """PLAXIS 3D Output that gives phases by name (g_o.Phase_2) and results of the whole model at once.
    Reading a phase name is forbidden: it is slow in a large model. EmbeddedBeam_3 stands elsewhere (x = 9)."""

    class Phase:
        def __init__(self, name):
            object.__setattr__(self, 'key', name)

        def __getattr__(self, item):
            raise AssertionError('phase property read: ' + item)

    def __init__(self):
        super().__init__()
        self.by_name = {n: self.Phase(n) for n in ('InitialPhase', 'Phase_1', 'Phase_2', 'Phase_3', 'Phase_4')}
        self.Phases = list(self.by_name.values())
        self.whole = 0

    def __getattr__(self, item):
        if item.startswith('Phase_') or item == 'InitialPhase':
            return self.__dict__['by_name'][item]
        raise AttributeError(item)

    def one(self, obj, phase, rtype):
        if phase.key == 'InitialPhase':
            raise RuntimeError('Object is not active in this phase')
        shim = SimpleNamespace(Name=value(phase.key))
        values = Fake3DOutput.getresults(self, obj, shim, rtype, 'node')
        return [9.0] * len(values) if obj is self.p3 and rtype.split('_')[0] == 'X' else values

    def getresults(self, *args):
        if len(args) == 4:
            return self.one(*args[:3])
        phase, rtype, _ = args
        self.whole += 1
        out = []
        for obj in self.EmbeddedBeams:
            try:
                out += self.one(obj, phase, rtype)
            except RuntimeError:
                pass
        return out


class Group3DTests(unittest.TestCase):
    def run_tree(self, output, selection, *extra):
        g_i = tree_input(selection)
        out = io.StringIO()
        with redirect_stdout(out):
            code = plaxis_export.main(['input', '--port', '10000', *extra],
                                      connector=lambda port, program='Output': {10000: g_i, 10002: output}[port])
        return code, json.loads(out.getvalue())

    def selection(self):
        return [SimpleNamespace(Name=value('EmbeddedBeam_1'), _plx_type='EmbeddedBeam'),
                SimpleNamespace(Name=value('Line_9'), _plx_type='Line', EmbeddedBeam=SimpleNamespace(Name=value('EmbeddedBeam_2')))]

    def test_selected_piles_are_a_group_with_3d_envelopes(self):
        output = Fake3DOutput()
        code, result = self.run_tree(output, self.selection())
        self.assertEqual(code, 0, result)
        self.assertEqual(result['info']['Dimension'], '3D')
        self.assertEqual(result['mode'], 'envelope')
        self.assertEqual(result['columns'], ['x', 'y', 'z', 'n_min', 'n_max', 'm2_min', 'm2_max', 'm3_min', 'm3_max',
                                             'q12_min', 'q12_max', 'q13_min', 'q13_max'])
        self.assertEqual([(o['name'], o['pile']) for o in result['objects']],
                         [('EmbeddedBeam_1_1', 'EmbeddedBeam_1'), ('EmbeddedBeam_1_2', 'EmbeddedBeam_1'), ('EmbeddedBeam_2_1', 'EmbeddedBeam_2')])
        self.assertEqual(result['result_types']['m2_max'], 'M2_EnvelopeMax')
        self.assertNotIn('EmbeddedBeam_3_1', {r[0] for r in output.requested})
        self.assertNotIn('moment_sign', [k for k, v in result.items() if v])
        self.assertEqual(result['data'][0]['nodes'][0][:3], [3.0, 1.0, 0.0])

    def test_per_phase_route_on_request(self):
        code, result = self.run_tree(Fake3DOutput(), self.selection(), '--per-phase')
        self.assertEqual(code, 0, result)
        self.assertNotIn('mode', result)
        self.assertEqual(result['columns'], ['x', 'y', 'z', 'n', 'm2', 'm3', 'q12', 'q13'])
        names = {p['id']: p['name'] for p in result['phases']}
        self.assertEqual({names[d['phase']] for d in result['data']}, {'Phase_1', 'Phase_2', 'Phase_4'})

    def test_whole_model_is_read_at_once_and_phases_by_name(self):
        output = WholeModel3DOutput()
        code, result = self.run_tree(output, self.selection())
        self.assertEqual(code, 0, result)
        self.assertEqual(result['mode'], 'envelope')
        # One data row per pile and branch: pile 1 joins its two segments (6 nodes), pile 3 is not taken.
        self.assertEqual(sorted(len(d['nodes']) for d in result['data']), [3, 3, 6, 6])
        objects = {o['id']: o['pile'] for o in result['objects']}
        self.assertEqual({objects[d['object']] for d in result['data']}, {'EmbeddedBeam_1', 'EmbeddedBeam_2'})
        # 13 result types × 2 branches, nothing per segment beyond the plan position of each pile.
        self.assertEqual(output.whole, 26)
        code, result = self.run_tree(WholeModel3DOutput(), self.selection(), '--per-phase')
        self.assertEqual(code, 0, result)
        self.assertEqual(len(result['data']), 6)

    def test_open_output_is_reused(self):
        g_i = tree_input(self.selection())
        output = WholeModel3DOutput()
        connections = {10000: g_i, 10002: output}
        out = io.StringIO()
        with redirect_stdout(out):
            code = plaxis_export.main(['input', '--port', '10000', '--reuse-output-port', '10002'],
                                      connector=lambda port, program='Output': connections[port])
        result = json.loads(out.getvalue())
        self.assertEqual(code, 0, result)
        self.assertIsNone(g_i.viewed)
        self.assertEqual(result['output_port'], 10002)
        # Nothing answers on the remembered port: Input opens Output as usual.
        out = io.StringIO()
        with redirect_stdout(out):
            code = plaxis_export.main(['input', '--port', '10000', '--reuse-output-port', '10009'],
                                      connector=lambda port, program='Output': connections[port])
        self.assertEqual(code, 0, out.getvalue())
        self.assertIsNotNone(g_i.viewed)

    def test_walls_are_not_supported_in_3d(self):
        output = Fake3DOutput()
        output.Plates = [SimpleNamespace(Name=value('Plate_1'))]
        output.ResultTypes.Plate = SimpleNamespace(**{n: n for n in ('X', 'Y', 'Z', 'N', 'M2', 'M3', 'Q12', 'Q13')})
        code, result = self.run_tree(output, [SimpleNamespace(Name=value('Plate_1'), _plx_type='Plate')])
        self.assertEqual(code, 1)
        self.assertIn('3D', result['error'])


if __name__ == '__main__':
    unittest.main()
