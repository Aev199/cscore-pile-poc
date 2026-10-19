import json
from pathlib import Path
import threading
import unittest
from urllib.request import Request,urlopen
from urllib.error import HTTPError
from http.server import ThreadingHTTPServer
from workbench_app import App,handler_for

class HttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app=App(Path('/missing/CScorePileRunner.exe'))
        cls.server=ThreadingHTTPServer(('127.0.0.1',0),handler_for(cls.app))
        cls.url=f'http://127.0.0.1:{cls.server.server_port}'
        cls.thread=threading.Thread(target=cls.server.serve_forever,daemon=True);cls.thread.start()
    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown();cls.server.server_close();cls.app.pool.shutdown()
    def test_static_and_session_required(self):
        self.assertIn('Сечения',urlopen(self.url).read().decode())
        with self.assertRaises(HTTPError):urlopen(self.url+'/api/default')
        r=Request(self.url+'/api/default',headers={'X-Session':self.app.token})
        self.assertEqual(json.load(urlopen(r))['kind'],'wall')

    def test_manual_section_is_start_page_and_profile_remains_available(self):
        html=urlopen(self.url).read().decode()
        self.assertIn('Проверка сечения',html)
        self.assertNotIn('__SESSION__',html)
        self.assertIn(self.app.token,html)
        self.assertIn('id="n"',html)
        self.assertIn('id="charts"',urlopen(self.url+'/profile').read().decode())
        self.assertEqual(urlopen(self.url+'/section.js').status,200)
        self.assertEqual(urlopen(self.url+'/section.css').status,200)

    def test_single_section_endpoint_accepts_one_station_without_fake_depth(self):
        from workbench.model import default_project
        p=default_project();p['rows']=[p['rows'][0]];p['max_tier']=1
        r=Request(self.url+'/api/section-calculate',data=json.dumps({'project':p}).encode(),headers={'X-Session':self.app.token})
        with self.assertRaises(HTTPError) as ctx:urlopen(r)
        # Reaches the real engine availability check, rather than requiring two depths.
        self.assertIn('CScorePileRunner',ctx.exception.read().decode())
    def test_cross_origin_rejected_and_missing_runner_reported(self):
        r=Request(self.url+'/api/table',data=b'{}',headers={'X-Session':self.app.token,'Origin':'https://evil.invalid'})
        with self.assertRaises(HTTPError) as ctx:urlopen(r)
        self.assertEqual(ctx.exception.code,403)
        from workbench.model import default_project
        r=Request(self.url+'/api/calculate',data=json.dumps({'project':default_project()}).encode(),headers={'X-Session':self.app.token})
        with self.assertRaises(HTTPError) as ctx:urlopen(r)
        self.assertIn('CScorePileRunner',ctx.exception.read().decode())
    def test_failed_reconnect_clears_previous_model(self):
        from unittest.mock import patch
        self.app.connection=object()
        with patch('workbench_app.OutputConnection',side_effect=ValueError('Connection failed')):
            r=Request(self.url+'/api/plaxis/connect',data=b'{"port":10001,"password":"x"}',headers={'X-Session':self.app.token})
            with self.assertRaises(HTTPError):urlopen(r)
        self.assertIsNone(self.app.connection)

    def test_mq_audit_api_is_diagnostic_only(self):
        from workbench.model import default_project
        p=default_project()
        r=Request(self.url+'/api/profile-audit',
                  data=json.dumps(p).encode(),
                  headers={'X-Session':self.app.token})
        result=json.load(urlopen(r))
        self.assertFalse(result['usable_for_design'])
        self.assertEqual(result['scope'],'diagnostic_only_no_shear_approval')
        self.assertEqual(len(result['profiles']),3)
        self.assertTrue(all(x['usable_for_design'] is False for x in result['profiles']))
        # Faulty or incomplete projects cannot get an optimistic profile label.
        p['rows'][0]['q']='NaN'
        r=Request(self.url+'/api/profile-audit',
                  data=json.dumps(p).encode(),
                  headers={'X-Session':self.app.token})
        with self.assertRaises(HTTPError):urlopen(r)

    def test_reinforcement_schedule_export_requires_valid_project_and_is_never_approved(self):
        from workbench.model import default_project
        p=default_project()
        p['transverse_zones']=[{'top':-2,'bottom':-6,'spacing_mm':150}]
        request=lambda data: Request(
            self.url+'/api/reinforcement-schedule',
            data=json.dumps(data).encode(),
            headers={'X-Session':self.app.token})
        result=json.load(urlopen(request(p)))
        self.assertEqual(result['draft']['status'],'DRAFT_NOT_VERIFIED')
        self.assertFalse(result['draft']['verified_for_design'])
        self.assertIn('ЧЕРНОВИК, НЕ ПРОВЕРЕНО',result['csv'])
        self.assertEqual(result['draft']['rows'][1]['links_pitch_mm'],150)
        p['q_allowed_steps_mm']=[0]
        with self.assertRaises(HTTPError) as ctx:urlopen(request(p))
        self.assertEqual(ctx.exception.code,400)

    def test_draft_dxf_contains_real_zone_geometry_and_no_approval(self):
        from workbench.model import default_project
        p=default_project()
        p['zones']=[{'top':0,'bottom':-6,'tiers':[2,1]}]
        p['transverse_zones']=[{'top':-3,'bottom':-9,'spacing_mm':150}]
        req=Request(self.url+'/api/reinforcement-drawing',
                    data=json.dumps(p).encode(),
                    headers={'X-Session':self.app.token})
        result=json.load(urlopen(req))
        self.assertEqual(result['status'],'DRAFT_NOT_VERIFIED')
        self.assertIn('AC1015',result['dxf'])
        self.assertIn('KJ_CAGE',result['dxf'])
        self.assertIn('D10/150',result['dxf'])
        self.assertIn('KJ_NOTES',result['dxf'])
        result['dxf'].encode('ascii')

    def test_path_not_exposed(self):
        with self.assertRaises(HTTPError):urlopen(self.url+'/workbench/model.py')

if __name__=='__main__':unittest.main()
