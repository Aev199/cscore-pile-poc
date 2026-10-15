#!/usr/bin/env python3
"""CScore pile ULS prototype orchestrator. No structural calculations are implemented here."""
from __future__ import annotations
import argparse
import csv
import itertools
import json
import math
import subprocess
import sys
from pathlib import Path
from typing import Any


class InputError(ValueError):
    pass


def finite_number(value: Any, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise InputError(f"{name}: требуется конечное число")
    return float(value)


def positive(value: Any, name: str, *, allow_zero: bool = False) -> float:
    number = finite_number(value, name)
    if number < 0 or (number == 0 and not allow_zero):
        raise InputError(f"{name}: требуется {'неотрицательное' if allow_zero else 'положительное'} число")
    return number



# Accidental eccentricity is a member-level assumption, not an intrinsic property
# of a PLAXIS force tuple. Explicit configuration prevents double counting.
ECC_MODES = {'sp63_stat_determinate', 'already_in_loads', 'research_none'}
ZERO_MOMENT_AXES = {
    'mx_positive': (1.0, 0.0), 'mx_negative': (-1.0, 0.0),
    'my_positive': (0.0, 1.0), 'my_negative': (0.0, -1.0),
}


def eccentricity_settings(data: dict) -> dict:
    raw = data.get('eccentricity')
    if raw is None:
        return {'mode': 'unspecified_legacy'}
    if not isinstance(raw, dict) or not isinstance(raw.get('mode'), str) or raw['mode'] not in ECC_MODES:
        raise InputError('eccentricity.mode: sp63_stat_determinate, already_in_loads или research_none')
    mode = raw['mode']
    if mode == 'sp63_stat_determinate':
        if set(raw) - {'mode', 'static_scheme', 'effective_length_mm', 'zero_moment_axis'}:
            raise InputError('Неизвестные параметры eccentricity для sp63_stat_determinate')
        if raw.get('static_scheme') != 'statically_determinate':
            raise InputError('Автоматический e_a пока поддерживает только статически определимую схему')
        length = positive(raw.get('effective_length_mm'), 'eccentricity.effective_length_mm')
        axis = raw.get('zero_moment_axis')
        if axis is not None and axis not in ZERO_MOMENT_AXES:
            raise InputError('eccentricity.zero_moment_axis: mx_positive/mx_negative/my_positive/my_negative')
        return {'mode': mode, 'static_scheme': 'statically_determinate',
                'effective_length_mm': length, 'zero_moment_axis': axis}
    if set(raw) != {'mode'}:
        raise InputError('Параметры длины и направления применимы только к sp63_stat_determinate')
    return {'mode': mode}


def prepare_design_loads(data: dict) -> tuple[list[dict], list[dict], dict]:
    """Preserve original joint loads; add e_a to the vector of bending moments on compression.

    e_a=max(l0/600,D/30,10mm), only for explicit statically determinate mode.
    Biaxial direction is an engineering hypothesis requiring separate validation.
    Pure concentric compression requires the user to specify a direction.
    """
    settings = eccentricity_settings(data)
    mode = settings['mode']
    ea_mm = (max(settings['effective_length_mm'] / 600,
                 data['pile']['diameter_mm'] / 30, 10.0)
             if mode == 'sp63_stat_determinate' else None)
    result, audit = [], []
    for load in data['loads']:
        original = {k: load[k] for k in ('id', 'n_kn', 'mx_knm', 'my_knm')}
        effective = original.copy()
        added_x = added_y = 0.0
        if mode == 'sp63_stat_determinate' and original['n_kn'] < 0:
            mx, my = original['mx_knm'], original['my_knm']
            magnitude = math.hypot(mx, my)
            if magnitude > 1e-12:
                ux, uy = mx / magnitude, my / magnitude
            else:
                axis = settings.get('zero_moment_axis')
                if axis is None:
                    raise InputError(
                        f"{original['id']}: при центральном сжатии нужно задать eccentricity.zero_moment_axis"
                    )
                ux, uy = ZERO_MOMENT_AXES[axis]
            delta = abs(original['n_kn']) * ea_mm / 1000.0
            added_x, added_y = delta * ux, delta * uy
            effective['mx_knm'] += added_x
            effective['my_knm'] += added_y
        if not all(math.isfinite(effective[k]) for k in ('n_kn', 'mx_knm', 'my_knm')):
            raise InputError(f"{original['id']}: переполнение при добавлении эксцентриситета")
        result.append(effective)
        audit.append({'id': original['id'], 'original': original,
                      'effective': effective.copy(),
                      'delta_mx_knm': added_x, 'delta_my_knm': added_y,
                      'ea_mm': ea_mm if original['n_kn'] < 0 and mode == 'sp63_stat_determinate' else 0.0})
    config = {**settings, 'ea_mm': ea_mm,
              'eccentricity_accounted_for': mode in ('sp63_stat_determinate', 'already_in_loads'),
              'note': ('Experimental uniaxial calibration to ARBAT; biaxial and zero-moment handling not independently validated'
                       if mode == 'sp63_stat_determinate' else
                       'No accidental eccentricity applied in this stage')}
    return result, audit, config


def validate_input(data: dict) -> None:
    if not isinstance(data, dict) or data.get('schema_version') != 1:
        raise InputError('schema_version должен быть равен 1')
    eccentricity_settings(data)
    pile = data.get('pile')
    materials = data.get('materials')
    search = data.get('search')
    loads = data.get('loads')
    if not all(isinstance(v, dict) for v in (pile, materials, search)) or not isinstance(loads, list):
        raise InputError('Требуются объекты pile, materials, search и массив loads')
    diameter = positive(pile.get('diameter_mm'), 'pile.diameter_mm')
    cover = positive(pile.get('cover_mm'), 'pile.cover_mm', allow_zero=True)
    tie_diameter = positive(pile.get('ties_diameter_mm'), 'pile.ties_diameter_mm', allow_zero=True)
    spacing = positive(pile.get('min_clear_spacing_mm'), 'pile.min_clear_spacing_mm', allow_zero=True)
    if 2 * (cover + tie_diameter) >= diameter:
        raise InputError('Защитный слой и поперечная арматура не помещаются в сечении')
    if not isinstance(pile.get('mesh_resolution'), int) or not 10 <= pile['mesh_resolution'] <= 200:
        raise InputError('pile.mesh_resolution должен быть целым от 10 до 200')
    if not isinstance(pile.get('polygon_sides'), int) or not 32 <= pile['polygon_sides'] <= 256:
        raise InputError('pile.polygon_sides должен быть целым от 32 до 256')
    for key in ('concrete_rb_kpa', 'concrete_rbt_kpa', 'concrete_e_kpa', 'rebar_rs_kpa', 'rebar_e_kpa'):
        positive(materials.get(key), f'materials.{key}')
    for key in ('concrete_ec1', 'concrete_ec2', 'rebar_ec2'):
        val = finite_number(materials.get(key), f'materials.{key}')
        if val >= 0:
            raise InputError(f'materials.{key} должен быть отрицательным')
    for key in ('concrete_et1', 'concrete_et2', 'rebar_et2'):
        positive(materials.get(key), f'materials.{key}')
    if not loads:
        raise InputError('loads не может быть пустым')
    seen = set()
    for idx, load in enumerate(loads):
        if not isinstance(load, dict) or not isinstance(load.get('id'), str) or not load['id'].strip():
            raise InputError(f'loads[{idx}].id обязателен')
        if load['id'] in seen:
            raise InputError(f'Повторяющийся id усилий: {load["id"]}')
        seen.add(load['id'])
        for key in ('n_kn', 'mx_knm', 'my_knm'):
            finite_number(load.get(key), f'loads[{idx}].{key}')
    counts = search.get('bar_counts')
    diameters = search.get('bar_diameters_mm')
    rotations = search.get('rotation_deg')
    if not isinstance(counts, list) or not counts or not isinstance(diameters, list) or not diameters or not isinstance(rotations, list) or not rotations:
        raise InputError('search: нужны непустые массивы bar_counts, bar_diameters_mm, rotation_deg')
    if any(isinstance(n, bool) or not isinstance(n, int) or n < 6 or n > 100 for n in counts):
        raise InputError('search.bar_counts: целые от 6 до 100')
    for d in diameters:
        positive(d, 'search.bar_diameters_mm')
    for rotation in rotations:
        finite_number(rotation, 'search.rotation_deg')
    if len(counts) * len(diameters) * len(rotations) > 1000:
        raise InputError('Слишком много кандидатов; предел первого прототипа — 1000')


def make_candidates(data: dict) -> tuple[list[dict], list[dict]]:
    pile = data['pile']
    radius_mm = pile['diameter_mm'] / 2
    permitted, rejected = [], []
    for count, d, rotation in itertools.product(sorted(set(data['search']['bar_counts'])),
                                                sorted(set(data['search']['bar_diameters_mm'])),
                                                sorted(set(data['search']['rotation_deg']))):
        label = f'{count}x{d:g}@{rotation:g}deg'
        axis_radius = radius_mm - pile['cover_mm'] - pile['ties_diameter_mm'] - d / 2
        # chord of a regular polygon of bar axes; explicit geometric check, not SP 63 clearance verification
        clearance = 2 * axis_radius * math.sin(math.pi / count) - d
        area_mm2 = count * math.pi * d * d / 4
        candidate = {'id': label, 'bar_count': count, 'bar_diameter_mm': d,
                     'rotation_deg': rotation, 'bar_axis_radius_mm': axis_radius,
                     'steel_area_mm2': area_mm2, 'clearance_mm': clearance}
        if axis_radius <= 0 or clearance < pile['min_clear_spacing_mm']:
            candidate['reason'] = 'не проходит предварительную геометрическую проверку'
            rejected.append(candidate)
        else:
            permitted.append(candidate)
    return permitted, rejected


def build_engine_request(data: dict, candidates: list[dict]) -> dict:
    return {'schema_version': 1,
            'pile': {'diameter_mm': data['pile']['diameter_mm'],
                     'mesh_resolution': data['pile']['mesh_resolution'],
                     'polygon_sides': data['pile']['polygon_sides']},
            'materials': data['materials'],
            'loads': data['loads'],
            'candidates': candidates}


def parse_runner_results(request: dict, raw: Any) -> list[dict]:
    """Fail-closed: unknown cases, missing results and non-convergence cannot pass."""
    expected_candidates = {c['id']: c for c in request['candidates']}
    expected_loads = {l['id'] for l in request['loads']}
    if not isinstance(raw, dict) or raw.get('schema_version') != 1 or not isinstance(raw.get('cases'), list):
        raise InputError('Ответ расчётного ядра имеет неверный формат')
    seen = set()
    result = []
    for item in raw['cases']:
        if not isinstance(item, dict) or not isinstance(item.get('id'), str):
            raise InputError('От ядра получен некорректный кандидат')
        name = item['id']
        if name not in expected_candidates or name in seen:
            raise InputError(f'Ядро вернуло неизвестный/повторяющийся кандидат: {name}')
        seen.add(name)
        cases = item.get('loads')
        if not isinstance(cases, list):
            raise InputError(f'У кандидата {name} нет массива loads')
        if len(cases) != len(expected_loads):
            raise InputError(f'{name}: неполный набор результатов')
        load_seen = set()
        decisions = []
        for row in cases:
            if not isinstance(row, dict) or row.get('id') not in expected_loads or row.get('id') in load_seen:
                raise InputError(f'{name}: дублированный или неизвестный набор усилий')
            load_seen.add(row['id'])
            if row.get('converged') is True and row.get('error') is None:
                factor = row.get('factor')
                util = row.get('utilization')
                if not isinstance(factor, (float, int)) or not isinstance(util, (float, int)) or not math.isfinite(factor) or not math.isfinite(util) or factor <= 0 or util < 0 or not math.isclose(factor * util, 1, rel_tol=0.01):
                    decision = 'indeterminate'
                else:
                    decision = 'pass_uls' if util <= 1.0 else 'fail_uls'
            else:
                decision = 'indeterminate'
            decisions.append({**row, 'decision': decision})
        statuses = [x['decision'] for x in decisions]
        overall = ('indeterminate' if 'indeterminate' in statuses else
                   'fail_uls' if 'fail_uls' in statuses else 'pass_uls')
        validated = expected_candidates[name].copy()
        validated['status'] = overall
        validated['loads'] = decisions
        validated['max_utilization'] = max((l['utilization'] for l in decisions if l['decision'] != 'indeterminate'), default=None)
        result.append(validated)
    if seen != set(expected_candidates):
        raise InputError('Не все кандидаты рассчитаны ядром')
    return sorted(result, key=lambda x: (x['steel_area_mm2'], x['bar_count'], x['bar_diameter_mm'], x['rotation_deg']))


def run(input_path: Path, runner: Path, output_path: Path, csv_path: Path, *, timeout: int = 900) -> dict:
    data = json.loads(input_path.read_text(encoding='utf-8'), parse_constant=lambda x: (_ for _ in ()).throw(InputError(f'Запрещено число {x}')))
    validate_input(data)
    permitted, rejected = make_candidates(data)
    if not permitted:
        raise InputError('Нет геометрически допустимых кандидатов для расчёта')
    effective_loads, forces_audit, eccentricity = prepare_design_loads(data)
    engine_data = {**data, 'loads': effective_loads}
    request = build_engine_request(engine_data, permitted)
    if runner.suffix.lower() == '.dll':
        argv = ['dotnet', str(runner.resolve())]
    else:
        argv = [str(runner.resolve())]
    p = subprocess.run(argv, input=json.dumps(request, ensure_ascii=False, allow_nan=False), capture_output=True, text=True, timeout=timeout, encoding='utf-8')
    if p.returncode:
        raise RuntimeError(f'CScore завершился с ошибкой ({p.returncode}): {p.stderr.strip()[:2000]}')
    calculated = parse_runner_results(request, json.loads(p.stdout, parse_constant=lambda x: (_ for _ in ()).throw(InputError(f'Ядро вернуло {x}'))))
    passes = [x for x in calculated if x['status'] == 'pass_uls']
    result = {'schema_version': 1, 'purpose': 'research_prototype_not_for_design',
              'calculation_scope': 'section_uls_only_no_eta_no_shear_no_crack_no_code_detailing',
              'eccentricity': eccentricity, 'forces_audit': forces_audit,
              'selected_uls_candidate': (passes[0]['id'] if passes else None)
                 if eccentricity['eccentricity_accounted_for'] else None,
              'engine_commit': '4ae6aa077be5b29145930ec093ba1c9eb7776222',
              'cases': calculated, 'rejected_geometry': rejected,
              'note': 'pass_uls означает только проверку нормального сечения; НЕ готовность КЖ'}
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(result, indent=2, ensure_ascii=False, allow_nan=False), encoding='utf-8')
    csv_path.parent.mkdir(parents=True, exist_ok=True)
    with csv_path.open('w', encoding='utf-8-sig', newline='') as h:
        wr = csv.writer(h, delimiter=';')
        wr.writerow(['candidate','steel_area_mm2','clearance_mm','status','max_utilization','load_id','load_status','utilization','factor','converged','error','moment_utilization','moment_factor','moment_converged','moment_error','original_n_kn','original_mx_knm','original_my_knm',
                     'effective_n_kn','effective_mx_knm','effective_my_knm','ea_mm','delta_mx_knm','delta_my_knm'])
        force_by_id = {item['id']: item for item in forces_audit}
        for c in calculated:
            for l in c['loads']:
                force = force_by_id[l['id']]
                wr.writerow([c['id'], round(c['steel_area_mm2'], 2), round(c['clearance_mm'],2),c['status'],c['max_utilization'],l['id'],l['decision'],l.get('utilization'),l.get('factor'),l.get('converged'),l.get('error'),l.get('moment_utilization'),l.get('moment_factor'),l.get('moment_converged'),l.get('moment_error'),
                             force['original']['n_kn'],force['original']['mx_knm'],force['original']['my_knm'],
                             force['effective']['n_kn'],force['effective']['mx_knm'],force['effective']['my_knm'],
                             force['ea_mm'],force['delta_mx_knm'],force['delta_my_knm']])
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description='Экспериментальный перебор продольного армирования БНС с помощью CScore (не для проектирования)')
    parser.add_argument('input', type=Path, help='JSON с геометрией, усилиями и вариантами')
    parser.add_argument('--runner', required=True, type=Path, help='Скомпилированный CScorePileRunner.exe или DLL')
    parser.add_argument('--output', type=Path, default=Path('results/pile_results.json'))
    parser.add_argument('--csv', type=Path, default=Path('results/pile_results.csv'))
    args = parser.parse_args()
    try:
        result = run(args.input, args.runner, args.output, args.csv)
    except (ValueError, OSError, RuntimeError, subprocess.TimeoutExpired) as err:
        print(f'ОШИБКА: {err}', file=sys.stderr)
        return 2
    print(f'Кандидатов: {len(result["cases"])}, прошли только прочность нормального сечения: {sum(c["status"] == "pass_uls" for c in result["cases"])}')
    print(f'Предварительно минимальный по площади: {result["selected_uls_candidate"] or "не найден"}')
    if not result['eccentricity']['eccentricity_accounted_for']:
        print('ВНИМАНИЕ: e_a не подтверждён. Подбор не выдаётся; настройте eccentricity.mode.',
              file=sys.stderr)
    elif result['eccentricity']['mode'] == 'sp63_stat_determinate':
        print(f"Случайный эксцентриситет: e_a={result['eccentricity']['ea_mm']:.2f} мм "
              "(только при сжатии; исходные усилия сохранены в отчёте)")
    # Provide actionable diagnostics in CI logs, not only in downloaded JSON artifacts.
    if all(c['status'] == 'indeterminate' for c in result['cases']):
        from collections import Counter
        reasons = Counter(
            row.get('error') or 'invalid_or_inconsistent_solver_result'
            for case in result['cases']
            for row in case['loads']
            if row['decision'] == 'indeterminate'
        )
        print('Все варианты имеют неопределённый результат. Основные причины:', file=sys.stderr)
        for reason, count in reasons.most_common(5):
            print(f'  {count} x {reason}', file=sys.stderr)
    print('ВНИМАНИЕ: пока не проверяются трещины, поперечная сила, устойчивость и полный конструктив.')
    print(f'Результаты: {args.output}, {args.csv}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
