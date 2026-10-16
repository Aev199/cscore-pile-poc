import unittest
from types import SimpleNamespace
from workbench.plaxis import OutputConnection

class PlaxisExtractionTests(unittest.TestCase):
    def connection(self,kind='EmbeddedBeam'):
        c=object.__new__(OutputConnection)
        c.objects=[(kind,'object',{})];c.phases=['Phase A'];c.info={}
        c.types={kind:{k:k for k in ('x','z','n','m','q')}}
        c.g=SimpleNamespace(getresults=lambda obj,phase,typ,loc:{
            'x':[0,0],'z':[0,-2000],'n':[-100000,-200000],
            'm':[300000000,500000000],'q':[10000,20000]}[typ])
        return c

    def options(self,**changes):
        return dict(objects=[0],phases=[0],basis='ULS',force_scale='per_member',
                    confirmed=True,force_unit='N',length_unit='mm',**changes)

    def test_embedded_member_units_are_converted_without_spacing(self):
        r=self.connection().extract(self.options())
        self.assertEqual(r['rows'][1]['z'],-2)
        self.assertEqual(r['rows'][0]['n'],-100)
        self.assertEqual(r['rows'][0]['m'],300)
        self.assertEqual(r['force_scale'],'per_member')
        self.assertNotIn('password',r['source'])

    def test_line_units_and_plate_scale(self):
        opts=self.options();opts['force_scale']='per_metre'
        r=self.connection('Plate').extract(opts)
        self.assertEqual(r['rows'][0]['n'],-100000)
        self.assertEqual(r['rows'][0]['m'],300000)
        with self.assertRaises(ValueError):self.connection('Plate').extract(self.options())

    def test_selection_missing_results_and_verticality(self):
        c=self.connection();opts=self.options();opts['objects']=[0,0]
        with self.assertRaises(ValueError):c.extract(opts)
        c.g=SimpleNamespace(getresults=lambda obj,phase,typ,loc:[0,2] if typ=='x' else [0,0])
        with self.assertRaisesRegex(ValueError,'вертик'):c.extract(self.options())
        c=self.connection();c.g=SimpleNamespace(getresults=lambda obj,phase,typ,loc:[] if typ=='m' else [0,0])
        with self.assertRaises(ValueError):c.extract(self.options())

    def test_same_phase_descriptions_keep_distinct_identity(self):
        c=self.connection()
        c.phases=[SimpleNamespace(Identification=SimpleNamespace(value='Same')),
                  SimpleNamespace(Identification=SimpleNamespace(value='Same'))]
        opts=self.options();opts['phases']=[0,1]
        rows=c.extract(opts)['rows']
        self.assertEqual(len({r['case'] for r in rows}),2)

    def test_sls_requires_explicit_sustained_declaration(self):
        opts=self.options();opts['basis']='SLS'
        with self.assertRaises(ValueError):self.connection().extract(opts)
        opts['fully_sustained']=True
        r=self.connection().extract(opts)
        self.assertEqual(r['rows'][0]['m_long'],r['rows'][0]['m'])

if __name__=='__main__':unittest.main()
