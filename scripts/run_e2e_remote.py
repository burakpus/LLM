"""Run the e2e test on the DGX server via SSH (paramiko).

Usage:
    python scripts/run_e2e_remote.py

Reads SSH user/password and AD password from env vars:
    SSH_USER, SSH_PASS  — SSH credentials for the server
    AD_PASS             — AD password to pass to e2e-test.sh
"""
import os
import sys
import io
from pathlib import Path
import paramiko

# Force UTF-8 stdout so Turkish chars don't blow up on Windows cp1252
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

HOST = "172.16.1.123"
LOCAL_SCRIPT = Path(__file__).parent / "e2e-test.sh"
REMOTE_PATH = f"/tmp/e2e-test-{os.environ.get('SSH_USER', 'x')}-{os.getpid()}.sh"

ssh_user = os.environ.get("SSH_USER") or (sys.argv[1] if len(sys.argv) > 1 else None)
ssh_pass = os.environ.get("SSH_PASS") or (sys.argv[2] if len(sys.argv) > 2 else None)
ad_pass  = os.environ.get("AD_PASS")  or (sys.argv[3] if len(sys.argv) > 3 else ssh_pass)

if not (ssh_user and ssh_pass):
    print("Need SSH_USER + SSH_PASS env vars (or argv 1 + 2)")
    sys.exit(2)

client = paramiko.SSHClient()
client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
print(f"[*] Connecting to {ssh_user}@{HOST} ...")
client.connect(HOST, username=ssh_user, password=ssh_pass, timeout=15)

print(f"[*] Uploading {LOCAL_SCRIPT} -> {REMOTE_PATH}")
sftp = client.open_sftp()
sftp.put(str(LOCAL_SCRIPT), REMOTE_PATH)
sftp.chmod(REMOTE_PATH, 0o755)
sftp.close()

cmd = f"bash {REMOTE_PATH} '{ad_pass}'"
print(f"[*] Running: {cmd.replace(ad_pass, '***')}")
print("-" * 70)
stdin, stdout, stderr = client.exec_command(cmd, timeout=300)
for line in iter(stdout.readline, ""):
    print(line, end="")
err = stderr.read().decode()
if err:
    print("STDERR:", err, file=sys.stderr)
exit_code = stdout.channel.recv_exit_status()
print("-" * 70)
print(f"[*] Remote exit code: {exit_code}")
client.close()
sys.exit(exit_code)
