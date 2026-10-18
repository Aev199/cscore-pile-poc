import copy
import unittest
from workbench import model
from workbench.connection import default_context, validate_context


def circular_project():
    p=model.default_project()
    p.update(kind='pile',force_scale='per_metre',spacing_m=1.5,
             materials_confirmed=True,eccentricity_confirmed=True,max_tier=1)
    p['rows']=[dict(z=z,case='U',basis='ULS',n=-50,m=40,q=200,m_long=None) for z in (0,-2)]
    p['transverse'].update(mode='screen',constructive_confirmed=True)
    p['calculation_context']={**default_context(),'circular_shear':{
        'method':'vniigs_1984_user_spiral','a_mm':80,
        'winding_diameter_mm':670,'circular_efficiency':.8,
        'efficiency_basis':'Заданная инженером модель; тест входного контракта',
        'confirmed':True}}
    return p


class CircularShearContracts(unittest.TestCase):
    def test_circular_mode_is_available_without_changing_selection(self):
        p=circular_project()
        model.validate_project(p)
        self.assertNotIn('shear_screening',model.section_input(p,model.normalized_rows(p)))

    def test_equivalence_values_cannot_be_invalid_or_unattributed(self):
        c=circular_project()['calculation_context']
        for key,value in [('a_mm',-1),('winding_diameter_mm',float('nan')),
                          ('circular_efficiency',1.01),('circular_efficiency',True),
                          ('efficiency_basis',''),('confirmed',1)]:
            with self.subTest(key=key,value=value):
                b=copy.deepcopy(c);b['circular_shear'][key]=value
                with self.assertRaises(ValueError):validate_context(b)

    def test_incomplete_equivalence_can_be_saved(self):
        c=default_context();c['circular_shear']={'method':'vniigs_1984_user_spiral',
            'a_mm':None,'winding_diameter_mm':None,'circular_efficiency':None,
            'efficiency_basis':'','confirmed':False}
        validate_context(c)

    def test_spiral_inside_concrete_and_longitudinal_geometry_is_consistent(self):
        p=circular_project()
        for changes in ({'a_mm':800},{'winding_diameter_mm':800}):
            b=copy.deepcopy(p);b['calculation_context']['circular_shear'].update(changes)
            with self.assertRaises(ValueError):model.validate_project(b)
        b=copy.deepcopy(p);b['transverse']['diameter_mm']=12
        with self.assertRaises(ValueError):model.validate_project(b)


if __name__=='__main__':unittest.main()
