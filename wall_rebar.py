#!/usr/bin/env python3
"""Identical wall cages from per-metre forces. Experimental section checks only."""
from __future__ import annotations

import argparse
import copy
import csv
import itertools
import json
import math
from pathlib import Path
import subprocess
import sys

from pile_rebar import InputError, positive, finite_number, validate_sls_probe, parse_runner_results

FACES = ('face_negative', 'face_positive')


def exact_keys(obj, allowed, label):
    if not isinstance(obj, dict) or set(obj) - set(allowed):
        raise InputError(f'{label}: неверный объект или неподдерживаемые поля')


def integer(value, low, high, label):
    if type(value) is not int or not low <= value <= high:
        raise InputError(f'{label}: целое от {low} до {high}')


def scaled_loads(rows, width_m, sls=False):
    mapping = ({'n_kn_per_m':'n_kn', 'mx_total_knm_per_m':'mx_total_knm',
                'mx_long_knm_per_m':'mx_long_knm'} if sls else
               {'n_kn_per_m':'n_kn', 'mx_knm_per_m':'mx_knm'})
    if not isinstance(rows, list) or not 1 <= len(rows) <= 1000:
        raise InputError('loads: требуется 1–1000 сочетаний')
    result, seen = [], set()
    for row in rows:
        exact_keys(row, {'id', *mapping, 'n_long_kn_per_m'} if sls else {'id', *mapping, 'q_kn_per_m'}, 'loads')
        name = row.get('id')
        if not isinstance(name, str) or not name.strip() or name in seen:
            raise InputError('loads: пустой или повторный ID')
        seen.add(name)
        converted = {'id': name}
        for key, dest in mapping.items():
            converted[dest] = finite_number(row.get(key), key) * width_m
            finite_number(converted[dest], dest)
        if sls and row.get('n_long_kn_per_m') is not None:
            converted['n_long_kn'] = finite_number(row['n_long_kn_per_m'], 'n_long_kn_per_m') * width_m
        if not sls and 'q_kn_per_m' in row:
            converted['q_kn'] = finite_number(row['q_kn_per_m'], 'q_kn_per_m') * width_m
        converted.update({'my_total_knm': 0, 'my_long_knm': 0} if sls else {'my_knm': 0})
        result.append(converted)
    return result


def validate_input(data):
    exact_keys(data, {'schema_version','wall','search','materials','loads','sls_probe',
                      'force_basis','eccentricity','transverse'}, 'input')
    if type(data.get('schema_version')) is not int or data['schema_version'] != 1:
        raise InputError('schema_version должен быть 1')
    wall = data.get('wall')
    exact_keys(wall, {'panel_width_mm','thickness_mm','cage_count','cage_width_mm',
                      'end_cover_mm','cover_mm','ties_diameter_mm',
                      'min_clear_spacing_mm','min_cage_gap_mm','mesh_resolution'}, 'wall')
    for key in ('panel_width_mm','thickness_mm','cage_width_mm','end_cover_mm',
                'cover_mm','ties_diameter_mm','min_clear_spacing_mm'):
        positive(wall.get(key), key)
    positive(wall.get('min_cage_gap_mm'), 'min_cage_gap_mm', allow_zero=True)
    integer(wall.get('cage_count'), 1, 3, 'cage_count')
    integer(wall.get('mesh_resolution'), 10, 200, 'mesh_resolution')
    if wall['panel_width_mm'] > 20000 or wall['thickness_mm'] > 5000:
        raise InputError('Размер вне области прототипа (ширина ≤20м, толщина ≤5м)')
    if 2*(wall['cover_mm']+wall['ties_diameter_mm']) >= wall['thickness_mm']:
        raise InputError('Армирование не помещается по толщине')
    required = (wall['cage_count']*wall['cage_width_mm'] + 2*wall['end_cover_mm']
                + (wall['cage_count']-1)*wall['min_cage_gap_mm'])
    if required > wall['panel_width_mm']:
        raise InputError('Каркасы и промежутки не помещаются в захватке')
    exact_keys(data.get('search'), FACES, 'search')
    combinations = 1
    for face in FACES:
        options = data['search'].get(face)
        exact_keys(options, {'bar_counts','bar_diameters_mm'}, face)
        for key in ('bar_counts','bar_diameters_mm'):
            values = options.get(key)
            if not isinstance(values,list) or not 1 <= len(values) <= 30:
                raise InputError(f'{face}.{key}: непустой список до 30 вариантов')
            for val in values:
                if key == 'bar_counts': integer(val,2,100,key)
                else:
                    positive(val,key)
                    if val > 60: raise InputError('Диаметр больше 60мм не поддерживается')
            if len(set(values)) != len(values): raise InputError(f'{key}: повторные варианты')
            combinations *= len(values)
    if combinations > 500: raise InputError('Не более 500 вариантов армирования за запуск')
    mat = data.get('materials')
    if not isinstance(mat,dict): raise InputError('materials обязателен')
    for key in ('concrete_rb_kpa','concrete_rbt_kpa','concrete_e_kpa','rebar_rs_kpa',
                'rebar_e_kpa','concrete_et1','concrete_et2','rebar_et2'):
        positive(mat.get(key),key)
    for key in ('concrete_ec1','concrete_ec2','rebar_ec2'):
        if finite_number(mat.get(key),key) >= 0: raise InputError(f'{key} должен быть отрицательным')
    ecc = data.get('eccentricity')
    if ecc not in ({'mode':'already_in_loads'}, {'mode':'research_none'}):
        raise InputError('eccentricity: явно already_in_loads или research_none')
    expected = {'uls':'design', **({'sls':'normative'} if 'sls_probe' in data else {})}
    if data.get('force_basis') not in (expected, {'uls':'design','sls':'normative'}):
        raise InputError('force_basis: ULS design, SLS normative')
    scaled_loads(data.get('loads'), wall['panel_width_mm']/1000)
    transverse = data.get('transverse')
    if transverse is not None:
        exact_keys(transverse, {'mode','diameter_mm','spacing_mm','effective_legs_per_cage',
                                'rsw_kpa','constructive_confirmed'}, 'transverse')
        if transverse.get('mode') not in ('off', 'screen'):
            raise InputError('transverse.mode: off либо screen')
        if transverse['mode'] == 'screen':
            for key in ('diameter_mm','spacing_mm','rsw_kpa'):
                positive(transverse.get(key), key)
            integer(transverse.get('effective_legs_per_cage'), 1, 30, 'Рабочих ветвей в каркасе')
            if transverse['rsw_kpa'] > 300000:
                raise InputError('Rsw выше 300 МПа: проверьте расчётное сопротивление поперечной арматуры')
            if type(transverse.get('constructive_confirmed')) is not bool:
                raise InputError('Подтверждение конструктивных требований 10.3 должно быть логическим')
            if any('q_kn_per_m' not in load for load in data['loads']):
                raise InputError('При расчёте Q требуется q_kn_per_m в каждом ULS сочетании')
    if 'sls_probe' in data:
        if not isinstance(data['sls_probe'],dict): raise InputError('sls_probe должен быть объектом')
        probe = copy.deepcopy(data['sls_probe'])
        probe['loads'] = scaled_loads(probe.get('loads'),wall['panel_width_mm']/1000,True)
        validate_sls_probe(probe)


def make_candidates(data):
    wall, search = data['wall'], data['search']
    width, thickness = wall['panel_width_mm'], wall['thickness_mm']
    cages, cw = wall['cage_count'], wall['cage_width_mm']
    gap = ((width-2*wall['end_cover_mm']-cages*cw)/(cages-1) if cages>1 else None)
    starts = ([-cw/2] if cages==1 else
              [-width/2+wall['end_cover_mm']+i*(cw+gap) for i in range(cages)])
    good,bad = [],[]
    for nn,dn,np,dp in itertools.product(search[FACES[0]]['bar_counts'],search[FACES[0]]['bar_diameters_mm'],
                                        search[FACES[1]]['bar_counts'],search[FACES[1]]['bar_diameters_mm']):
        c = {'id':f'{cages}c_neg{nn}d{dn:g}_pos{np}d{dp:g}', 'actual_bars':[],
             'cage_gap_mm':gap, 'bar_count':cages*(nn+np), 'bar_diameter_mm':max(dn,dp),
             'rotation_deg':0, 'bar_axis_radius_mm':0}
        reasons, clearances = [], []
        for face,n,d,sign in ((FACES[0],nn,dn,-1),(FACES[1],np,dp,1)):
            inset = wall['ties_diameter_mm']+d/2
            pitch = (cw-2*inset)/(n-1)
            depth = thickness/2-wall['cover_mm']-inset
            clearance = pitch-d
            total = cages*n
            eq_pitch = width/total
            c[face] = {'count_per_cage':n,'diameter_mm':d,'count_per_panel':total,
                       'area_mm2_per_m':total*math.pi*d*d/4/(width/1000),
                       'equivalent_spacing_mm':eq_pitch, 'actual_spacing_mm':pitch,
                       'actual_clear_spacing_mm':clearance, 'bar_axis_y_mm':sign*depth}
            if depth<=d/2 or clearance<wall['min_clear_spacing_mm'] or eq_pitch<=d:
                reasons.append(f'{face}: недостаточный просвет или стержни не помещаются')
            clearances.append(clearance)
            for k,start in enumerate(starts):
                c['actual_bars'].extend({'cage':k+1,'face':face,'x_mm':start+inset+i*pitch,
                                        'y_mm':sign*depth,'diameter_mm':d} for i in range(n))
        through_clear = c[FACES[1]]['bar_axis_y_mm']-c[FACES[0]]['bar_axis_y_mm']-(dn+dp)/2
        if through_clear < wall['min_clear_spacing_mm']:
            reasons.append('Недостаточный просвет между противоположными рядами')
        clearances.append(through_clear)
        if gap is not None:
            between_clear = gap+2*wall['ties_diameter_mm']
            clearances.append(between_clear)
            if between_clear < wall['min_clear_spacing_mm']:
                reasons.append('Недостаточный просвет стержней между каркасами')
        c['clearance_mm'] = min(clearances)
        c['steel_area_mm2_per_m'] = sum(c[f]['area_mm2_per_m'] for f in FACES)
        c['steel_area_mm2'] = c['steel_area_mm2_per_m']*width/1000
        if reasons: bad.append({**c,'reason':'; '.join(reasons)})
        else: good.append(c)
    good.sort(key=lambda c:(c['steel_area_mm2_per_m'],c['bar_count'],c['id']))
    return good,bad


def build_engine_request(data, candidate):
    wall = data['wall']; width = wall['panel_width_mm']; bars = []
    for face in FACES:
        f = candidate[face]
        # Uniform equivalent reinforcement; physical cages are kept separately.
        bars.extend({'x_mm':-width/2+(i+.5)*f['equivalent_spacing_mm'],
                     'y_mm':f['bar_axis_y_mm'],'diameter_mm':f['diameter_mm']}
                    for i in range(f['count_per_panel']))
    request = {'schema_version':1, 'pile':{'diameter_mm':0,'polygon_sides':0,
                'mesh_resolution':wall['mesh_resolution']},
               'research_rectangle':{'width_mm':width,'height_mm':wall['thickness_mm'],
                 'mesh_resolution':wall['mesh_resolution'],'bars':bars},
               'materials':copy.deepcopy(data['materials']),
               'loads':scaled_loads(data['loads'],width/1000),
               # CScore rejects unknown JSON fields. Keep report metadata local.
               'candidates':[{key:candidate[key] for key in (
                   'id','bar_count','bar_diameter_mm','rotation_deg',
                   'bar_axis_radius_mm','steel_area_mm2','clearance_mm')}],
               'sls_execution_policy':'uls_pass_only'}
    transverse = data.get('transverse')
    if transverse and transverse['mode'] == 'screen':
        request['shear_screening'] = {
            'enabled': True,
            'diameter_mm': transverse['diameter_mm'],
            'spacing_mm': transverse['spacing_mm'],
            'effective_legs_per_cage': transverse['effective_legs_per_cage'],
            'cage_count': wall['cage_count'],
            'rsw_kpa': transverse['rsw_kpa'],
            'constructive_confirmed': transverse['constructive_confirmed']
        }
    if 'sls_probe' in data:
        request['sls_probe'] = copy.deepcopy(data['sls_probe'])
        request['sls_probe']['loads'] = scaled_loads(data['sls_probe']['loads'],width/1000,True)
    return request


def calculate(data, engine):
    validate_input(data)
    candidates, rejected = make_candidates(data)
    calculated = []
    for candidate in candidates:
        request = build_engine_request(data,candidate)
        validated = parse_runner_results(request,engine(request))[0]
        row = {**copy.deepcopy(candidate), **validated}
        # C# legacy ULS parser accepts bool as a number; do not inherit that ambiguity here.
        for load in row['loads']:
            if any(isinstance(load.get(k),bool) for k in ('factor','utilization')):
                raise InputError('Ядро вернуло логическое значение вместо коэффициента')
        known = [l for l in row['loads'] if l['decision']!='indeterminate']
        row['governing_uls_id'] = max(known,key=lambda l:l['utilization'])['id'] if known else None
        sls = [l for l in row.get('sls_probe') or [] if l.get('status')=='research_calculated']
        row['governing_sls_id'] = max(sls,key=lambda l:l['preliminary_governing_utilization'])['id'] if sls else None
        row['design_status'] = 'NOT_VERIFIED'
        calculated.append(row)
    passes = [c for c in calculated if c['status']=='pass_uls']
    prelim = [c for c in passes if c.get('sls_status')=='preliminary_below_limit']
    can_select = data['eccentricity']['mode']=='already_in_loads'
    return {'schema_version':1,'purpose':'research_prototype_not_for_design',
            'calculation_model':'uniaxial_homogenized_panel_scaled_from_per_metre',
            'wall':copy.deepcopy(data['wall']), 'force_basis':copy.deepcopy(data['force_basis']),
            'force_basis_note':'user_declared_not_independently_verified',
            'eccentricity':copy.deepcopy(data['eccentricity']),
            'source_loads_per_m':copy.deepcopy(data['loads']),
            'source_sls_per_m':copy.deepcopy(data.get('sls_probe')),
            'engine_force_multiplier':data['wall']['panel_width_mm']/1000,
            'sls_execution_policy':'uls_pass_only',
            'selected_uls_candidate':passes[0]['id'] if passes and can_select else None,
            'selected_preliminary_uls_sls_research_candidate':prelim[0]['id'] if prelim and can_select else None,
            'cases':calculated, 'rejected_geometry':rejected,
            'shear_scope':'local_constant_profile_research_screen_only' if data.get('transverse',{}).get('mode')=='screen' else 'not_evaluated',
            'design_status':'NOT_VERIFIED',
            'limitations':['SLS experimental; no local crack checks between cages',
                           'Shear screening is local only, not a complete inclined-section check',
                           'No verified shear, anchorage, stability or full code detailing checks']}


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8'),
                      parse_constant=lambda s: (_ for _ in ()).throw(InputError(f'Недопустимое число {s}')))


def invoke_engine(runner, request, timeout=900):
    runner = Path(runner).resolve()
    argv = ['dotnet',str(runner)] if runner.suffix.lower()=='.dll' else [str(runner)]
    proc = subprocess.run(argv,input=json.dumps(request,ensure_ascii=False,allow_nan=False),
                          capture_output=True,text=True,encoding='utf-8',timeout=timeout)
    if proc.returncode: raise RuntimeError(f'CScore: {proc.stderr[:2000]}')
    return json.loads(proc.stdout,parse_constant=lambda s: (_ for _ in ()).throw(InputError(s)))


def run(input_path, runner, output_path, csv_path):
    result = calculate(read_json(input_path),lambda req:invoke_engine(runner,req))
    output_path, csv_path = Path(output_path),Path(csv_path)
    output_path.parent.mkdir(parents=True,exist_ok=True)
    output_path.write_text(json.dumps(result,ensure_ascii=False,indent=2,allow_nan=False),encoding='utf-8')
    csv_path.parent.mkdir(parents=True,exist_ok=True)
    with csv_path.open('w',encoding='utf-8-sig',newline='') as stream:
        writer=csv.writer(stream,delimiter=';')
        writer.writerow(['candidate','face','bars_per_cage','diameter_mm','As_mm2_per_m',
                         'equivalent_spacing_mm','actual_spacing_mm','uls_status','max_utilization',
                         'governing_uls_id','sls_status','governing_sls_id','design_status'])
        for c in result['cases']:
            for face in FACES:
                f=c[face]
                writer.writerow([c['id'],face,f['count_per_cage'],f['diameter_mm'],f['area_mm2_per_m'],
                                 f['equivalent_spacing_mm'],f['actual_spacing_mm'],c['status'],c['max_utilization'],
                                 c['governing_uls_id'],c.get('sls_status','not_requested'),c['governing_sls_id'],'NOT_VERIFIED'])
    return result


def main():
    parser=argparse.ArgumentParser(description='Предварительный подбор одинаковых каркасов стены в грунте')
    parser.add_argument('input',type=Path)
    parser.add_argument('--runner',type=Path)
    parser.add_argument('--preview',action='store_true',help='Показать геометрию и As/м без расчёта')
    parser.add_argument('--output',type=Path,default=Path('results/wall_results.json'))
    parser.add_argument('--csv',type=Path,default=Path('results/wall_results.csv'))
    args=parser.parse_args()
    try:
        if args.preview:
            data=read_json(args.input); validate_input(data)
            good,bad=make_candidates(data)
            print(f'Допустимых вариантов: {len(good)}; исключено геометрией: {len(bad)}')
            for c in good:
                print(c['id'])
                for face in FACES:
                    f=c[face]
                    print(f"  {face}: {f['count_per_cage']}Ø{f['diameter_mm']:g} на каркас; "
                          f"As={f['area_mm2_per_m']/100:.2f} см²/м; шаг экв.={f['equivalent_spacing_mm']:.2f} мм; "
                          f"шаг в каркасе={f['actual_spacing_mm']:.2f} мм")
            return 0
        if args.runner is None: raise InputError('Укажите --runner или --preview')
        result=run(args.input,args.runner,args.output,args.csv)
        print(f"Рассчитано вариантов: {len(result['cases'])}; геометрических отказов: {len(result['rejected_geometry'])}")
        print(f"Предварительный ULS: {result['selected_uls_candidate'] or 'не найден'}")
        print(f"ULS + исследовательский SLS: {result['selected_preliminary_uls_sls_research_candidate'] or 'не найден'}")
        print('Только проверка нормального сечения. SLS не верифицирован для выпуска КЖ.')
        print(f'Результаты: {args.output}, {args.csv}')
        return 0
    except (ValueError,OSError,RuntimeError,subprocess.TimeoutExpired) as err:
        print(f'ОШИБКА: {err}',file=sys.stderr); return 2


if __name__=='__main__': sys.exit(main())
