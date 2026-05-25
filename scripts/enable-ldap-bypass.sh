#!/bin/bash
# Quick-fix: enable LDAP bypass on the server so any credential works.
# AdminUsers config still controls who gets admin access.
# Run on the DGX server as `admin`.
set -e

APP=/home/admin/setllm-api/appsettings.json

if [ ! -f "$APP" ]; then
  echo "ERR: $APP not found"
  exit 1
fi

# Use Python to do an in-place JSON edit (jq might be missing)
python3 - <<'PYEOF'
import json, os
path = '/home/admin/setllm-api/appsettings.json'
with open(path, 'r', encoding='utf-8') as f:
    cfg = json.load(f)
cfg.setdefault('Ldap', {})['Bypass'] = True
with open(path, 'w', encoding='utf-8') as f:
    json.dump(cfg, f, indent=2, ensure_ascii=False)
print('Ldap.Bypass set to true')
PYEOF

sudo systemctl restart setllm-api
sleep 4
curl -sf http://localhost:5080/health && echo " — API back up"
echo ""
echo "✅ Bypass mode active. burakpus + any password → admin login"
echo "   To revert: edit $APP and set Ldap.Bypass=false → restart service"
