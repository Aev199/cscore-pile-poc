"""Read-only PLAXIS Output adapter; result availability checked on connection."""
import math
import threading

LOCK=threading.Lock()


def label(obj):
    for key in ('Identification','Name'):
        try:return str(getattr(obj,key).value)
        except Exception:pass
    return str(obj)


def phase_label(obj,index):
    # Identification is editable and not unique. Keep the catalog index even
    # when a user assigns the same description to several phases.
    try:name=str(obj.Name.value)
    except Exception:name='Phase'
    return f'{label(obj)} [{name} #{index}]'


def available(obj,names):
    for name in names:
        try:return getattr(obj,name),name
        except Exception:pass
    raise ValueError('PLAXIS не предоставляет: '+', '.join(names))


class OutputConnection:
    def __init__(self,port,password):
        try:from plxscripting.easy import new_server
        except ImportError as exc:
            raise ValueError('Не найден plxscripting. Запустите приложение Python, в котором доступен модуль PLAXIS Remote Scripting. Ручной ввод и вставка таблиц доступны без него.') from exc
        if type(port) is not int or not 1<=port<=65535:raise ValueError('Неверный порт Output')
        self.server,self.g=new_server('127.0.0.1',port,password=password)
        self.objects=[];self.phases=list(self.g.Phases);self.types={}
        self.info={}
        for key in ('UnitForce','UnitLength','Version'):
            try:self.info[key]=str(getattr(self.g.GeneralInfo,key).value)
            except Exception:self.info[key]='не прочитано'
        for kind,collections,type_names in (
            ('Plate',('Plates',),('Plate',)),
            ('EmbeddedBeam',('EmbeddedBeams','EmbeddedBeamRows'),('EmbeddedBeam','EmbeddedBeamRow'))):
            try:
                items,_=available(self.g,collections);rt,rt_name=available(self.g.ResultTypes,type_names)
                types={};names={}
                for key,candidates in {'x':('X',),'z':('Y',),
                        'n':('N2D',) if kind=='Plate' else ('Nx2D','N2D'),
                        'm':('M2D',),'q':('Q2D',)}.items():
                    types[key],names[key]=available(rt,candidates)
                self.types[kind]=types
                for obj in items:self.objects.append((kind,obj,names))
            except Exception:continue
        if not self.objects:raise ValueError('Нет доступных Plate/Embedded Beam с результатами X, Y, N, M, Q. Проверьте модель и API версии.')

    def catalog(self):
        return {'objects':[{'id':i,'kind':k,'name':label(o),'result_types':n}
                           for i,(k,o,n) in enumerate(self.objects)],
                'phases':[{'id':i,'name':phase_label(o,i)} for i,o in enumerate(self.phases)],
                'info':self.info,
                'note':'Для Embedded Beam v22.01 подтвердите масштаб по таблице Output. В 2023.1 Bentley изменил выдачу на один элемент.'}

    def extract(self,opts):
        object_ids=opts.get('objects',[]);phase_ids=opts.get('phases',[])
        if not object_ids or not phase_ids:raise ValueError('Выберите объекты и фазы')
        if len(set(object_ids))!=len(object_ids) or len(set(phase_ids))!=len(phase_ids):raise ValueError('Повтор выбранного объекта/фазы')
        for indices,items in ((object_ids,self.objects),(phase_ids,self.phases)):
            if any(type(i) is not int or not 0<=i<len(items) for i in indices):raise ValueError('Неверный ID выбора')
        kinds={self.objects[i][0] for i in object_ids}
        if len(kinds)!=1:raise ValueError('За один импорт выбирайте один тип элементов')
        basis=opts.get('basis');scale=opts.get('force_scale')
        if basis not in ('ULS','SLS') or scale not in ('per_metre','per_member'):raise ValueError('Подтвердите группу и масштаб усилий')
        if kinds=={'Plate'} and scale!='per_metre':raise ValueError('Plate: выберите усилия на метр')
        if opts.get('confirmed') is not True:raise ValueError('Подтвердите единицы, масштаб и направление осей')
        f={'kN':1,'N':.001,'tf':9.80665}.get(opts.get('force_unit'))
        length={'m':1,'mm':.001,'cm':.01}.get(opts.get('length_unit'))
        if f is None or length is None:raise ValueError('Выберите поддерживаемые единицы проекта')
        if basis=='SLS' and opts.get('fully_sustained') is not True:
            raise ValueError('Для импорта SLS подтвердите полностью длительные усилия; иначе задайте Mдлит вручную после импорта как ULS')
        rows=[];xs=[]
        for pi in phase_ids:
            phase=self.phases[pi]
            for oi in object_ids:
                kind,obj,_=self.objects[oi]
                arrays={k:list(self.g.getresults(obj,phase,t,'node')) for k,t in self.types[kind].items()}
                if len({len(a) for a in arrays.values()})!=1 or not arrays['z']:raise ValueError('PLAXIS вернул несовместимые/пустые массивы')
                for i in range(len(arrays['z'])):
                    vals={k:float(v[i]) for k,v in arrays.items()}
                    if not all(math.isfinite(v) for v in vals.values()):raise ValueError('Неконечное значение из PLAXIS')
                    xs.append(vals['x']*length)
                    nf=f/length if scale=='per_metre' else f
                    mf=f if scale=='per_metre' else f*length
                    rows.append({'z':vals['z']*length,'case':phase_label(phase,pi),'basis':basis,
                                 'n':vals['n']*nf,'m':vals['m']*mf,'q':vals['q']*nf,
                                 'm_long':vals['m']*mf if basis=='SLS' else None})
        if max(xs)-min(xs)>.001:raise ValueError('Выбраны разные вертикали или наклонная конструкция. Допуск по X — 1 мм.')
        return {'rows':rows,'force_scale':scale,'source':{'type':'plaxis_output','info':self.info,
                'objects':[label(self.objects[i][1]) for i in object_ids],
                'phases':[phase_label(self.phases[i],i) for i in phase_ids],
                'force_unit':opts['force_unit'],'length_unit':opts['length_unit'],
                'force_scale':scale,'conversion_confirmed':True}}
