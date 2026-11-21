"""A deliberately limited 1:1 (mm) DXF underlay of wall reinforcement zones.

This is not a fabricated reinforcement cage drawing: it contains a wall
elevation, physical cage bounding boxes, zone guides and a readable zone
schedule. It NEVER models actual individual bars, hooks or anchorage.
"""
from __future__ import annotations
import math


def _number(value):
    if not math.isfinite(value):
        raise ValueError('DXF: нечисловая координата')
    return f'{value:.6f}'.rstrip('0').rstrip('.') if value != 0 else '0'


def _dxf_text(value):
    # R2000 DXF plain ASCII with AutoCAD Unicode escape sequences.
    return ''.join(f'\\U+{ord(ch):04X}' if ord(ch) > 127 else ch
                   for ch in str(value).replace('\r', ' ').replace('\n', ' '))


class _Dxf:
    def __init__(self):
        self.entities = []

    def add(self, kind, *groups):
        pairs = [('0', kind), *[(str(code), str(value)) for code, value in groups]]
        self.entities.extend(pairs)

    def line(self, layer, x1, y1, x2, y2):
        self.add('LINE', (8, layer), (10, _number(x1)), (20, _number(y1)),
                 (30, '0'), (11, _number(x2)), (21, _number(y2)), (31, '0'))

    def rect(self, layer, x0, y0, x1, y1):
        self.add('LWPOLYLINE', (8, layer), (90, 4), (70, 1),
                 (10, _number(x0)), (20, _number(y0)),
                 (10, _number(x1)), (20, _number(y0)),
                 (10, _number(x1)), (20, _number(y1)),
                 (10, _number(x0)), (20, _number(y1)))

    def text(self, layer, x, y, value, height=105):
        self.add('TEXT', (8, layer), (10, _number(x)), (20, _number(y)),
                 (30, '0'), (40, _number(height)),
                 (1, _dxf_text(value)), (7, 'Standard'))

    def finish(self):
        header=[('0','SECTION'),('2','HEADER'),
                ('9','$ACADVER'),('1','AC1015'),
                ('9','$INSUNITS'),('70','4'),
                ('0','ENDSEC')]
        # Explicit layer, line-type and text-style declarations are more
        # interoperable than relying on each DWG editor to invent defaults.
        tables=[('0','SECTION'),('2','TABLES'),
                ('0','TABLE'),('2','LTYPE'),('70','1'),
                ('0','LTYPE'),('2','CONTINUOUS'),('70','0'),
                ('3','Solid line'),('72','65'),('73','0'),('40','0'),
                ('0','ENDTAB'),
                ('0','TABLE'),('2','LAYER'),('70','6')]
        for name,color in (('KJ_WALL',7),('KJ_CAGE',4),('KJ_ZONE',8),
                           ('KJ_TABLE',8),('KJ_TEXT',7),('KJ_NOTES',1)):
            tables.extend((('0','LAYER'),('2',name),('70','0'),
                           ('62',str(color)),('6','CONTINUOUS')))
        tables.extend((('0','ENDTAB'),('0','TABLE'),('2','STYLE'),('70','1'),
                       ('0','STYLE'),('2','Standard'),('70','0'),
                       ('40','0'),('41','1'),('50','0'),('71','0'),
                       ('42','2.5'),('3','txt'),('4',''),
                       ('0','ENDTAB'),('0','ENDSEC')))
        blocks=[('0','SECTION'),('2','BLOCKS'),('0','ENDSEC')]
        items=header+tables+blocks+[('0','SECTION'),('2','ENTITIES')]
        items+=self.entities+[('0','ENDSEC'),('0','EOF')]
        return ''.join(k+'\n'+v+'\n' for k,v in items)


def make_wall_dxf(project, schedule):
    if project.get('kind') != 'wall' or schedule.get('status') != 'DRAFT_NOT_VERIFIED':
        raise ValueError('DXF: только черновая развёртка стены в грунте')
    rows=schedule.get('rows')
    if not rows:
        raise ValueError('DXF: отсутствуют участки армирования')
    geom=project['wall']
    width=float(geom['panel_width_mm'])
    cages=int(geom['cage_count'])
    cage_width=float(geom['cage_width_mm'])
    cover=float(geom['end_cover_mm'])
    if cages not in (1,2,3) or min(width,cage_width,cover)<=0:
        raise ValueError('DXF: некорректная геометрия захватки')
    top=float(rows[0]['top_m'])
    bottom=float(rows[-1]['bottom_m'])
    height=(top-bottom)*1000
    if height<=0:
        raise ValueError('DXF: некорректная высота развёртки')
    if cages==1:
        left=(width-cage_width)/2
        positions=[left]
    else:
        gap=(width-2*cover-cages*cage_width)/(cages-1)
        if gap<0:
            raise ValueError('DXF: каркасы не размещаются по ширине захватки')
        positions=[cover+i*(cage_width+gap) for i in range(cages)]
    if positions[0]<0 or positions[-1]+cage_width>width+1e-6:
        raise ValueError('DXF: каркасы выходят за габариты захватки')

    d=_Dxf()
    d.text('KJ_TEXT',0,650,'РАЗВЕРТКА СТЕНЫ В ГРУНТЕ - ЧЕРНОВИК',180)
    d.text('KJ_TEXT',0,330,f'Захватка {width:g} мм; {cages} карк.; отметки {top:g} ... {bottom:g} м',110)
    d.rect('KJ_WALL',0,0,width,-height)
    for idx,x in enumerate(positions,1):
        d.rect('KJ_CAGE',x,0,x+cage_width,-height)
        d.text('KJ_TEXT',x+30,-220,f'К{idx}',110)

    # Elevation lines are exact to schedule interval boundaries, 1:1 mm.
    for row in rows[1:]:
        y=(float(row['top_m'])-top)*1000
        d.line('KJ_ZONE',-110,y,width+110,y)

    tx=width+650
    dx=[0,950,1900,2600,3300]
    headers=['ВЕРХ/НИЗ, м','ГРАНЬ Y-','ГРАНЬ Y+','ХОМУТ','КАРК.']
    d.text('KJ_TEXT',tx,130, 'АРМИРОВАНИЕ ПО УЧАСТКАМ',150)
    for x,label in zip(dx,headers):
        d.text('KJ_TEXT',tx+x,-140,label,100)
    row_step=310
    d.line('KJ_TABLE',tx-60,-185,tx+4040,-185)
    for i,row in enumerate(rows):
        y=-400-i*row_step
        label=f"{row['top_m']:g} ... {row['bottom_m']:g}"
        left=f"{row['face_minus_bars_per_cage']}xD{row['face_minus_diameter_mm']:g}"
        right=f"{row['face_plus_bars_per_cage']}xD{row['face_plus_diameter_mm']:g}"
        links=f"D{row['links_diameter_mm']:g}/{row['links_pitch_mm']:g}"
        for x,value in zip(dx,(label,left,right,links,str(row['cages']))):
            d.text('KJ_TEXT',tx+x,y,value,108)
        d.line('KJ_TABLE',tx-60,y-95,tx+4040,y-95)
    note_y=min(-height-480,-650-len(rows)*row_step)
    d.text('KJ_NOTES',0,note_y,'НЕ ДЛЯ ВЫПУСКА КЖ. ТОЛЬКО СХЕМА УЧАСТКОВ, ММ.',140)
    d.text('KJ_NOTES',0,note_y-230,
           'НЕ ПОКАЗАНЫ: АНКЕРОВКА, ОБРЫВЫ, ГИБЫ, СОЕДИНЕНИЯ, МАРКИ, ДЕТАЛИ ХОМУТОВ.',110)
    d.text('KJ_NOTES',0,note_y-440,
           'РАСЧЕТ Q И ДЕТАЛИРОВКА НЕ ПОДТВЕРЖДЕНЫ. ИНЖЕНЕРНАЯ ПРОВЕРКА ОБЯЗАТЕЛЬНА.',110)
    return d.finish()
