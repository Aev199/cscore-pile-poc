#!/usr/bin/env python3
"""Local browser UI. No external service and no third-party UI dependencies."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import secrets
import threading
from urllib.parse import urlparse,parse_qs
import webbrowser

from workbench import model
from workbench.profile_audit import audit as audit_profiles
from workbench.plaxis import OutputConnection,LOCK

ROOT=Path(__file__).resolve().parent


class App:
    def __init__(self,runner):
        self.runner=runner;self.token=secrets.token_urlsafe(32)
        self.jobs={};self.pool=ThreadPoolExecutor(max_workers=1);self.connection=None;self.guard=threading.Lock()

    def start(self,p,recheck,profile=False):
        model.validate_project(p)
        if not self.runner.exists():raise ValueError('Нет CScorePileRunner.exe. Используйте Windows-архив сборки или укажите --runner.')
        with self.guard:
            if any(j['status']=='running' for j in self.jobs.values()):raise ValueError('Расчёт уже идёт. Дождитесь завершения.')
            self.jobs.clear();key=secrets.token_hex(12)
            self.jobs[key]={'status':'running','message':'Подготовка','result':None}
        def run():
            try:
                def progress(msg):self.jobs[key]['message']=msg
                result=(model.screen_background_profile(p,runner=self.runner) if profile
                        else model.design(p,runner=self.runner,progress=progress,recheck=recheck))
                self.jobs[key].update(status='done',result=result,message='Расчёт завершён')
            except Exception as exc:self.jobs[key].update(status='error',message=str(exc))
        self.pool.submit(run)
        return {'job':key}


def handler_for(app):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self,*args):pass
        def respond(self,status,content,kind='application/json; charset=utf-8'):
            data=json.dumps(content,ensure_ascii=False,allow_nan=False).encode() if kind.startswith('application/json') else content
            self.send_response(status);self.send_header('Content-Type',kind);self.send_header('Content-Length',str(len(data)))
            self.send_header('Cache-Control','no-store');self.send_header('X-Content-Type-Options','nosniff')
            self.send_header('Content-Security-Policy',"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'")
            self.end_headers();self.wfile.write(data)
        def local(self):
            return self.headers.get('Host')==f'127.0.0.1:{self.server.server_port}'
        def do_GET(self):
            if not self.local():return self.respond(403,{'error':'Local host only'})
            path=urlparse(self.path).path
            if path.startswith('/api/'):
                if self.headers.get('X-Session')!=app.token:return self.respond(403,{'error':'Session required'})
                if path=='/api/default':return self.respond(200,model.default_project())
                if path=='/api/job':
                    key=parse_qs(urlparse(self.path).query).get('id',[''])[0]
                    return self.respond(200,app.jobs.get(key,{'status':'error','message':'Задание не найдено'}))
                return self.respond(404,{'error':'Not found'})
            files={'/':('index.html','text/html; charset=utf-8'),'/app.js':('app.js','text/javascript; charset=utf-8'),'/style.css':('style.css','text/css; charset=utf-8')}
            if path not in files:return self.respond(404,{'error':'Not found'})
            name,kind=files[path];data=(ROOT/'web'/name).read_bytes()
            if name=='index.html':data=data.replace(b'__SESSION__',app.token.encode())
            self.respond(200,data,kind)
        def do_POST(self):
            try:
                origin=self.headers.get('Origin')
                if not self.local() or self.headers.get('X-Session')!=app.token or (origin and origin!=f'http://127.0.0.1:{self.server.server_port}'):
                    return self.respond(403,{'error':'Local session required'})
                length=int(self.headers.get('Content-Length','0'))
                if not 0<length<=4_000_000:return self.respond(413,{'error':'Запрос слишком большой'})
                data=json.loads(self.rfile.read(length),parse_constant=lambda x:(_ for _ in ()).throw(ValueError(x)))
                path=urlparse(self.path).path
                if path=='/api/table':result={'rows':model.parse_table(data['text'])}
                elif path=='/api/preview':
                    rows=model.validate_project(data);result={'rows':rows}
                elif path=='/api/profile-audit':
                    rows=model.validate_project(data);result=audit_profiles(rows)
                elif path=='/api/calculate':result=app.start(data['project'],data.get('recheck',False))
                elif path=='/api/profile-calculate':result=app.start(data['project'],False,profile=True)
                elif path=='/api/plaxis/connect':
                    with LOCK:
                        app.connection=None
                        connection=OutputConnection(int(data['port']),data['password'])
                        result=connection.catalog()
                        app.connection=connection
                elif path=='/api/plaxis/extract':
                    with LOCK:
                        if app.connection is None:raise ValueError('Сначала подключитесь к Output')
                        result=app.connection.extract(data)
                else:return self.respond(404,{'error':'Not found'})
                self.respond(200,result)
            except Exception as exc:self.respond(400,{'error':str(exc)})
    return Handler


def main():
    parser=argparse.ArgumentParser(description='Сечения — локальный интерфейс расчёта')
    parser.add_argument('--runner',type=Path,default=ROOT/'CScorePileRunner.exe')
    parser.add_argument('--port',type=int,default=0)
    parser.add_argument('--no-browser',action='store_true')
    args=parser.parse_args();app=App(args.runner.resolve())
    server=ThreadingHTTPServer(('127.0.0.1',args.port),handler_for(app))
    url=f'http://127.0.0.1:{server.server_port}'
    # Windows may encode redirected stdout as cp1252 even when the UI is Russian.
    # Keep the startup banner ASCII so a piped launcher cannot crash the server.
    print('Workbench:',url,flush=True)
    print('Press Ctrl+C to stop.',flush=True)
    if not args.no_browser:webbrowser.open(url)
    try:server.serve_forever()
    except KeyboardInterrupt:pass
    finally:server.server_close();app.pool.shutdown(wait=False,cancel_futures=True)

if __name__=='__main__':main()
