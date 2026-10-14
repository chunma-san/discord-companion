#!/usr/bin/env python3
import pathlib,subprocess,pwd,grp,shutil,os
base=pathlib.Path(__file__).resolve().parent
env=base/'.env'
if env.exists():raise SystemExit('Existing .env found. Back it up or configure the bridge manually.')
def run(args):subprocess.run(args,check=True)
if os.geteuid()!=0:raise SystemExit('Run with sudo on the server.')
try:group=grp.getgrnam('gliv')
except KeyError:run(['groupadd','--system','gliv']);group=grp.getgrnam('gliv')
try:user=pwd.getpwnam('gliv')
except KeyError:
    run(['useradd','--system','--gid','gliv','--home-dir','/var/lib/gliv','--no-create-home','--shell','/usr/sbin/nologin','gliv']);user=pwd.getpwnam('gliv')
assert user.pw_uid!=0 and group.gr_gid!=0
run(['python3',str(base/'test_host_broker.py')])
dest=pathlib.Path('/usr/local/lib/gliv');dest.mkdir(mode=0o755,parents=True,exist_ok=True)
shutil.copyfile(base/'host_broker.py',dest/'host_broker.py');(dest/'host_broker.py').chmod(0o644);os.chown(dest/'host_broker.py',0,0)
unit=pathlib.Path('/etc/systemd/system/gliv-host-bridge.service')
unit.write_text('''[Unit]
Description=Great Love Immortal Venerable fixed host bridge
After=docker.service teamspeak-ts-timer.service
Wants=docker.service

[Service]
Type=simple
User=root
Group=gliv
ExecStart=/usr/bin/python3 -u /usr/local/lib/gliv/host_broker.py
RuntimeDirectory=gliv
RuntimeDirectoryMode=0750
Restart=on-failure
RestartSec=5
UMask=0077
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=read-only
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
PrivateTmp=yes
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK
ReadWritePaths=/run/gliv -/run/teamspeak-ts-timer

[Install]
WantedBy=multi-user.target
''');unit.chmod(0o644)
env.write_text(f'GLIV_UID={user.pw_uid}\nGLIV_GID={group.gr_gid}\n');env.chmod(0o600);os.chown(env,int(os.environ.get('SUDO_UID',str(user.pw_uid))),int(os.environ.get('SUDO_GID',str(group.gr_gid))))
run(['systemctl','daemon-reload']);run(['systemctl','enable','gliv-host-bridge.service']);run(['systemctl','restart','gliv-host-bridge.service'])
run(['systemctl','is-active','gliv-host-bridge.service'])
print('Fixed host bridge installed. Dedicated bot user has no sudo or Docker group membership.')
print('Discord bot remains stopped until its own credentials are configured.')
