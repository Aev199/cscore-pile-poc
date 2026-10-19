#!/usr/bin/env python3
"""Real CScore checks of adopted sections. No mock numerical results."""
import argparse,copy,json,sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from workbench import model
from workbench.connection import default_context


def main():
    a=argparse.ArgumentParser();a.add_argument('--runner',type=Path,required=True)
    a.add_argument('--output-dir',type=Path,default=Path('results/connection'))
    args=a.parse_args();args.output_dir.mkdir(parents=True,exist_ok=True)
    def run(name,p):
        r=model.check_adopted(p,runner=args.runner)
        (args.output_dir/(name+'.json')).write_text(json.dumps(r,ensure_ascii=False,indent=2),encoding='utf-8')
        return r
    def check(r,key,case=None):
        return next(c for c in r['stations'][0]['report']['checks'] if c['check_id']==key and (case is None or c['case_id']==case))
    p=model.default_project();p.update(materials_confirmed=True,eccentricity_confirmed=True,max_tier=1)
    p['rows']=[dict(z=z,case=case,basis=basis,n=n,m=m,q=q,m_long=ml)
        for z in (0,-2) for case,basis,n,m,q,ml in [('U','ULS',-100,40,20,None),('S','SLS',-80,30,15,30)]]
    p['transverse'].update(mode='screen',constructive_confirmed=True)
    base=run('wall_missing_context',p)
    assert check(base,'normal_strength')['status']=='passed'
    assert check(base,'member_stability')['status']=='not_evaluated'
    assert check(base,'inclined_moment')['status']=='not_evaluated'
    assert check(base,'crack_formation')['status']=='calculated'
    assert check(base,'crack_width_long')['status']=='passed'
    assert check(base,'crack_width_short')['status']=='passed'
    p['calculation_context']={**default_context(),'second_order_mode':'already_in_loads',
        'inclined_moment_requested':True,'anchorage_confirmed':True,'anchorage_factor':1,
        'sections':[dict(z=z,support_distance_positive_m=3,support_distance_negative_m=3) for z in (0,-2)]}
    moment=run('wall_native_inclined_moment',p)
    m=check(moment,'inclined_moment')
    assert m['status'] in ('passed','failed'),m
    assert m['details']['formula']=='8.63',m
    assert check(moment,'member_stability')['status']=='accounted_in_loads'
    assert check(moment,'anchorage')['status']=='not_evaluated'
    no_support=copy.deepcopy(p)
    no_support['calculation_context']['sections']=[dict(z=z,support_distance_positive_m=0,support_distance_negative_m=0) for z in (0,-2)]
    no_support_result=run('wall_zero_support_moment_not_evaluated',no_support)
    assert check(no_support_result,'inclined_moment')['status']=='not_evaluated'
    q=copy.deepcopy(p)
    for row in q['rows']:
        if row['basis']=='ULS':row['q']=50000
    failure=run('wall_Q_fail',q)
    assert check(failure,'shear_strip')['status']=='failed'
    assert failure['status']=='failed' and not failure['verified_for_design']
    # At high compression phiN reaches zero: a native zero capacity is a known
    # failure, even though utilization is +Infinity and cannot be JSON-encoded.
    zero_capacity=copy.deepcopy(p)
    for row in zero_capacity['rows']:
        if row['basis']=='ULS': row.update(n=-500000,q=20)
    zero_result=run('wall_zero_native_strip_capacity',zero_capacity)
    assert check(zero_result,'shear_strip')['status']=='failed',check(zero_result,'shear_strip')
    assert check(zero_result,'shear_strip')['reason']=='native_zero_capacity'
    directional=copy.deepcopy(p)
    for row in directional['rows']:
        if row['basis']=='ULS':row['q']=500
    directional['calculation_context']['sections']=[dict(z=z,support_distance_positive_m=.1,
        support_distance_negative_m=3) for z in (0,-2)]
    directional_result=run('wall_known_directional_failure',directional)
    assert check(directional_result,'shear_inclined')['status']=='failed'
    assert directional_result['status']=='failed'
    # Second-order corrections and strength are produced by native RodEtaWiring/NDM.
    member=copy.deepcopy(p)
    member['calculation_context'].update(second_order_mode='cscore_member',l0_x_m=6,l0_y_m=6,psi=.5)
    eta=run('wall_native_eta',member)
    assert check(eta,'member_stability')['status']=='stable',check(eta,'member_stability')
    assert check(eta,'normal_member_strength')['status']=='passed'
    assert check(eta,'crack_width_short')['status']=='passed'
    assert check(eta,'crack_width_short')['basis']=='native_member_CrackWidthEta.ScaleLongTotal'
    for key in ('shear_strip','shear_inclined'):
        actual,source=check(eta,key),check(moment,key)
        assert actual['status']==source['status'],actual
        assert abs(actual['utilization']-source['utilization'])<1e-10,(actual,source)
        forces=actual['details']['force_context']
        assert forces['q_kn']==60 and forces['n_kn']==-300,forces
        assert forces['eta_applied_to_q'] is False,forces
        assert forces['normal_mx_eff_knm']>forces['mx_knm'],forces
    assert check(eta,'inclined_moment')['status']=='not_evaluated'
    assert check(eta,'inclined_moment')['reason']=='inclined_moment_external_demand_required_with_eta'
    member_q_overload=copy.deepcopy(member)
    for row in member_q_overload['rows']:
        if row['basis']=='ULS':row['q']=50000
    member_q_failure=run('wall_native_eta_Q_failure',member_q_overload)
    assert check(member_q_failure,'member_stability')['details']['eta_x']>1
    assert check(member_q_failure,'shear_strip')['status']=='failed'
    assert member_q_failure['status']=='failed'
    # Native eta=1 leaves the original N/M/Q unchanged: no profile reconstruction is needed.
    stocky=copy.deepcopy(member)
    stocky['calculation_context'].update(l0_x_m=.5,l0_y_m=.5)
    short=run('wall_native_eta_one',stocky)
    diagnostics=check(short,'member_stability')['details']
    assert diagnostics['eta_x']==diagnostics['eta_y']==1,diagnostics
    for key in ('shear_strip','shear_inclined','inclined_moment'):
        original,current=check(moment,key),check(short,key)
        assert current['status']==original['status'],(key,current,original)
        assert abs(current['utilization']-original['utilization'])<1e-10,(key,current,original)
        forces=current['details']['force_context']
        assert forces['actions_basis']=='source_actions_SP63_8_1_32_34',forces
        assert forces['eta']['eta_x']==forces['eta']['eta_y']==1,forces
    near_one=copy.deepcopy(member)
    for row in near_one['rows']:
        if row['basis']=='ULS':row['n']=-.00001
    tiny=run('wall_native_eta_barely_above_one',near_one)
    assert 1<check(tiny,'member_stability')['details']['eta_x']<1.000001
    assert check(tiny,'shear_inclined')['status'] in ('passed','failed')
    assert check(tiny,'inclined_moment')['status']=='not_evaluated'
    mixed=copy.deepcopy(member)
    mixed['rows'] += [dict(row,case='No compression',n=0)
                     for row in member['rows'] if row['basis']=='ULS']
    mixed_result=run('wall_native_mixed_eta_cases',mixed)
    mixed_checks=mixed_result['stations'][0]['report']['checks']
    by_case={c['case_id']:c for c in mixed_checks if c['check_id']=='member_stability'}
    for shear in (c for c in mixed_checks if c['check_id']=='shear_inclined'):
        assert shear['status'] in ('passed','failed'),shear
        assert shear['details']['force_context']['eta']['eta_x']==by_case[shear['case_id']]['details']['eta_x'],shear
    unconfirmed=copy.deepcopy(stocky);unconfirmed['eccentricity_confirmed']=False
    missing_confirmation=run('wall_native_eta_eccentricity_unconfirmed',unconfirmed)
    assert check(missing_confirmation,'shear_inclined')['status']=='not_evaluated'
    assert check(missing_confirmation,'shear_inclined')['reason']=='confirm_eccentricity_in_source'
    # The same applies to N=0 and tension, without guessing eta from the force sign.
    for name,n in [('no_axial_force',0),('tension',100)]:
        unchanged=copy.deepcopy(member)
        for row in unchanged['rows']:
            if row['basis']=='ULS':row['n']=n
        unchanged_result=run('wall_native_eta_one_'+name,unchanged)
        assert check(unchanged_result,'member_stability')['details']['eta_x']==1
        assert check(unchanged_result,'shear_inclined')['status'] in ('passed','failed')
    # Circular equivalent shear must follow the same native stability gate.
    stocky_pile=copy.deepcopy(stocky);stocky_pile.update(kind='pile',force_scale='per_member')
    stocky_pile['calculation_context']['circular_shear']={
        'method':'vniigs_1984_two_legs','a_mm':80,'winding_diameter_mm':None,
        'circular_efficiency':None,'efficiency_basis':'Accepted two conditional legs',
        'confirmed':True}
    pile_short=run('pile_native_eta_one',stocky_pile)
    assert check(pile_short,'member_stability')['status']=='stable'
    for key in ('shear_strip','shear_inclined','inclined_moment'):
        assert check(pile_short,key)['status'] in ('passed','failed'),check(pile_short,key)
    stocky_pile['calculation_context'].update(l0_x_m=12,l0_y_m=12)
    pile_amplified=run('pile_native_eta_amplified',stocky_pile)
    assert check(pile_amplified,'member_stability')['details']['eta_x']>1
    for key in ('shear_strip','shear_inclined'):
        assert check(pile_amplified,key)['status'] in ('passed','failed'),check(pile_amplified,key)
        assert abs(check(pile_amplified,key)['utilization']-check(pile_short,key)['utilization'])<1e-10
    assert check(pile_amplified,'inclined_moment')['status']=='not_evaluated'
    # Loss of stability must never become a zero-utilization strength pass.
    unstable=copy.deepcopy(member)
    unstable['calculation_context'].update(l0_x_m=100,l0_y_m=100)
    for row in unstable['rows']:
        if row['basis']=='ULS':row['n']=-1000
    instability=run('wall_native_unstable',unstable)
    assert check(instability,'member_stability')['status']=='unstable'
    assert check(instability,'normal_member_strength')['status']=='not_evaluated'
    for key in ('shear_strip','shear_inclined','inclined_moment'):
        assert check(instability,key)['status']=='not_evaluated'
        assert check(instability,key)['reason']=='native_member_unstable'
    assert check(instability,'crack_width_short')['status']=='not_evaluated'
    assert instability['status']=='failed'
    sls_instability=copy.deepcopy(member)
    sls_instability['calculation_context'].update(l0_x_m=100,l0_y_m=100)
    for row in sls_instability['rows']:
        row.update(n=-10 if row['basis']=='ULS' else -1000,m=1)
        if row['basis']=='SLS':row['m_long']=1
    sls_unstable=run('wall_SLS_only_instability',sls_instability)
    assert sls_unstable['status']=='failed',sls_unstable
    assert check(sls_unstable,'sls_member_stability')['status']=='unstable'
    bns=copy.deepcopy(p);bns.update(kind='pile',force_scale='per_member')
    bns['transverse']['mode']='off';bns['calculation_context']=default_context()
    pile=run('pile_native_checks',bns)
    assert check(pile,'normal_strength')['status']=='passed'
    assert check(pile,'crack_formation')['status']=='calculated'
    assert check(pile,'shear_inclined')['status']=='not_applicable'
    no_sls=copy.deepcopy(p);no_sls['rows']=[r for r in p['rows'] if r['basis']=='ULS']
    missing=run('wall_no_SLS',no_sls)
    assert check(missing,'crack_width_short')['status']=='not_evaluated'
    overload=copy.deepcopy(p)
    for row in overload['rows']:
        if row['basis']=='ULS':row['m']=50000
    skipped=run('wall_ULS_gate',overload)
    assert check(skipped,'normal_strength')['status']=='failed'
    assert check(skipped,'crack_width_long')['status']=='not_evaluated'
    assert check(skipped,'crack_width_long')['reason']=='skipped_uls_fail'
    print('Native CScore cycle: N/M, SLS formation/long/short, Q, inclined moment, eta, instability, missing data and unsupported circular shear passed')

if __name__=='__main__':main()
