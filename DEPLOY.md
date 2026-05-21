# Deploy Checklist — Agentic AI MVP

Hedef: PG schema → embed servisi → API → smoke test.
Süre: ~30 dk.

---

## 0. Ön Koşullar

- [ ] `172.16.0.8:5432` dışarıdan erişilebilir (port açık)
- [ ] DGX (`172.16.1.123`) docker stack çalışıyor
- [ ] `.env` dosyası dolu — `VLLM_KEY`, `LITELLM_MASTER_KEY`, DB şifresi

---

## 1. PostgreSQL — Schema

`spark-7507` üzerinden:

```bash
cd ~/Documents/MultiModel/dgx-spark-llm-stack

# Bağlantı testi
nc -zv 172.16.0.8 5432
PGPASSWORD='Atlas_71' psql -h 172.16.0.8 -U setadmin -d mydb -c "SELECT version();"

# Schema uygula
PGPASSWORD='Atlas_71' psql -h 172.16.0.8 -U setadmin -d mydb -f db/001_init.sql

# Doğrula
PGPASSWORD='Atlas_71' psql -h 172.16.0.8 -U setadmin -d mydb -c "\dt"
# beklenen: kb_documents, session_memories, agent_memories

PGPASSWORD='Atlas_71' psql -h 172.16.0.8 -U setadmin -d mydb \
  -c "SELECT extname FROM pg_extension WHERE extname IN ('vector','uuid-ossp','pg_trgm','unaccent');"
# 4 satır dönmeli
```

---

## 2. Embedding Servisi (vllm-embed)

```bash
cd ~/Documents/MultiModel/dgx-spark-llm-stack

docker compose --profile default up -d vllm-embed

# Hazır olmasını bekle (~2 dk, model indiriyorsa daha uzun)
docker logs -f vllm-embed
# "Application startup complete" görene kadar bekle

# Smoke test
curl -s http://172.16.1.123:8004/v1/models | jq .

curl -s http://172.16.1.123:8004/v1/embeddings \
  -H "Authorization: Bearer $(cat secrets/vllm_key.txt)" \
  -H "Content-Type: application/json" \
  -d '{"model":"nomic-embed-text","input":"merhaba dünya"}' | jq '.data[0].embedding | length'
# beklenen: 768
```

---

## 3. LiteLLM Reload (embed model'i tanısın)

```bash
docker compose restart litellm

curl -s http://172.16.1.123:4000/v1/models \
  -H "Authorization: Bearer $LITELLM_MASTER_KEY" | jq '.data[].id'
# beklenen: chat, code, embed, huihui, reason
```

---

## 4. API Build & Run

```bash
cd ~/Documents/MultiModel/dgx-spark-llm-stack/dotnet/Api

# appsettings.json'da REPLACE_ME değerlerini doldur:
#   LiteLLM.ApiKey       → LITELLM_MASTER_KEY
#   VectorStore.EmbedApiKey → secrets/vllm_key.txt içeriği

# Skills klasörünü kopyala (IIS'tekilerle aynı)
mkdir -p Skills
cp ../../IIS/Skills/*.md Skills/

dotnet restore
dotnet build -c Release

# Çalıştır
dotnet run -c Release --urls http://0.0.0.0:5080
# veya production için:
#   dotnet publish -c Release -o /opt/llm-api
#   sudo systemctl enable --now llm-api.service
```

---

## 5. Smoke Test

```bash
# Health
curl http://localhost:5080/health

# Tek dokümanı ingest et
curl -X POST http://localhost:5080/api/ingest \
  -H "Content-Type: application/json" \
  -d '{
    "collection": "test",
    "source": "smoke-test",
    "title": "Test belgesi",
    "content": "Q1 2024 net gelir artışı yüzde 18 olarak gerçekleşti. Stok devir hızı 4.2x seviyesine ulaştı."
  }'
# beklenen: {"chunksCreated":1,...}

# Chat dene
curl -X POST http://localhost:5080/api/chat \
  -H "Content-Type: application/json" \
  -H "X-User-Id: burak.pus" \
  -d '{
    "sessionId": "test-session-1",
    "agentId": "finance",
    "skillName": "Analysis_Assistant",
    "message": "Bu çeyrekte gelir nasıl?",
    "collections": ["test"]
  }'
# beklenen: kbHits ≥ 1, content içinde "%18" geçmeli
```

---

## 6. Sorun Çıkarsa

| Belirti | Bak |
|---|---|
| `kbHits: 0` ama belge var | EmbedDimensions=768 mi? `\d kb_documents` çıktısında `vector(768)` |
| `connection refused` PG | `pg_hba.conf` + `listen_addresses` |
| `Bearer` 401 | API key uyuşmazlığı — `secrets/vllm_key.txt` ↔ `appsettings.json` |
| Embed servisi başlamıyor | GPU memory — `nvidia-smi`; başka model yer kaplıyor olabilir |
| Türkçe arama bulamıyor | `SELECT to_tsvector('turkish_unaccent','test');` çalışıyor mu? |

---

## 7. IIS Entegrasyonu (sonraki adım)

`IIS/index.aspx` içine tek bir proxy endpoint daha eklenecek (`X-Agent-Chat`):

```csharp
// LDAP session'dan user → X-User-Id header'ı → POST http://172.16.1.123:5080/api/chat
```

IIS sadece auth + proxy yapacak, business logic API tarafında kalacak.

---

## Hızlı Komut Özeti

```bash
# 1. Schema
psql -h 172.16.0.8 -U setadmin -d mydb -f db/001_init.sql

# 2. Embed
docker compose up -d vllm-embed

# 3. LiteLLM reload
docker compose restart litellm

# 4. API
cd dotnet/Api && dotnet run -c Release --urls http://0.0.0.0:5080

# 5. Test
curl localhost:5080/health
```
