"""Prepare member forces using CScore only; no eccentricity formulas in Python."""
import json
import math
from pathlib import Path
import subprocess


def validate(value):
    if value is None:return {'mode':'source'}
    if not isinstance(value,dict) or set(value)-{'mode','length_m','structural_scheme'}:
        raise ValueError('Некорректные условия случайного эксцентриситета')
    if value.get('mode') not in ('source','sp63'):raise ValueError('Выберите учёт эксцентриситета')
    if value['mode']=='sp63':
        length=value.get('length_m')
        if type(length) not in (int,float) or not math.isfinite(length) or length<=0:
            raise ValueError('Для эксцентриситета задайте фактическую длину элемента')
        if value.get('structural_scheme') not in ('statically_determinate','statically_indeterminate'):
            raise ValueError('Укажите статическую определимость схемы')
    return value


def prepare(request,settings,runner):
    """CScore returns all sign variants; keep Q unchanged and record provenance."""
    if runner is None:raise ValueError('Для подготовки эксцентриситета нужен исполняемый CScore')
    rect=request.get('research_rectangle')
    hx=(rect['height_mm'] if rect else request['pile']['diameter_mm'])/1000
    hy=(rect['width_mm'] if rect else request['pile']['diameter_mm'])/1000
    original={};loads=[]
    for kind,rows in [('ULS',request['loads']),('SLS',request.get('sls_probe',{}).get('loads',[]))]:
        for row in rows:
            source=kind+':'+row['id'];original[source]=(kind,row)
            loads.append(dict(id=source,n_kn=row['n_kn'],
                mx_total_knm=row['mx_knm'] if kind=='ULS' else row['mx_total_knm'],
                my_total_knm=row['my_knm'] if kind=='ULS' else row['my_total_knm'],
                mx_long_knm=row['mx_knm'] if kind=='ULS' else row['mx_long_knm'],
                my_long_knm=row['my_knm'] if kind=='ULS' else row['my_long_knm']))
    payload=dict(length_m=settings['length_m'],height_x_m=hx,height_y_m=hy,
                 structural_scheme=settings['structural_scheme'],loads=loads)
    runner=Path(runner);cmd=['dotnet',str(runner)] if runner.suffix.lower()=='.dll' else [str(runner)]
    proc=subprocess.run(cmd+['--prepare-member-loads'],input=json.dumps(payload,allow_nan=False),
                        capture_output=True,text=True,encoding='utf-8',timeout=120)
    if proc.returncode:raise ValueError('CScore: не удалось подготовить эксцентриситет: '+proc.stderr[:1000])
    result=json.loads(proc.stdout);prepared={'ULS':[],'SLS':[]};seen=set();covered=set()
    if result.get('method')!='CScore.Sp63.Normal.Sp63MemberContext':raise ValueError('Нет подтверждённой подготовки CScore')
    for row in result.get('loads',[]):
        source=row.get('source_id');name=row.get('id')
        if source not in original or (source,name) in seen or not isinstance(name,str):
            raise ValueError('CScore: некорректные варианты эксцентриситета')
        seen.add((source,name));covered.add(source);kind,src=original[source]
        for key in ('n_kn','mx_total_knm','my_total_knm','mx_long_knm','my_long_knm'):
            if type(row.get(key)) not in (int,float) or not math.isfinite(row[key]):
                raise ValueError('CScore: неконечные подготовленные усилия')
        if row['n_kn']!=src['n_kn']:raise ValueError('CScore: изменилась осевая сила')
        dst={**src,'id':name,'n_kn':row['n_kn']}
        if kind=='ULS':dst.update(mx_knm=row['mx_total_knm'],my_knm=row['my_total_knm'],
                                  source_mx_knm=src['mx_knm'],source_my_knm=src['my_knm'])
        else:dst.update({key:row[key] for key in ('mx_total_knm','my_total_knm','mx_long_knm','my_long_knm')})
        prepared[kind].append(dst)
    if covered!=set(original):raise ValueError('CScore: пропущены сочетания при подготовке эксцентриситета')
    request['loads']=prepared['ULS']
    if 'sls_probe' in request:request['sls_probe']['loads']=prepared['SLS']
    request['connection_context']['eccentricity_in_loads']=True
    return {'settings':settings,'geometry':{'height_x_m':hx,'height_y_m':hy},**result}
