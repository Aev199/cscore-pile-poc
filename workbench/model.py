"""Depth-wise selection. No concrete/steel constitutive equations live here."""
from __future__ import annotations
import copy
import csv
import io
import json
import math
from pathlib import Path

import pile_rebar as pile
import wall_rebar as wall

ROOT=Path(__file__).resolve().parents[1]
FIELDS=['z','case','basis','n','m','q','m_long']


def default_project():
    # Templates are packaged alongside the app in both source and release.
    ref=ROOT/'examples/verification/P800_sls_preselection_example.json'
    if not ref.exists():ref=ROOT/'verification/P800_sls_preselection_example.json'
    b=json.loads(ref.read_text(encoding='utf-8'))
    p={'version':1,'title':'Новый расчёт','kind':'wall','force_scale':'per_metre','spacing_m':1.5,
       'moment_sign':1,'materials_confirmed':False,'eccentricity_confirmed':False,
       'background':[4,4],'pile_background':8,'diameters':[25,25],'pile_diameter':25,
       'max_tier':3,'wall':{'panel_width_mm':3000,'thickness_mm':800,'cage_count':3,
         'cage_width_mm':900,'end_cover_mm':50,'cover_mm':60,'ties_diameter_mm':10,
         'min_clear_spacing_mm':25,'min_cage_gap_mm':50,'mesh_resolution':41},
       'pile':{'diameter_mm':800,'cover_mm':60,'ties_diameter_mm':10,
         'min_clear_spacing_mm':25,'mesh_resolution':41,'polygon_sides':96},
       'materials':b['materials'],'sls_settings':{k:v for k,v in b['sls_probe'].items() if k!='loads'},
       'rows':[],'zones':[],'source':{'type':'manual','note':'Иллюстративные данные; замените своими'}}
    for i,z in enumerate([0,-2,-4,-6,-8,-10,-12]):
        m=[40,150,290,420,310,160,30][i]
        for case,basis,n,mult in [('Выемка 1','ULS',-100, .7),('Выемка 2','ULS',-180,1),('Эксплуатация','SLS',-130,.8)]:
            p['rows'].append({'z':z,'case':case,'basis':basis,'n':n,'m':m*mult,
                              'q':[30,60,50,0,-50,-60,-30][i], 'm_long':m*.6 if basis=='SLS' else None})
    return p


def number(v,name):
    if isinstance(v,str):v=float(v.strip().replace(',','.'))
    return pile.finite_number(v,name)


def validate_rows(rows):
    if not isinstance(rows,list) or not 1<=len(rows)<=3000:raise ValueError('Требуется 1–3000 строк усилий')
    out=[]
    for i,r in enumerate(rows):
        if not isinstance(r,dict) or set(r)-set(FIELDS):raise ValueError(f'Строка {i+1}: неверные столбцы')
        if r.get('basis') not in ('ULS','SLS'):raise ValueError(f'Строка {i+1}: назначьте ULS или SLS')
        if not isinstance(r.get('case'),str) or not r['case'].strip():raise ValueError('Не задано сочетание/фаза')
        a={k:number(r.get(k),k) for k in ('z','n','m','q')}
        a.update(case=r['case'].strip(),basis=r['basis'])
        a['m_long']=number(r.get('m_long'),'m_long') if r['basis']=='SLS' else None
        out.append(a)
    return out


def parse_table(text):
    lines=text.strip().splitlines()
    if not lines:raise ValueError('Таблица пуста')
    delimiter='\t' if '\t' in lines[0] else ';'
    rows=list(csv.reader(io.StringIO(text.strip()),delimiter=delimiter))
    if [c.strip().lower() for c in rows[0]]==FIELDS:rows=rows[1:]
    parsed=[]
    for r in rows:
        if not any(x.strip() for x in r):continue
        if len(r)!=7:raise ValueError('Нужно 7 столбцов: z;case;basis;n;m;q;m_long')
        parsed.append(dict(zip(FIELDS,[s.strip() for s in r])))
    return validate_rows(parsed)


def normalized_rows(p):
    rows=validate_rows(p.get('rows'))
    if p.get('kind') not in ('wall','pile'):raise ValueError('Выберите стену или БНС')
    if p.get('force_scale') not in ('per_metre','per_member'):raise ValueError('Не задан масштаб усилий')
    if p['kind']=='wall' and p['force_scale']!='per_metre':raise ValueError('Для стены нужны усилия на метр')
    factor=1.
    if p['kind']=='pile' and p['force_scale']=='per_metre':factor=pile.positive(p.get('spacing_m'),'Шаг свай')
    sign=p.get('moment_sign')
    if type(sign) is not int or sign not in (-1,1):raise ValueError('Направление момента: +1 или -1')
    for r in rows:
        for key in ('n','m','q','m_long'):
            if r[key] is not None:r[key]=number(r[key]*factor*(sign if key in ('m','m_long') else 1),key)
    return rows


def counts(p,tiers):
    if p['kind']=='pile':return [p['pile_background']*tiers[0]]
    return [(p['background'][i]-1)*tiers[i]+1 for i in range(2)]


def validate_project(p):
    if not isinstance(p,dict) or p.get('version')!=1:raise ValueError('Неподдерживаемый формат проекта')
    rows=normalized_rows(p)
    wall.integer(p.get('max_tier'),1,3,'Ступеней усиления')
    if p['kind']=='wall':
        for x in p.get('background',[]):wall.integer(x,2,20,'Стержней фона')
        if len(p.get('background',[]))!=2 or len(p.get('diameters',[]))!=2:raise ValueError('Нужны две грани')
        for x in p['diameters']:pile.positive(x,'Диаметр')
    else:
        wall.integer(p.get('pile_background'),6,32,'Стержней кольца')
        pile.positive(p.get('pile_diameter'),'Диаметр стержня')
    stations={r['z'] for r in rows}
    if len(stations)<2 or len(stations)>200:raise ValueError('Нужно 2–200 различных отметок')
    groups={}
    for r in rows:groups.setdefault((r['case'],r['basis']),set()).add(r['z'])
    for key,zs in groups.items():
        if zs!=stations:raise ValueError(f'{key[0]} ({key[1]}): набор отметок отличается. Согласуйте точки; пропуски не интерполируются.')
    if not any(r['basis']=='ULS' for r in rows):raise ValueError('Нужны расчётные усилия ULS')
    validate_zones(p.get('zones',[]),max(stations),min(stations))
    # Validate engine inputs even before starting the worker.
    section=section_input(p,[r for r in rows if r['z']==max(stations)])
    (wall.validate_input if p['kind']=='wall' else pile.validate_input)(section)
    return rows


def section_input(p,rows,tiers=None):
    req={'schema_version':1,'materials':copy.deepcopy(p['materials']),
         'eccentricity':{'mode':'already_in_loads' if p.get('eccentricity_confirmed') else 'research_none'},
         'force_basis':{'uls':'design','sls':'normative'},'loads':[]}
    u=[r for r in rows if r['basis']=='ULS']; s=[r for r in rows if r['basis']=='SLS']
    if p['kind']=='wall':
        req['wall']=copy.deepcopy(p['wall'])
        req['search']={f:{'bar_counts':[counts(p,tiers)[i]] if tiers else
                         [(p['background'][i]-1)*t+1 for t in range(1,p['max_tier']+1)],
                         'bar_diameters_mm':[p['diameters'][i]]} for i,f in enumerate(wall.FACES)}
        req['loads']=[{'id':f"{r['case']} #{i+1}",'n_kn_per_m':r['n'],'mx_knm_per_m':r['m']} for i,r in enumerate(u)]
    else:
        req['pile']=copy.deepcopy(p['pile'])
        req['search']={'bar_counts':[counts(p,tiers)[0]] if tiers else
                      [p['pile_background']*t for t in range(1,p['max_tier']+1)],
                       'bar_diameters_mm':[p['pile_diameter']],'rotation_deg':[0]}
        req['loads']=[{'id':f"{r['case']} #{i+1}",'n_kn':r['n'],'mx_knm':r['m'],'my_knm':0} for i,r in enumerate(u)]
    if s:
        req['sls_probe']=copy.deepcopy(p['sls_settings'])
        if p['kind']=='wall':
            req['sls_probe']['loads']=[{'id':f"{r['case']} #{i+1}",'n_kn_per_m':r['n'],
                'mx_total_knm_per_m':r['m'],'mx_long_knm_per_m':r['m_long']} for i,r in enumerate(s)]
        else:
            req['sls_probe']['loads']=[{'id':f"{r['case']} #{i+1}",'n_kn':r['n'],
                'mx_total_knm':r['m'],'mx_long_knm':r['m_long'],'my_total_knm':0,'my_long_knm':0} for i,r in enumerate(s)]
    return req


def evaluate(p,rows,engine,tiers=None):
    req=section_input(p,rows,tiers)
    if p['kind']=='wall':cases=wall.calculate(req,engine)['cases']
    else:
        pile.validate_input(req)
        candidates,_=pile.make_candidates(req)
        if not candidates:return []
        wire=pile.build_engine_request(req,candidates)
        cases=pile.parse_runner_results(wire,engine(wire))
    for c in cases:
        if p['kind']=='wall':
            c['tiers']=[(c[f]['count_per_cage']-1)//(p['background'][i]-1) for i,f in enumerate(wall.FACES)]
            c['areas']=[c[f]['area_mm2_per_m']/100 for f in wall.FACES]
        else:c.update(tiers=[c['bar_count']//p['pile_background'],1],areas=[c['steel_area_mm2']/100])
        c['accepted']=c['status']=='pass_uls' and ('sls_probe' not in req or c.get('sls_status')=='preliminary_below_limit')
        c['accepted']=c['accepted'] and bool(p.get('eccentricity_confirmed'))
    return cases


def suggest_zones(stations):
    if any(s['tiers'] is None for s in stations):return []
    zones=[]
    points=sorted(stations,key=lambda s:-s['z'])
    for a,b in zip(points,points[1:]):
        t=[max(x,y) for x,y in zip(a['tiers'],b['tiers'])]
        if t==[1,1]:continue
        if zones and zones[-1]['bottom']==a['z'] and zones[-1]['tiers']==t:zones[-1]['bottom']=b['z']
        else:zones.append({'top':a['z'],'bottom':b['z'],'tiers':t})
    return zones


def validate_zones(zones,top,bottom):
    if not isinstance(zones,list) or len(zones)>200:raise ValueError('Не более 200 участков')
    for z in zones:
        if not isinstance(z,dict) or set(z)!={'top','bottom','tiers'}:raise ValueError('Неверный участок усиления')
        a=number(z['top'],'Верх');b=number(z['bottom'],'Низ')
        if not bottom<=b<a<=top:raise ValueError('Участок должен находиться в пределах конструкции, верх выше низа')
        if not isinstance(z['tiers'],list) or len(z['tiers'])!=2:raise ValueError('Нужны уровни двух граней')
        for t in z['tiers']:wall.integer(t,1,3,'Уровень армирования')
    ordered=sorted(zones,key=lambda z:-z['top'])
    if any(b['top']>a['bottom'] for a,b in zip(ordered,ordered[1:])):raise ValueError('Участки усиления перекрываются')


def tiers_at(z,zones):
    active=[x['tiers'] for x in zones if x['bottom']<=z<=x['top']]
    return [max([1]+[x[i] for x in active]) for i in range(2)]


def design(p,engine=None,progress=lambda v:None,recheck=False,runner=None):
    rows=validate_project(p)
    if p.get('materials_confirmed') is not True:raise ValueError('Проверьте материалы и подтвердите их перед расчётом')
    engine=engine or (lambda req:wall.invoke_engine(runner,req))
    memo={}
    def cached(req):
        key=json.dumps(req,sort_keys=True,allow_nan=False)
        if key not in memo:memo[key]=engine(req)
        return copy.deepcopy(memo[key])
    zs=sorted({r['z'] for r in rows},reverse=True)
    by_z={z:[r for r in rows if r['z']==z] for z in zs}
    stations=[]
    if not recheck:
        for i,z in enumerate(zs):
            progress(f'Подбор: отметка {z:g} м ({i+1}/{len(zs)})')
            cases=evaluate(p,by_z[z],cached)
            selected=next((c for c in cases if c['accepted']),None)
            stations.append({'z':z,'tiers':selected['tiers'] if selected else None,
                             'required_areas':selected['areas'] if selected else None,
                             'selected':selected,'candidates':cases})
        zones=suggest_zones(stations)
    else:
        zones=copy.deepcopy(p.get('zones',[]))
        stations=[{'z':z,'tiers':None,'required_areas':None,'selected':None,'candidates':[]} for z in zs]
    validate_zones(zones,max(zs),min(zs))
    for i,s in enumerate(stations):
        progress(f'Проверка принятого: отметка {s["z"]:g} м ({i+1}/{len(zs)})')
        t=tiers_at(s['z'],zones)
        cases=evaluate(p,by_z[s['z']],cached,t)
        s['adopted']=cases[0] if cases else None
        s['checked']=bool(cases and cases[0]['accepted'])
    return {'stations':stations,'zones':zones,'rows':rows,'all_checked':all(s['checked'] for s in stations),
            'design_status':'NOT_VERIFIED','scope':'discrete_sections_only_no_shear_no_anchorage',
            'has_sls':any(r['basis']=='SLS' for r in rows),'recheck':recheck}
