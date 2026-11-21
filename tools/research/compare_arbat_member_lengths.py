#!/usr/bin/env python3
"""Compare reported ea=20 mm inputs; no fitted resistance or stiffness changes."""
import argparse,json,re,sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[2]))
from tools.verify_member_eccentricity import project
from workbench import model

REFERENCE=Path('docs/research/data/arbat-member-lengths-2026-10-19.json')


def value(text,label):
    m=re.search(re.escape(label)+r'\s*([\d,]+)',text)
    return float(m[1].replace(',','.')) if m else None


def run(runner,out):
    refs=json.loads(REFERENCE.read_text(encoding='utf-8'));out.mkdir(parents=True,exist_ok=True)
    results=[]
    for ref in sorted(refs['reports'],key=lambda r:int(re.search(r'(\d+)м',r['name'])[1])):
        length=int(re.search(r'(\d+)м',ref['name'])[1]);text=ref['text']
        arbat=value(text,'Прочность по предельному моменту сечения')
        for mesh in (41,200):
            variants=[]
            for sign in (-1,1):
                p=project();p['member_load_preparation']={'mode':'source'};p['eccentricity_confirmed']=True
                p['wall']['mesh_resolution']=mesh;p['calculation_context'].update(l0_x_m=length,l0_y_m=length)
                # Statically indeterminate report: main static M=150 dominates
                # its 20 kNm minimum. Weak static M=0 becomes +/-20 kNm.
                # Input preparation for a benchmark, not a product resistance formula.
                p['rows'][0]['my']=sign*50
                p['source']={'type':'manual','manual_force_basis':'per_section',
                             'note':'Для сравнения с Арбатом: в моментах уже учтены заданные 20 мм; My в отчёте Арбата соответствует Mx формы.'}
                p['title']=f'Арбат · {length} м · заданный ea=20 мм'
                result=model.check_adopted(p,runner=runner,single_section=True)
                c={r['check_id']:r for r in result['stations'][0]['report']['checks']}
                variants.append(c)
                (out/f'L{length}_mesh{mesh}_sign{sign}.json').write_text(json.dumps(dict(project=p,result=result),ensure_ascii=False,indent=2),encoding='utf-8')
                if length==15 and mesh==41 and sign==1:
                    Path('examples/verification/member_length15_arbat20_manual.json').write_text(json.dumps(p,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
            worst=max(variants,key=lambda c:c['normal_member_strength']['utilization'])
            member=worst['member_stability']['details'];normal=worst['normal_member_strength']['utilization']
            row=dict(length_m=length,mesh_resolution=mesh,ea_report_mm=20,
                arbat_normal=arbat,cscore_normal=normal,normal_delta_percent=(normal/arbat-1)*100,
                arbat_concrete_strain=value(text,'Деформации в сжатом бетоне'),
                arbat_rebar_strain=value(text,'Деформации в растянутой арматуре'),
                arbat_axial_deflection=value(text,'Продольная сила при учете прогиба при гибкости L0/i>14'),
                cscore_n_over_ncr_y=1000/member['ncr_y_kn'] if member['ncr_y_kn'] else None,
                eta_x=member['eta_x'],eta_y=member['eta_y'],ncr_y_kn=member['ncr_y_kn'],
                shear_strip=worst['shear_strip']['utilization'],shear_inclined=worst['shear_inclined']['utilization'],
                normal_status=worst['normal_member_strength']['status'],
                all_report_coefficients_not_equivalent_to_cscore_capacity=True)
            results.append(row);print(json.dumps(row),flush=True)
    (out/'summary.json').write_text(json.dumps(results,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--runner',type=Path,required=True)
    p.add_argument('--out',type=Path,default=Path('results/arbat-member-equal-inputs'))
    a=p.parse_args();run(a.runner.resolve(),a.out)
