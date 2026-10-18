"""Native-check input/output contracts. No resistance formulas or selection logic."""
import math

ULS_CHECKS=('normal_strength','shear_strip','shear_inclined','inclined_moment',
            'member_stability','normal_member_strength')
SLS_CHECKS=('crack_formation','crack_width_long','crack_width_short','sls_member_stability')
GLOBAL_CHECKS=('anchorage','detailing')
STATUSES={'passed','failed','calculated','not_evaluated','not_applicable','indeterminate','accounted_in_loads','stable','unstable'}


def default_context():
    return {'second_order_mode':'unspecified','l0_x_m':None,'l0_y_m':None,'psi':1.0,
            'inclined_moment_requested':False,'anchorage_confirmed':False,
            'anchorage_factor':None,'sections':[],'circular_shear':None}


def validate_context(value):
    if not isinstance(value,dict) or set(value)-set(default_context()):
        raise ValueError('Неизвестные параметры подключения CScore')
    c={**default_context(),**value}
    if c['second_order_mode'] not in ('unspecified','already_in_loads','cscore_member','section_only'):
        raise ValueError('Выберите режим второго порядка')
    for key in ('l0_x_m','l0_y_m','anchorage_factor'):
        v=c[key]
        if v is not None and (type(v) not in (int,float) or not math.isfinite(v) or v<=0):
            raise ValueError(f'{key}: требуется положительное конечное число')
    if c['second_order_mode']=='cscore_member' and any(c[k] is None for k in ('l0_x_m','l0_y_m')):
        raise ValueError('Для CScore задайте обе расчётные длины')
    if type(c['psi']) not in (int,float) or not math.isfinite(c['psi']) or not 0<=c['psi']<=1:
        raise ValueError('Доля длительного момента должна быть от 0 до 1')
    for key in ('inclined_moment_requested','anchorage_confirmed'):
        if type(c[key]) is not bool:raise ValueError('Подтверждение должно быть логическим')
    if c['anchorage_factor'] is not None and c['anchorage_factor']>1:
        raise ValueError('Коэффициент анкеровки должен быть от 0 до 1')
    if c['anchorage_confirmed'] and c['anchorage_factor'] is None:
        raise ValueError('Подтвердите коэффициент анкеровки')
    e=c['circular_shear']
    if e is not None:
        keys={'method','a_mm','winding_diameter_mm','circular_efficiency','efficiency_basis','confirmed'}
        if (not isinstance(e,dict) or set(e)!=keys
                or e['method'] not in ('vniigs_1984_user_spiral','vniigs_1984_concrete_only') or type(e['confirmed']) is not bool
                or not isinstance(e['efficiency_basis'],str) or len(e['efficiency_basis'])>1000):
            raise ValueError('Некорректная модель приведённого сечения БНС')
        for k in ('a_mm','winding_diameter_mm','circular_efficiency'):
            v=e[k]
            if v is not None and (type(v) not in (int,float) or not math.isfinite(v) or v<0
                    or k=='winding_diameter_mm' and v==0
                    or k=='circular_efficiency' and v>1):
                raise ValueError('Недопустимые параметры приведения спирали')
        if e['method']=='vniigs_1984_concrete_only' and any(
                e[k] is not None for k in ('winding_diameter_mm','circular_efficiency')):
            raise ValueError('В бетонной проверке параметры вклада навивки должны отсутствовать')
        if e['confirmed'] and not e['efficiency_basis'].strip():
            raise ValueError('Укажите обоснование принятой приведённой модели')
    if not isinstance(c['sections'],list) or len(c['sections'])>200:
        raise ValueError('Не более 200 сечений с данными об опирании')
    seen=set()
    for row in c['sections']:
        if not isinstance(row,dict) or set(row)!={'z','support_distance_positive_m','support_distance_negative_m'}:
            raise ValueError('Некорректные данные опоры сечения')
        z=row['z'];distances=[row['support_distance_positive_m'],row['support_distance_negative_m']]
        if (type(z) not in (int,float) or not math.isfinite(z) or z in seen
                or any(d is not None and (type(d) not in (int,float) or not math.isfinite(d) or d<0) for d in distances)):
            raise ValueError('Повторная отметка или некорректное расстояние до опоры')
        seen.add(z)
    return c


def request_context(project,z):
    c=validate_context(project.get('calculation_context',{}))
    section=next((r for r in c.pop('sections') if r['z']==z),{})
    return {**c,'support_distance_positive_m':section.get('support_distance_positive_m'),
            'support_distance_negative_m':section.get('support_distance_negative_m'),
            'eccentricity_in_loads':project.get('eccentricity_confirmed') is True}


def expected_checks(req):
    u=[r['id'] for r in req['loads']]
    s=[r['id'] for r in req.get('sls_probe',{}).get('loads',[])] or ['']
    return {(k,case) for case in u for k in ULS_CHECKS}|{(k,case) for case in s for k in SLS_CHECKS}|{(k,'') for k in GLOBAL_CHECKS}


def methods(check):
    if check in ('normal_strength','normal_member_strength'):
        return {'CScore.LimitForceSolver.AllFactor'}
    if check in SLS_CHECKS and check!='sls_member_stability':return {'CScore.CrackWidthSolver.Compute'}
    if check in ('member_stability','sls_member_stability'):return {'CScore.Sp63.RodEtaWiring','CScore.Sp63.RodEtaWiring.Apply'}
    return {'CScore.Sp63Shear.ShearInclinedChecker.Check','CScore.Sp63Shear.Sp63ShearApplicability.Evaluate'} if check in ('shear_strip','shear_inclined','inclined_moment') else {'CScore.Sp63Shear.ShearInclinedChecker'}


def validate_report(req,report):
    if (not isinstance(report,dict) or report.get('version')!=1
            or report.get('verified_for_design') is not False or not isinstance(report.get('checks'),list)):
        raise ValueError('CScore: нет достоверного полного отчёта проверок')
    expected=expected_checks(req);seen=set();failures=False;gaps=False
    for row in report['checks']:
        if not isinstance(row,dict):raise ValueError('CScore: некорректная проверка')
        key=(row.get('check_id'),row.get('case_id'))
        if key not in expected or key in seen:raise ValueError('CScore: повторная или неизвестная проверка')
        seen.add(key)
        status=row.get('status');ratio=row.get('utilization')
        if (status not in STATUSES or row.get('verified_for_design') is not False
                or not isinstance(row.get('engine_method'),str)
                or row['engine_method'] not in methods(key[0])):
            raise ValueError('CScore: недопустимый статус или источник проверки')
        if status=='passed' and isinstance(row.get('details'),dict) and row['details'].get('directions_complete') is False:
            raise ValueError('CScore: неполная проверка направлений не может быть пройдена')
        if status in ('passed','failed') and key[0] in ('member_stability','sls_member_stability','crack_formation','anchorage','detailing'):
            raise ValueError('CScore: недопустимый расчётный допуск')
        if status in ('passed','failed') and row['engine_method']=='CScore.Sp63Shear.Sp63ShearApplicability.Evaluate':
            raise ValueError('CScore: применимость не является расчётом прочности')
        if status in ('passed','failed'):
            detail=row.get('details')
            zero_capacity=(status=='failed' and ratio is None and row.get('reason')=='native_zero_capacity'
                and key[0] in ('shear_strip','shear_inclined','inclined_moment')
                and row['engine_method']=='CScore.Sp63Shear.ShearInclinedChecker.Check'
                and isinstance(detail,dict)
                and detail.get('formula')=={'shear_strip':'8.55','shear_inclined':'8.56','inclined_moment':'8.63'}[key[0]]
                and type(detail.get('allowable')) in (int,float) and detail['allowable']==0
                and type(detail.get('applied')) in (int,float) and math.isfinite(detail['applied']) and detail['applied']>0)
            if not zero_capacity and (type(ratio) not in (int,float) or not math.isfinite(ratio) or ratio<0
                    or (ratio<=1)!=(status=='passed')):
                raise ValueError('CScore: коэффициент не соответствует результату проверки')
        elif ratio is not None:
            raise ValueError('CScore: непроверенный случай не может иметь коэффициент')
        if status=='calculated' and key[0]=='crack_formation':
            d=row.get('details')
            if (not isinstance(d,dict) or type(d.get('cracked')) is not bool
                    or type(d.get('mcrc_knm')) not in (int,float)
                    or not math.isfinite(d['mcrc_knm']) or d['mcrc_knm']<0):
                raise ValueError('CScore: нет достоверного результата трещинообразования')
        if status=='calculated' and key[0]!='crack_formation':
            raise ValueError('CScore: неизвестная форма рассчитанного результата')
        if status in ('stable','unstable') and key[0] not in ('member_stability','sls_member_stability'):
            raise ValueError('CScore: неизвестный статус устойчивости')
        if status=='accounted_in_loads' and key[0] not in ('member_stability','sls_member_stability'):
            raise ValueError('CScore: недопустимое внешнее подтверждение')
        if status in ('not_evaluated','not_applicable','indeterminate','accounted_in_loads') and not row.get('reason'):
            raise ValueError('CScore: не указана причина пропуска проверки')
        failures |= status in ('failed','unstable')
        gaps |= status in ('not_evaluated','not_applicable','indeterminate') or (isinstance(row.get('details'),dict) and row['details'].get('directions_complete') is False)
    if seen!=expected:raise ValueError('CScore: возвращены не все необходимые проверки')
    return {**report,'status':'failed' if failures else 'incomplete' if gaps else 'calculated_not_verified',
            'has_failures':failures,'has_gaps':gaps}
