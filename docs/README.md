# SET LLM — Dokümantasyon

SET LLM, DGX Spark üzerinde çalışan şirket içi yapay zekâ platformudur — chat
asistanı, RAG dökümanları, SQL veri kaynakları, OWASP-uyumlu denetim kaydı,
arka plan iş kuyruğu ve admin panelinden yönetim sağlar.

## 📚 Belgeler

| Belge | Hedef Kitle | İçerik |
|-------|-------------|--------|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Geliştirici / Sistem yöneticisi | Bileşenler, veri akışı, DB şeması, deploy pipeline |
| [OPERATIONS.md](OPERATIONS.md) | Sistem yöneticisi | Deploy, backup, restore, troubleshooting playbook'ları |
| [SECURITY.md](SECURITY.md) | Güvenlik / Sistem yöneticisi | LDAP, JWT, rate limits, OWASP event log, threat model |
| [API.md](API.md) | Geliştirici / Entegratör | Endpoint referansı, auth, request/response şemaları |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Geliştirici | Repo yapısı, local kurulum, build, code conventions |

## 🚀 Hızlı bakış (1 dakika)

- **Adres**: `http://172.16.1.123:5080`
- **Modeller**: Gemma 4 26B (chat), Qwen3 27B (code/agentic), GPT-OSS 120B (reason)
- **Backend**: .NET 8 Minimal API
- **Frontend**: React 18 + Vite + Zustand + Tailwind
- **Veri**: PostgreSQL 16 + pgvector, Skills/ klasörü (folder + flat .md)
- **Auth**: LDAP (Novell.Directory.Ldap) + JWT bearer
- **Servis**: vLLM (3 model) + LiteLLM gateway + nginx
- **Monitoring**: Prometheus + Grafana + Loki + custom event_log

## 🗺 Mimari (bird's-eye)

```
Browser  ←→  nginx/Traefik  ←→  setllm-api (.NET 8)  ←→  PostgreSQL + pgvector
                                       │
                                       ├──→ LiteLLM gateway ──→ vLLM (Gemma/Qwen/GPT-OSS)
                                       ├──→ Skill dosyaları (Skills/)
                                       ├──→ LDAP (setyazilim.com)
                                       └──→ Python file-gen (docx/xlsx/pdf/pptx)
```

Detay diyagram için [ARCHITECTURE.md](ARCHITECTURE.md).
