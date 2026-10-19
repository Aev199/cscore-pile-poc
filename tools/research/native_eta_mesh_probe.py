"""Native-only mesh sensitivity diagnostic; never a product/reference acceptance."""
import argparse,json,sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[2]))
from workbench import model
from workbench.connection import default_context
import wall_rebar as wall

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--runner',type=Path,required=True)
    args=ap.parse_args();out=Path('results/arbat-user-sections/eta-mesh');out.mkdir(parents=True,exist_ok=True)
    p=model.default_project();p.update(materials_confirmed=True,eccentricity_confirmed=True,max_tier=1)
    p['wall'].update(panel_width_mm=400,thickness_mm=600,cage_count=1,cage_width_mm=316,
                     end_cover_mm=42,cover_mm=42,ties_diameter_mm=8)
    p['rows']=[dict(z=0,case='U',basis='ULS',n=-2500,m=375,q=250,m_long=None)]
    p['background']=[4,4];p['diameters']=[25,25]
    p['transverse'].update(mode='screen',diameter_mm=8,constructive_confirmed=True)
    p['calculation_context']={**default_context(),'second_order_mode':'cscore_member','l0_x_m':15,'l0_y_m':15}
    records=[]
    for mesh in (21,41,81,161,200):
        p['wall']['mesh_resolution']=mesh
        inp=model.section_input(p,model.validate_project(p,single_section=True),tiers=[1,1])
        candidates,_=wall.make_candidates(inp);req=wall.build_engine_request(inp,candidates[0])
        req['connection_context']={**p['calculation_context'],'eccentricity_in_loads':True}
        req['connection_context'].pop('sections')
        req['connection_context'].update(support_distance_positive_m=None,support_distance_negative_m=None)
        for my in (0,20,25):
            req['loads'][0]['my_knm']=my
            raw=wall.invoke_engine(args.runner,req)
            checks={r['check_id']:r for r in raw['cases'][0]['connection_report']['checks']}
            row=dict(mesh=mesh,source_my_knm=my,normal=checks['normal_member_strength']['utilization'],
                     status=checks['normal_member_strength']['status'],**checks['member_stability']['details'])
            records.append(row);print(json.dumps(row),flush=True)
            (out/f'mesh{mesh}_my{my}.json').write_text(json.dumps(dict(request=req,result=raw),ensure_ascii=False,indent=2),encoding='utf-8')
    (out/'summary.json').write_text(json.dumps(records,indent=2),encoding='utf-8')

if __name__=='__main__':main()
