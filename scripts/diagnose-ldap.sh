#!/bin/bash
# Run LDAP diagnostic on the server to see exactly where it fails.
# Usage: ./diagnose-ldap.sh <domain> <username> <password>
# Calls the unauthenticated /api/auth/debug-ldap-anonymous endpoint
# (only works if AdminUsers is empty, see endpoint guard)

set -e

DOMAIN=${1:-SETYAZILIM}
USER=${2:-burakpus}
PASS=${3:-}

if [ -z "$PASS" ]; then
  echo "Usage: $0 <domain> <username> <password>"
  exit 1
fi

echo "=== LDAP diagnostic: $USER @ $DOMAIN ==="
curl -s -X POST http://localhost:5080/api/auth/debug-ldap-anonymous \
  -H "Content-Type: application/json" \
  -d "{\"domain\":\"$DOMAIN\",\"username\":\"$USER\",\"password\":\"$PASS\"}" \
  | python3 -m json.tool
