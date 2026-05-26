#!/bin/bash
# End-to-end smoke test for SET LLM (run on server).
# Usage: bash e2e-test.sh <password>
set -u
PASS="${1:-}"
if [ -z "$PASS" ]; then echo "usage: $0 <password>"; exit 1; fi

API="http://localhost:5080"
PASS_TESTS=0
FAIL_TESTS=0

passed() { echo "  PASS  $1"; PASS_TESTS=$((PASS_TESTS+1)); }
failed() { echo "  FAIL  $1"; FAIL_TESTS=$((FAIL_TESTS+1)); }

login() {
    curl -s -X POST $API/api/auth/login \
        -H "Content-Type: application/json" \
        -d "{\"username\":\"burakpus\",\"password\":\"$PASS\",\"domain\":\"SETYAZILIM\"}" \
        | python3 -c "import sys,json;print(json.load(sys.stdin).get('token',''))"
}

echo "===================================================================="
echo "T1. Basic health"
echo "===================================================================="
H=$(curl -sf $API/health || echo "")
[ -n "$H" ] && passed "/health: $H" || failed "/health"

echo ""
echo "===================================================================="
echo "T2. Login (correct password)"
echo "===================================================================="
TOKEN=$(login)
if [ ${#TOKEN} -gt 100 ]; then
    passed "Token alindi (${#TOKEN} char)"
else
    failed "Login basarisiz"
    exit 1
fi

echo ""
echo "===================================================================="
echo "T3. Brute-force rate limit"
echo "===================================================================="
RL_USER="rl$(date +%s)"
HIT_429=0
for i in 1 2 3 4 5 6 7; do
    CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST $API/api/auth/login \
        -H "Content-Type: application/json" \
        -d "{\"username\":\"$RL_USER\",\"password\":\"X$i\",\"domain\":\"SETYAZILIM\"}")
    echo "  Deneme $i: HTTP $CODE"
    [ "$CODE" = "429" ] && HIT_429=1 && break
done
[ "$HIT_429" = "1" ] && passed "Rate limit calisti (429 alindi)" || failed "Rate limit 429 alinmadi"

echo ""
echo "===================================================================="
echo "T4. /health/deep"
echo "===================================================================="
curl -sf $API/health/deep | python3 -c "
import sys,json
d = json.load(sys.stdin)
print(f'  status: {d[\"status\"]}')
for k,v in d.get('probes',{}).items():
    icon = 'OK' if v.get('ok') else 'FAIL'
    print(f'  [{icon}] {k}: {v.get(\"ms\",\"?\")}ms')
"

echo ""
echo "===================================================================="
echo "T5. Skills count (expect 21: 4 flat + 17 folder)"
echo "===================================================================="
curl -s $API/api/skills -H "Authorization: Bearer $TOKEN" | python3 -c "
import sys,json
d = json.load(sys.stdin)
total = len(d); folder = sum(1 for s in d if s.get('isFolder')); flat = total - folder
print(f'  Total={total}  Folder={folder}  Flat={flat}')
"

curl -s $API/api/skills -H "Authorization: Bearer $TOKEN" > /tmp/skill-resp.json
COUNT=$(python3 -c "import json; d=json.load(open('/tmp/skill-resp.json')); print(len(d))")
[ "$COUNT" = "21" ] && passed "21 skill yuklendi" || failed "Skill sayisi: $COUNT (21 bekleniyor)"
rm -f /tmp/skill-resp.json

echo ""
echo "===================================================================="
echo "T6. Event log summary + Security category"
echo "===================================================================="
curl -s "$API/api/admin/event-log/summary" -H "Authorization: Bearer $TOKEN" | python3 -c "
import sys,json
d = json.load(sys.stdin)
print(f'  Son 24 saat dagilim:')
for r in d.get('rows',[]): print(f'    {r[\"category\"]:10} {r[\"severity\"]:8} {r[\"count\"]:>5}')
"
SECCNT=$(curl -s "$API/api/admin/event-log?category=Security&pageSize=1" -H "Authorization: Bearer $TOKEN" | python3 -c "import sys,json;print(json.load(sys.stdin).get('total',0))")
[ "$SECCNT" -gt "0" ] && passed "Security event'leri var ($SECCNT toplam)" || failed "Security event yok"

echo ""
echo "===================================================================="
echo "T7. generate_file (4 formats)"
echo "===================================================================="
gen_check() {
    local KIND="$1"; local SPEC="$2"
    local R=$(curl -s -X POST $API/api/tools/generate-file \
        -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
        -d "{\"kind\":\"$KIND\",\"filename\":\"e2e.$KIND\",\"spec\":$SPEC}")
    local OK=$(echo "$R" | python3 -c "import sys,json;print(json.load(sys.stdin).get('ok',False))" 2>/dev/null || echo False)
    local SZ=$(echo "$R" | python3 -c "import sys,json;print(json.load(sys.stdin).get('sizeBytes',0))" 2>/dev/null || echo 0)
    if [ "$OK" = "True" ] && [ "$SZ" -gt "100" ]; then
        passed "$KIND uretildi ($SZ bytes)"
    else
        failed "$KIND uretilemedi"
    fi
}
gen_check docx '{"title":"T","sections":[{"paragraphs":["p1"]}]}'
gen_check xlsx '{"sheets":[{"name":"S","headers":["A"],"rows":[[1],[2]]}]}'
gen_check pdf  '{"title":"T","content_markdown":"# H\n\np"}'
gen_check pptx '{"slides":[{"title":"S","bullets":["b"]}]}'

echo ""
echo "===================================================================="
echo "T8. Benchmark (N=3)"
echo "===================================================================="
BENCH=$(curl -s -X POST $API/api/admin/benchmark \
    -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"model":"chat","concurrency":3,"prompt":"3 cumle ile e-posta nedir","maxTokens":60,"temperature":0.4,"label":"E2E"}')
echo "$BENCH" | python3 -c "
import sys,json
try:
    d = json.load(sys.stdin)
    print(f'  N={d[\"concurrency\"]}  Success={d[\"success\"]}/{d[\"success\"]+d[\"failed\"]}  Wall={d[\"wallSeconds\"]:.1f}s  TTFT p50={d[\"ttftP50Ms\"]:.0f}ms  Agg={d[\"tpsAggregate\"]:.1f} tok/s')
except Exception as e:
    print(f'  ERR: {e}')
"

echo ""
echo "===================================================================="
echo "T9. Background services in logs"
echo "===================================================================="
for SVC in JobWorker AutoSyncScheduler EventLogRetentionService GeneratedFilesCleanupService SkillRegistryEagerInitializer; do
    if journalctl -u setllm-api --since "2 hours ago" 2>&1 | grep -q "$SVC"; then
        passed "$SVC log gorundu"
    else
        echo "  SKIP  $SVC (log henuz yok)"
    fi
done

echo ""
echo "===================================================================="
echo "T10. Schema ingest job #16 status"
echo "===================================================================="
curl -s "$API/api/jobs/16" -H "Authorization: Bearer $TOKEN" | python3 -c "
import sys,json
try:
    j = json.load(sys.stdin)
    cur = j.get('progressCur',0); tot = j.get('progressTot',1)
    pct = round((cur/max(tot,1))*100,1)
    print(f'  status={j[\"status\"]}  {cur}/{tot} ({pct}%)')
    msg = j.get('message','')
    if msg: print(f'  msg: {msg[:80]}')
except Exception as e: print(f'  ERR: {e}')
"

echo ""
echo "===================================================================="
echo "T11. Deprecated endpoints removed (expect 404)"
echo "===================================================================="
for EP in ingest-schema-sync ingest-data-sync sync-schema-sync; do
    CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST \
        $API/api/admin/sql-connections/1/$EP \
        -H "Authorization: Bearer $TOKEN" \
        -H "Content-Type: application/json" -d '{}')
    # 404 (route hiç yok) veya 405 (SPA fallback GET'i index.html'e gönderiyor, POST için method
    # uygun değil) ikisi de "kullanıcı çağıramaz" anlamına gelir — başarılı.
    if [ "$CODE" = "404" ] || [ "$CODE" = "405" ]; then
        passed "$EP -> $CODE (erişilemez)"
    else
        failed "$EP -> $CODE"
    fi
done

echo ""
echo "===================================================================="
echo "T12. JWT auth boundary"
echo "===================================================================="
CODE=$(curl -s -o /dev/null -w "%{http_code}" $API/api/admin/skills -H "Authorization: Bearer INVALID_TOKEN")
[ "$CODE" = "401" ] && passed "Invalid token -> 401" || failed "Invalid token -> $CODE"
CODE=$(curl -s -o /dev/null -w "%{http_code}" $API/api/admin/skills)
[ "$CODE" = "401" ] && passed "No token -> 401" || failed "No token -> $CODE"

echo ""
echo "===================================================================="
echo "SUMMARY"
echo "===================================================================="
echo "PASS: $PASS_TESTS"
echo "FAIL: $FAIL_TESTS"
exit $FAIL_TESTS
