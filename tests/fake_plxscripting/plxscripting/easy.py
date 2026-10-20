"""Stand-in for PLAXIS plxscripting in the bridge test: Input on port 10000, Output on 10002."""
from types import SimpleNamespace


def value(v):
    return SimpleNamespace(value=v)


class Input:
    def __init__(self):
        self.selection = [SimpleNamespace(Name=value('Line_1'), _plx_type='Line', Plate=SimpleNamespace(Name=value('Plate_1')))]
        initial = SimpleNamespace(Name=value('InitialPhase'), Identification=value('Начальная'), DeformCalcType=value('K0 procedure'), PreviousPhase=None)
        p1 = SimpleNamespace(Name=value('Phase_1'), Identification=value('Выемка'), DeformCalcType=value('Plastic'), PreviousPhase=initial)
        fs = SimpleNamespace(Name=value('Phase_2'), Identification=value('Запас'), DeformCalcType=value('Safety'), PreviousPhase=p1)
        self.Phases = [initial, p1, fs]

    def view(self, phase):
        return 10002


class Output:
    def __init__(self):
        self.Plates = [SimpleNamespace(Name=value('Plate_1_1')), SimpleNamespace(Name=value('Plate_1_2'))]
        self.Phases = [SimpleNamespace(Name=value(n), Identification=value(n)) for n in ('InitialPhase', 'Phase_1', 'Phase_2')]
        names = dict(X='X', Y='Y', Nx2D='Nx2D', M2D='M2D', Q2D='Q2D')
        for base in ('Nx2D', 'M2D', 'Q2D'):
            for bound in ('Max', 'Min'):
                names[base + '_Envelope' + bound] = base + '_Envelope' + bound
        group = SimpleNamespace(**names)
        self.ResultTypes = SimpleNamespace(Plate=group)
        self.GeneralInfo = SimpleNamespace(UnitForce=value('kN'), UnitLength=value('m'), Version=value('22.01'), Title=value('Котлован'))

    def getresults(self, obj, phase, rtype, location):
        if phase.Name.value == 'InitialPhase':
            raise RuntimeError('not active')
        if phase.Name.value == 'Phase_2':
            raise AssertionError('Safety phase must not be read')
        top = 0.0 if obj is self.Plates[0] else -5.0
        y = [top - 0.5 * i for i in range(11)]
        if 'Envelope' in rtype:
            base = {'Nx2D': [-20.0 * (-v + 1) for v in y], 'M2D': [100.0 * (-v) for v in y], 'Q2D': [-100.0] * 11}[rtype.split('_')[0]]
            return [v * (0.5 if rtype.endswith('Max') == (v < 0) else 1.0) for v in base]
        return {'X': [5.0] * 11, 'Y': y, 'Nx2D': [-20.0 * (-v + 1) for v in y],
                'M2D': [100.0 * (-v) for v in y], 'Q2D': [-100.0] * 11}[rtype]


def new_server(host, port, password=None):
    import os
    if password != 'secret':
        raise RuntimeError('wrong password')
    return None, {10000: Input, 10002: Output}[port]()
