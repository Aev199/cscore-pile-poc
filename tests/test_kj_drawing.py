"""Draft DXF: physically located cages and reinforcement zones, no fake details."""
import unittest

from workbench import model
from workbench.kj_drawing import make_wall_dxf


def entities(dxf):
    pairs=dxf.splitlines()
    assert len(pairs)%2==0
    chunks=[]
    current=None
    for code,value in zip(pairs[::2],pairs[1::2]):
        if code=='0' and value in ('LINE','LWPOLYLINE','TEXT'):
            if current:chunks.append(current)
            current=[(code,value)]
        elif code=='0' and current:
            chunks.append(current)
            current=None
        elif current:
            current.append((code,value))
    if current:chunks.append(current)
    return chunks


def attr(ent, code):
    return [v for k,v in ent if k==str(code)]


class KJDraftDxfTests(unittest.TestCase):
    def test_three_physical_cages_1_to_1_and_cyrillic(self):
        p=model.default_project()
        schedule=model.reinforcement_schedule(p)
        data=make_wall_dxf(p,schedule)
        data.encode('ascii')  # Requires real DXF R2000 Unicode escapes.
        self.assertIn('$INSUNITS\n70\n4',data)
        self.assertIn('AC1015',data)
        self.assertIn('\\U+0420',data)
        parts=entities(data)
        wall=[e for e in parts if attr(e,8)==['KJ_WALL']]
        self.assertEqual(len(wall),1)
        self.assertEqual(attr(wall[0],10),['0','3000','3000','0'])
        self.assertEqual(attr(wall[0],20),['0','0','-12000','-12000'])
        cages=[e for e in parts if attr(e,8)==['KJ_CAGE']]
        self.assertEqual(len(cages),3)
        self.assertEqual([float(attr(e,10)[0]) for e in cages],[50,1050,2050])
        self.assertTrue(all(attr(e,70)==['1'] for e in cages))

    def test_zones_are_actual_elevation_boundaries_not_schematic_z(self):
        p=model.default_project()
        p['zones']=[{'top':0,'bottom':-6,'tiers':[2,1]}]
        p['transverse_zones']=[{'top':-3,'bottom':-9,'spacing_mm':150}]
        info=model.reinforcement_schedule(p)
        result=make_wall_dxf(p,info)
        line_ys=sorted(float(attr(e,20)[0]) for e in entities(result)
                       if attr(e,8)==['KJ_ZONE'])
        self.assertEqual(line_ys,[-9000,-6000,-3000])
        self.assertIn('D10/150',result)
        self.assertIn('7xD25',result)
        self.assertEqual(result.count('0\nLWPOLYLINE\n'),4)
        # Information is explicitly draft and never contains actual bars.
        self.assertNotIn('KJ_BARS',result)
        self.assertIn('KJ_NOTES',result)

    def test_one_cage_centers_without_invented_clearances(self):
        p=model.default_project()
        p['wall']['cage_count']=1
        p['wall']['cage_width_mm']=900
        draft=model.reinforcement_schedule(p)
        data=make_wall_dxf(p,draft)
        cages=[e for e in entities(data) if attr(e,8)==['KJ_CAGE']]
        self.assertEqual([float(v) for v in attr(cages[0],10)],
                         [1050,1950,1950,1050])

    def test_fail_closed_on_wrong_document_or_geometry(self):
        p=model.default_project()
        d=model.reinforcement_schedule(p)
        bad={**d,'status':'APPROVED'}
        with self.assertRaises(ValueError):
            make_wall_dxf(p,bad)
        with self.assertRaises(ValueError):
            make_wall_dxf(p,{**d,'rows':[]})
        p['wall']['cage_width_mm']=1800
        with self.assertRaisesRegex(ValueError,'не размещаются'):
            make_wall_dxf(p,d)


if __name__=='__main__':
    unittest.main()
