"""CI-ONLY test infrastructure for M23: a disposable Samba server on loopback and a REAL Linux kernel CIFS mount.

Never part of the plugin. Binds only to 127.0.0.1, uses a random per-run account and password, exports a fresh
directory under the runner temp folder, and is torn down (unmounted, server killed, account removed) even on failure.
Requires passwordless sudo (GitHub-hosted runners) because the kernel mount and smbd's port 445 need root.

The library becomes a real `cifs` mount (checked with findmnt). Fault controls are deterministic process/mount
operations: SIGSTOP/SIGCONT the server (unreachable), SIGKILL + restart (crash), lazy unmount (path becomes a plain
empty local directory), remount read-only.
"""
from __future__ import annotations

import os
import secrets
import signal
import subprocess
import time
from pathlib import Path

USER = "crescendarrci"


def sudo(*args: str, check: bool = True, timeout: float = 60) -> subprocess.CompletedProcess:
    return subprocess.run(["sudo", "-n", *args], check=check, capture_output=True, text=True, timeout=timeout)


class CifsShare:
    def __init__(self, export: Path, mountpoint: Path, state: Path):
        self.export, self.mountpoint, self.state = export, mountpoint, state
        self.password = secrets.token_urlsafe(18)
        self.conf = state / "smb.conf"
        self.process: subprocess.Popen | None = None
        self.options = ("vers=3.1.1,soft,echo_interval=2,retrans=1,actimeo=1,serverino,"
                        f"uid={os.getuid()},gid={os.getgid()},file_mode=0664,dir_mode=0775")

    # ------------------------------------------------------------------ server
    def configure(self) -> None:
        self.export.mkdir(parents=True, exist_ok=True)
        os.chmod(self.export, 0o777)
        for d in ("private", "lock", "state", "cache", "pid", "log", "ncalrpc", "binddns"):
            (self.state / d).mkdir(parents=True, exist_ok=True)
        s = self.state
        self.conf.write_text(f"""[global]
   server role = standalone server
   workgroup = CRESCENDARRCI
   interfaces = lo
   bind interfaces only = yes
   smb ports = 445
   server min protocol = SMB3_11
   map to guest = never
   disable netbios = yes
   load printers = no
   printing = bsd
   printcap name = /dev/null
   private dir = {s / 'private'}
   lock directory = {s / 'lock'}
   state directory = {s / 'state'}
   cache directory = {s / 'cache'}
   pid directory = {s / 'pid'}
   log file = {s / 'log'}/smbd.log
   log level = 1
   ncalrpc dir = {s / 'ncalrpc'}
   binddns dir = {s / 'binddns'}
   server services = smb
   strict sync = yes
   sync always = no
[crescendarr]
   path = {self.export}
   read only = no
   valid users = {USER}
   force user = {os.environ.get('USER', 'runner')}
   create mask = 0664
   directory mask = 0775
""", encoding="utf-8")
        sudo("useradd", "-M", "-s", "/usr/sbin/nologin", USER, check=False)
        subprocess.run(["sudo", "-n", "smbpasswd", "-c", str(self.conf), "-s", "-a", USER], input=f"{self.password}\n{self.password}\n",
                       text=True, check=True, capture_output=True)

    def start_server(self) -> None:
        # Daemon mode (not a foreground child of sudo: Ubuntu 24.04 sudo with use_pty terminates detached children).
        result = sudo("smbd", "-D", "-s", str(self.conf), check=False)
        (self.state / "smbd.stdout").write_text(result.stdout + result.stderr)
        self.process = None
        limit = time.monotonic() + 30
        while time.monotonic() < limit:
            if subprocess.run(["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/445"], capture_output=True).returncode == 0:
                return
            time.sleep(0.3)
        output = (self.state / "smbd.stdout").read_text(errors="replace")[-4000:]
        logs = sudo("bash", "-c", f"tail -c 3000 {self.state / 'log'}/*.log 2>&1; ls -la {self.state}/*", check=False).stdout[-5000:]
        raise RuntimeError(f"smbd did not listen on 127.0.0.1:445 (pids={self.server_pids()})"
                           f"\n--- smbd output ---\n{output}\n--- smbd log ---\n{logs}")

    def server_pids(self) -> list[int]:
        out = subprocess.run(["pgrep", "-f", f"smbd -D -s {self.conf}"], capture_output=True, text=True).stdout.split()
        return [int(p) for p in out]

    def signal_server(self, sig: signal.Signals) -> None:
        for pid in self.server_pids():
            sudo("kill", f"-{sig.name.replace('SIG', '')}", str(pid), check=False)

    def kill_server(self) -> None:
        self.signal_server(signal.SIGKILL)
        limit = time.monotonic() + 15
        while self.server_pids() and time.monotonic() < limit:
            time.sleep(0.2)

    # ------------------------------------------------------------------ client
    def mount(self, read_only: bool = False) -> None:
        self.mountpoint.mkdir(parents=True, exist_ok=True)
        options = self.options + (",ro" if read_only else ",rw") + f",username={USER},password={self.password}"
        sudo("mount", "-t", "cifs", f"//127.0.0.1/crescendarr", str(self.mountpoint), "-o", options)
        if self.fstype() != "cifs":
            raise RuntimeError(f"{self.mountpoint} is not a cifs mount after mount(8)")

    def umount(self, lazy: bool = True) -> None:
        sudo("umount", "-l" if lazy else "-f", str(self.mountpoint), check=False)

    def fstype(self) -> str:
        return subprocess.run(["findmnt", "-n", "-o", "FSTYPE", "--target", str(self.mountpoint)], capture_output=True, text=True).stdout.strip()

    def evidence(self) -> dict:
        mounts = [l for l in Path("/proc/mounts").read_text().splitlines() if str(self.mountpoint) in l]
        debug = sudo("cat", "/proc/fs/cifs/DebugData", check=False).stdout
        dialect = [l.strip() for l in debug.splitlines() if "Dialect" in l][:2]
        return {
            "smbd": subprocess.run(["smbd", "--version"], capture_output=True, text=True).stdout.strip(),
            "mount.cifs": subprocess.run(["mount.cifs", "-V"], capture_output=True, text=True).stdout.strip(),
            "kernel": os.uname().release,
            "cifs_module": sudo("modinfo", "-F", "version", "cifs", check=False).stdout.strip(),
            "proc_mounts": [m.replace(self.password, "<redacted>") for m in mounts],
            "dialect": dialect,
            "findmnt_fstype": self.fstype(),
        }

    def teardown(self) -> None:
        self.signal_server(signal.SIGCONT)
        self.umount(lazy=True)
        self.kill_server()
        sudo("smbpasswd", "-c", str(self.conf), "-x", USER, check=False)
        sudo("userdel", USER, check=False)
