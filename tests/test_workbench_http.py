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

    def test_path_not_exposed(self):
        with self.assertRaises(HTTPError):urlopen(self.url+'/workbench/model.py')

if __name__=='__main__':unittest.main()
