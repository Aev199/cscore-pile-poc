#!/usr/bin/env python3
"""Real CScore regression: two-plane eccentricity, source Q and long-member failure."""
import argparse,copy,json,math,sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from workbench import model
from workbench.connection import default_context
from workbench.member_loads import prepare


def project():
    p=model.default_project();p.update(materials_confirmed=True,eccentricity_confirmed=False,max_tier=1)
    p['wall'].update(panel_width_mm=400,thickness_mm=600,cage_count=1,cage_width_mm=316,
                     end_cover_mm=42,cover_mm=42,ties_diameter_mm=8)
    p['background']=[4,4];p['diameters']=[25,25]
    p['transverse'].update(mode='screen',diameter_mm=8,constructive_confirmed=True)
    p['rows']=[dict(z=0,case='U',basis='ULS',n=-2500,m=375,my=0,q=250,m_long=None)]
    p['calculation_context']={**default_context(),'second_order_mode':'cscore_member',
                              'l0_x_m':15,'l0_y_m':15}
    p['member_load_preparation']={'mode':'sp63','length_m':15,'structural_scheme':'statically_indeterminate'}
    return p


def run(runner,out):
    out.mkdir(parents=True,exist_ok=True);summary=[]
    arbat={1:.332,10:.388,12:.443,15:6.483}
    for length in (1,10,12,15):
        p=project();p['member_load_preparation']['length_m']=length
        p['calculation_context'].update(l0_x_m=length,l0_y_m=length)
        r=model.check_adopted(p,runner=runner,single_section=True);s=r['stations'][0]
        prep=s['member_load_preparation'];assert len(s['loads'])==2
        checks=s['report']['checks'];normal=[c for c in checks if c['check_id']=='normal_member_strength']
        assert len(normal)==2 and all(c['utilization'] is not None for c in normal),normal
        assert {math.copysign(1,l['my_knm']) for l in s['loads']}=={-1,1}
        for check in (c for c in checks if c['check_id'] in ('shear_strip','shear_inclined')):
            assert check['status']=='passed',check
            f=check['details']['force_context'];assert f['q_kn']==100 and f['mx_knm']==150
            assert f['eta_applied_to_q'] is False
        use=max(c['utilization'] for c in normal)
        if length==15:
            assert prep['ea_x_mm']==25 and prep['ea_y_mm']==25
            assert use>7 and all(c['status']=='failed' for c in normal),normal
            assert all(c['details']['ncr_y_kn'] is not None for c in checks if c['check_id']=='member_stability')
        row=dict(length_m=length,ea_x_mm=prep['ea_x_mm'],ea_y_mm=prep['ea_y_mm'],
                 cscore_normal=use,arbat_recorded_normal=arbat[length],
                 reference_inputs_not_fully_reestablished=True)
        summary.append(row);print(json.dumps(row),flush=True)
        (out/f'L{length}.json').write_text(json.dumps(dict(project=p,result=r),ensure_ascii=False,indent=2),encoding='utf-8')
    # Explicit 20 mm weak-plane eccentricity: no normative auto substitution.
    p=project();p['member_load_preparation']={'mode':'source'};p['eccentricity_confirmed']=True
    p['rows'][0]['my']=50 # 20 kNm on a 0.4 m panel, corresponding to N=1000 kN and e=20 mm.
    r=model.check_adopted(p,runner=runner,single_section=True)
    normal=next(c for c in r['stations'][0]['report']['checks'] if c['check_id']=='normal_member_strength')
    assert math.isclose(normal['utilization'],6.346466288,rel_tol=1e-6),normal
    summary.append(dict(case='L15_explicit_20mm_weak_plane',cscore_normal=normal['utilization'],arbat_recorded_normal=6.483,
                        reference_inputs_not_fully_reestablished=True))
    (out/'L15_explicit20.json').write_text(json.dumps(dict(project=p,result=r),ensure_ascii=False,indent=2),encoding='utf-8')
    # Actual length controls ea; effective lengths independently control eta.
    p=project();p['member_load_preparation']['length_m']=12
    r=model.check_adopted(p,runner=runner,single_section=True)
    prep=r['stations'][0]['member_load_preparation'];assert prep['ea_y_mm']==20
    for c in r['stations'][0]['report']['checks']:
        if c['check_id']=='member_stability':assert c['details']['l0_y_m']==15
    (out/'summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
    # SLS: service forces and long/total moments stay as given (no ea, SP63 8.2).
    p=project();p['member_load_preparation']['length_m']=6
    p['calculation_context'].update(l0_x_m=6,l0_y_m=6)
    p['rows'].append(dict(z=0,case='S',basis='SLS',n=-250,m=375,my=0,q=0,m_long=250,my_long=0))
    r=model.check_adopted(p,runner=runner,single_section=True);station=r['stations'][0]
    widths=[c for c in station['report']['checks'] if c['check_id'].startswith('crack_width')]
    assert len(widths)==2 and all(c['utilization'] is not None and c['utilization']>0 for c in widths)
    assert all(c['status']=='stable' for c in station['report']['checks'] if c['check_id']=='sls_member_stability')
    assert [(l['mx_total_knm'],l['mx_long_knm'],l['my_total_knm']) for l in station['sls_loads']]==[(150,100,0)]
    (out/'SLS_L6.json').write_text(json.dumps(dict(project=p,result=r),ensure_ascii=False,indent=2),encoding='utf-8')
    # Circular Q uses external actions, not the added normal-section My.
    p=project();p.update(kind='pile',force_scale='per_member',pile_background=12)
    p['pile']['ties_diameter_mm']=8
    p['rows']=[dict(z=0,case='U',basis='ULS',n=-1000,m=150,my=0,q=100,m_long=None)]
    p['member_load_preparation']['length_m']=6;p['calculation_context'].update(l0_x_m=6,l0_y_m=6)
    p['calculation_context']['circular_shear']=dict(method='vniigs_1984_two_legs',a_mm=85,
        winding_diameter_mm=None,circular_efficiency=None,efficiency_basis='Accepted two conditional legs',confirmed=True)
    r=model.check_adopted(p,runner=runner,single_section=True)
    for c in r['stations'][0]['report']['checks']:
        if c['check_id'] in ('shear_strip','shear_inclined'):assert c['status'] in ('passed','failed'),c
    (out/'pile_L6.json').write_text(json.dumps(dict(project=p,result=r),ensure_ascii=False,indent=2),encoding='utf-8')
    # Exercise the upstream preparation boundary without a resistance solve.
    settings={'mode':'sp63','length_m':12,'structural_scheme':'statically_determinate'}
    def wire(n,mx,my=0):
        return {'research_rectangle':{'height_mm':600,'width_mm':400},'loads':[
            {'id':'U','n_kn':n,'mx_knm':mx,'my_knm':my,'q_kn':100}], 'connection_context':{}}
    w=wire(-1000,-150);prepare(w,settings,runner)
    assert {round(l['mx_knm'],8) for l in w['loads']}=={-170}
    assert {l['my_knm'] for l in w['loads']}=={-20,20}
    w=wire(-1000,0);prepare(w,settings,runner)
    assert len(w['loads'])==4
    assert {(l['mx_knm'],l['my_knm']) for l in w['loads']}=={(-20,-20),(-20,20),(20,-20),(20,20)}
    for axial in (0,100):
        w=wire(axial,150);prepare(w,settings,runner)
        assert len(w['loads'])==1 and w['loads'][0]['mx_knm']==150 and w['loads'][0]['my_knm']==0
    w=wire(-1000,100)
    w['sls_probe']={'loads':[{'id':'S','n_kn':-500,'mx_total_knm':100,'my_total_knm':0,'mx_long_knm':50,'my_long_knm':0}]}
    sls=copy.deepcopy(w['sls_probe']);prepare(w,settings,runner)
    assert w['sls_probe']==sls
    print('Member eccentricity: both zero-axis signs, independent lengths, preserved Q and explicit source moments passed')


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--runner',type=Path,required=True)
    p.add_argument('--out',type=Path,default=Path('results/member-eccentricity'))
    a=p.parse_args();run(a.runner.resolve(),a.out)
