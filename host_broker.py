#!/usr/bin/env python3
"""Local, fixed-operation bridge. Never accepts shell commands or arbitrary paths."""
import datetime, json, os, pathlib, shutil, socketserver, stat, subprocess, urllib.request

SOCKET=os.getenv('HOST_SOCKET','/run/gliv/host.sock')
SERVICES=json.loads(os.environ.get("HOST_SERVICE_ALLOWLIST", "{}"))
TS_BOTS=json.loads(os.environ.get("TS_BOT_CONTAINERS", "[]"))
ACTIONS={'server','uptime','disk','network','docker','containers','service','voice-timer','teamspeak','navidrome','immich','teamspeak-start','teamspeak-stop'}

def run(args):
    result=subprocess.run(args,capture_output=True,text=True,timeout=10)
    if result.returncode: raise RuntimeError('Local command failed')
    return result.stdout.strip()

def validate(request):
    if not isinstance(request,dict) or set(request)-{'action','service'}:raise ValueError('Invalid request')
    action=request.get('action')
    if not isinstance(action,str) or action not in ACTIONS:raise ValueError('Unsupported action')
    service=request.get('service')
    if action=='service' and service not in SERVICES:raise ValueError('Unsupported service')
    if action!='service' and service is not None:raise ValueError('Unexpected service')
    return action,service

def service_state(name):
    text=run(['systemctl','show',name+'.service','-p','ActiveState','-p','SubState','-p','NRestarts'])
    return {key:value for key,value in (line.split('=',1) for line in text.splitlines())}

def containers():
    ids=run(['docker','ps','-aq']).split()
    if not ids:return []
    info=json.loads(run(['docker','inspect',*ids]))
    return [{'name':d['Name'].lstrip('/'),'state':d['State']['Status'],
             'health':d['State'].get('Health',{}).get('Status','no healthcheck'),
             'restarts':d['RestartCount']} for d in info]

def uptime():
    seconds=int(float(pathlib.Path('/proc/uptime').read_text().split()[0]))
    return {'uptime':str(datetime.timedelta(seconds=seconds))}

def disk():
    data={}
    for path in ['/',os.getenv('STORAGE_MOUNT','/srv/storage')]:
        if path!='/':
            if not os.path.ismount(path):data[path]='NOT MOUNTED';continue
            uuid=run(['findmnt','-n','-o','UUID','--target',path])
            if uuid!=os.environ.get('EXPECTED_STORAGE_UUID', ''):data[path]='UNEXPECTED DISK';continue
        total,used,free=shutil.disk_usage(path)
        data[path]=f'{used/2**30:.1f} / {total/2**30:.1f} GiB used · {free/2**30:.1f} GiB free'
    return data

def probe(url):
    try:
        with urllib.request.urlopen(url,timeout=4) as response:return response.status
    except Exception:return 'unreachable'

def handle(request):
    action,service=validate(request)
    if action=='uptime':return uptime()
    if action=='disk':return disk()
    if action=='server':
        mem={k:int(v.split()[0]) for k,v in (line.split(':',1) for line in pathlib.Path('/proc/meminfo').read_text().splitlines())}
        data={**uptime(),'load (1/5/15 min)':' / '.join(f'{x:.2f}' for x in os.getloadavg()),
              'RAM':f'{(mem["MemTotal"]-mem["MemAvailable"])/2**20:.2f} / {mem["MemTotal"]/2**20:.2f} GiB',**disk()}
        status=pathlib.Path('/var/lib/server-health/status.json')
        if status.exists():
            health=json.loads(status.read_text())
            data['health']=json.dumps({k:v['status'] for k,v in health.get('checks',{}).items()})
        return data
    if action=='network':
        return {'Ethernet':pathlib.Path('/sys/class/net/'+os.getenv('HOST_NETWORK_INTERFACE','eth0')+'/operstate').read_text().strip(),
                'Ethernet addresses':', '.join(a['local'] for interface in json.loads(run(['ip','-j','-4','addr','show',os.getenv('HOST_NETWORK_INTERFACE','eth0')])) for a in interface.get('addr_info',[])),
                'Tailscale':service_state('tailscaled')['ActiveState']}
    if action=='service':return {service:json.dumps(service_state(SERVICES[service]))}
    if action in {'docker','containers','voice-timer','teamspeak'}:
        rows=containers()
        if action=='docker':return {'running':sum(d['state']=='running' for d in rows),'stopped':sum(d['state']!='running' for d in rows),'total':len(rows)}
        if action=='containers':return [f"{d['name']}: {d['state']} · {d['health']} · restarts {d['restarts']}" for d in rows]
        data={d['name']:d['state'] for d in rows if d['name'] in TS_BOTS}
        if action=='voice-timer':data['timer service']=service_state('teamspeak-ts-timer')['ActiveState']
        return data
    if action in {'navidrome','immich'}:
        name='navidrome' if action=='navidrome' else 'immich_server'
        data=next((d for d in containers() if d['name']==name),{'state':'missing'})
        data['HTTP']=probe('http://127.0.0.1:2283/api/server/ping' if action=='immich' else 'http://127.0.0.1:4533/ping')
        return data
    if action in {'teamspeak-start','teamspeak-stop'}:
        if pathlib.Path('/etc/server-maintenance').exists():raise RuntimeError('Maintenance active')
        command='start' if action=='teamspeak-start' else 'stop'
        path='/run/teamspeak-ts-timer/control'
        if not stat.S_ISFIFO(os.stat(path,follow_symlinks=False).st_mode):raise RuntimeError('Timer control unavailable')
        fd=os.open(path,os.O_WRONLY|os.O_NONBLOCK|os.O_NOFOLLOW)
        try:os.write(fd,(command+'\n').encode())
        finally:os.close(fd)
        return {'timer command':command+' sent','TeamSpeak bots':'unchanged'}
    raise ValueError('Unsupported action')

class Handler(socketserver.StreamRequestHandler):
    def handle(self):
        self.request.settimeout(20)
        try:
            line=self.rfile.readline(1025)
            if len(line)>1024 or not line.endswith(b'\n'):raise ValueError('Invalid message size')
            request=json.loads(line)
            data=handle(request)
            response={'ok':True,'data':data}
            print('Host action: '+request['action'],flush=True)
        except Exception as exc:
            print('Host request rejected/failed: '+type(exc).__name__,flush=True)
            response={'ok':False,'error':'Request refused or failed'}
        self.wfile.write((json.dumps(response)+'\n').encode())

if __name__=='__main__':
    import grp
    pathlib.Path(SOCKET).unlink(missing_ok=True)
    with socketserver.UnixStreamServer(SOCKET,Handler) as server:
        os.chown(SOCKET,0,grp.getgrnam('gliv').gr_gid);os.chmod(SOCKET,0o660)
        print('Great Love Immortal Venerable host bridge ready',flush=True)
        server.serve_forever()
