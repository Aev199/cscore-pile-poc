#!/usr/bin/env python3
"""Real native checks; coefficients below are declared test inputs, not design defaults."""
import argparse,copy,json,math,sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from workbench import model
from workbench.connection import default_context
import wall_rebar


def main():
    ap=argparse.ArgumentParser();ap.add_argument('--runner',type=Path,required=True)
    ap.add_argument('--output-dir',type=Path,default=Path('results/circular-shear'))
    args=ap.parse_args();args.output_dir.mkdir(parents=True,exist_ok=True)
    p=model.default_project();p.update(kind='pile',force_scale='per_member',
        materials_confirmed=True,eccentricity_confirmed=True,max_tier=1)
    p['rows']=[dict(z=z,case='U',basis='ULS',n=0,m=40,q=300,m_long=None) for z in (0,-2)]
    p['transverse'].update(mode='screen',constructive_confirmed=True)
    p['calculation_context']={**default_context(),'circular_shear':{
        'method':'vniigs_1984_user_spiral','a_mm':80,'winding_diameter_mm':670,
        'circular_efficiency':.8,'efficiency_basis':'Test input: prescribed effective area',
        'confirmed':True}}
    wires=[]
    def engine(req):
        wires.append(copy.deepcopy(req))
        return wall_rebar.invoke_engine(args.runner,req)
    def run(name,project):
        r=model.check_adopted(project,engine=engine)
        (args.output_dir/(name+'.json')).write_text(json.dumps(r,ensure_ascii=False,indent=2),encoding='utf8')
        return next(c for c in r['stations'][0]['report']['checks'] if c['check_id']=='shear_inclined')
    full=run('spiral',p)
    assert full['status']=='passed',full
    e=full['details']['equivalence']
    assert abs(e['b_m']-.708)<1e-10 and abs(e['h0_m']-.628)<1e-10,e
    assert 0<e['helix_projection']<1 and e['effective_asw_mm2']<2*78.54*.8,e
    assert full['engine_method']=='CScore.Sp63Shear.ShearInclinedChecker.Check'
    two=copy.deepcopy(p)
    two['calculation_context']['circular_shear'].update(
        method='vniigs_1984_two_legs',winding_diameter_mm=None,
        circular_efficiency=None,efficiency_basis='Accepted engineering model: two conditional legs per full turn')
    two_result=run('two_legs',two)
    te=two_result['details']['equivalence']
    assert two_result['status']=='passed',two_result
    assert te['two_conditional_legs'] and te['circular_efficiency'] is None and te['helix_projection'] is None,te
    assert abs(te['effective_asw_mm2']-2*math.pi*10**2/4)<1e-10,te
    assert wires[-1]['shear_screening']['spacing_mm']==200,wires[-1]
    bad_two=copy.deepcopy(wires[-1]);bad_two['shear_screening']['diameter_mm']=1000
    rejected=wall_rebar.invoke_engine(args.runner,bad_two)
    rejected_row=next(c for c in rejected['cases'][0]['connection_report']['checks'] if c['check_id']=='shear_inclined')
    assert rejected_row['status']=='indeterminate' and rejected_row['utilization'] is None,rejected_row
    qsw=two_result['details']['variables']['qsw']
    assert abs(qsw-170000*(te['effective_asw_mm2']/1e6)/.2)<1e-8,qsw
    closer=copy.deepcopy(two);closer['transverse']['spacing_mm']=100
    closer_result=run('two_legs_half_pitch',closer)
    assert abs(closer_result['details']['variables']['qsw']-2*qsw)<1e-8,closer_result
    assert closer_result['utilization']<=two_result['utilization'],closer_result
    two['calculation_context']['circular_shear']['confirmed']=False
    assert run('two_legs_unconfirmed',two)['status']=='not_evaluated'
    # Direct runner input cannot bypass the physical enclosure of the ring.
    bad=copy.deepcopy(wires[0]);bad['connection_context']['circular_shear']['winding_diameter_mm']=100
    raw=wall_rebar.invoke_engine(args.runner,bad)
    row=next(c for c in raw['cases'][0]['connection_report']['checks'] if c['check_id']=='shear_inclined')
    assert row['status']=='indeterminate' and row['utilization'] is None,row
    off=copy.deepcopy(p);off['calculation_context']['circular_shear']['circular_efficiency']=0
    concrete=run('concrete_only',off)
    assert concrete['status']=='failed',concrete
    assert full['utilization']<concrete['utilization'],(full,concrete)
    # Explicit concrete-only mode must work without any transverse-force inputs.
    plain=copy.deepcopy(p)
    plain['transverse'].update(mode='off',constructive_confirmed=False)
    plain['calculation_context']['circular_shear'].update(
        method='vniigs_1984_concrete_only',winding_diameter_mm=None,
        circular_efficiency=None,efficiency_basis='Declared equivalent concrete geometry')
    plain_fail=run('concrete_only_without_spiral_inputs',plain)
    assert plain_fail['status']=='failed',plain_fail
    assert abs(plain_fail['utilization']-concrete['utilization'])<1e-10
    assert plain_fail['details']['equivalence']['effective_asw_mm2']==0
    assert plain_fail['details']['variables']['Qsw']==0
    assert 'shear_screening' not in wires[-1]
    for row in plain['rows']:row['q']=100
    plain_pass=run('concrete_only_below_one',plain)
    assert plain_pass['status']=='passed',plain_pass
    plain['calculation_context']['circular_shear']['confirmed']=False
    assert run('concrete_geometry_unconfirmed',plain)['status']=='not_evaluated'
    hidden_credit=copy.deepcopy(wires[-1])
    hidden_credit['connection_context']['circular_shear']['circular_efficiency']=.8
    try:
        wall_rebar.invoke_engine(args.runner,hidden_credit)
    except RuntimeError as error:
        assert 'Invalid attributed circular spiral equivalence' in str(error),error
    else:
        raise AssertionError('Direct runner accepted hidden spiral parameters in concrete-only mode')
    for name,update in [('unconfirmed',{'confirmed':False}),('missing_efficiency',{'circular_efficiency':None})]:
        b=copy.deepcopy(p);b['calculation_context']['circular_shear'].update(update)
        row=run(name,b)
        assert row['status']=='not_evaluated' and row['utilization'] is None,row
    reverse=copy.deepcopy(p)
    for row in reverse['rows']:row.update(q=-300,m=-40)
    rev=run('reverse',reverse)
    assert abs(rev['utilization']-full['utilization'])<1e-8,(rev,full)
    fail=copy.deepcopy(p)
    for row in fail['rows']:row['q']=50000
    assert run('overload',fail)['status']=='failed'
    metre=copy.deepcopy(p);metre.update(force_scale='per_metre',spacing_m=2)
    for row in metre['rows']:row.update(m=20,q=150)
    same=run('per_metre',metre)
    assert abs(same['utilization']-full['utilization'])<1e-8,(same,full)
    print('Circular equivalent spiral: native Q, concrete-only failure, reinforcement effect, missing data, signs, overload, and unit conversion passed')


if __name__=='__main__':main()
