#!/usr/bin/env python3
"""Reproducible N/M-chord geometry experiment using the existing CScore DLL.

Does not evaluate shear capacity, modify the app, or certify SP63 equivalence.
"""
import argparse,copy,csv,json,subprocess,sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT))
import pile_rebar
from workbench import model
from tools.research.spiral_geometry import circular_efficiency,helix_projection


def main():
    ap=argparse.ArgumentParser()
    ap.add_argument('--dotnet',default='dotnet')
    ap.add_argument('--probe',type=Path,required=True)
    ap.add_argument('--runner',type=Path,required=True)
    ap.add_argument('--out',type=Path,required=True)
    args=ap.parse_args();args.out.mkdir(parents=True,exist_ok=True)
    base=json.loads((ROOT/'examples/verification/circular_shear/P800_spiral_200_control.json').read_text())
    # Include states for which the two-chord model cannot be silently assumed.
    base['rows']=[r for r in base['rows'] if r['z']==0]
    base['rows'] += [dict(z=0,case=case,basis='ULS',n=n,m=m,q=300,m_long=None)
        for case,n,m in [('pure_compression',-500,0),('pure_tension',100,0),
                         ('almost_axial_compression',-1000,.1),('near_capacity',0,500)]]
    summaries=[]
    for mesh,sides in [(41,96),(81,192),(121,256)]:
        p=copy.deepcopy(base);p['pile'].update(mesh_resolution=mesh,polygon_sides=sides)
        inp=model.section_input(p,model.normalized_rows(p),tiers=[1,1])
        pile_rebar.validate_input(inp);candidates,_=pile_rebar.make_candidates(inp)
        wire=pile_rebar.build_engine_request(inp,candidates)
        # Stress probe explicitly ignores Q: forces below are only N/M targets.
        request_file=args.out/f'NM_probe_mesh{mesh}.input.json'
        request_file.write_text(json.dumps(wire,ensure_ascii=False,indent=2)+'\n')
        proc=subprocess.run([args.dotnet,str(args.probe.resolve()),str(args.runner.resolve()),str(request_file.resolve())],
            capture_output=True,text=True,encoding='utf8',check=True)
        states=json.loads(proc.stdout)
        for state in states:
            row={**state,'mesh':mesh,'polygon_sides':sides,'lambda_c':None,
                 'lambda_h_200':helix_projection(200,697),'lambda_h_300':helix_projection(300,697),
                 'geometry_status':state['status']}
            if state['status']=='native_NM_chords_only':
                try:
                    row['lambda_c']=circular_efficiency(state['tension_y_m'],state['compression_y_m'],.697/2)
                    row['geometry_status']='geometric_integral_defined_NM_initial_state_only'
                except ValueError as e:row['geometry_status']=str(e)
            summaries.append(row)
        print(f'Native section states inspected: mesh {mesh}, {len(states)} action tuples',flush=True)
    (args.out/'spiral_NM_geometry.json').write_text(json.dumps(summaries,ensure_ascii=False,indent=2)+'\n')
    fields=['id','mesh','polygon_sides','n_kn','mx_knm','geometry_status','tension_y_m','compression_y_m',
            'z_m','lambda_c','lambda_h_200','lambda_h_300','native_residual','force_closure','moment_closure']
    with (args.out/'spiral_NM_geometry.csv').open('w',newline='',encoding='utf8') as f:
        w=csv.DictWriter(f,fieldnames=fields,extrasaction='ignore');w.writeheader();w.writerows(summaries)
    # Independent benchmark: publication's symmetric 0.8D lever and 0.45D radius.
    benchmark={'symmetric_paper_lambda_c':circular_efficiency(-.4,.4,.45),
        'full_diameter_lambda_c':circular_efficiency(-1,1,1),
        'scope':'geometry_only_no_shear_capacity_no_SP63_verdict'}
    (args.out/'geometry_benchmarks.json').write_text(json.dumps(benchmark,indent=2)+'\n')


if __name__=='__main__':main()
