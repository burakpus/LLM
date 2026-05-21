# Slack Slash Command Setup

## 1. Create Slack App

1. https://api.slack.com/apps → **Create New App** → "From scratch"
2. Name: `DGX LLM Bot`. Pick workspace.

## 2. Enable Slash Commands

In your app: **Slash Commands → Create New Command**

| Command | Request URL | Short Description |
|---|---|---|
| `/llm-state` | `https://llm-bot.internal/slack/commands` | Show current LLM stack state |
| `/llm-swap` | `https://llm-bot.internal/slack/commands` | Swap between hot/cold tier (`reasoning` or `default`) |

Usage hint for `/llm-swap`: `reasoning | default`

## 3. Get Signing Secret

**Basic Information → App Credentials → Signing Secret**.
Copy into `appsettings.json` under `Slack:SigningSecret`, or pass via env var:

```bash
export Slack__SigningSecret=...
```

## 4. Install to Workspace

**OAuth & Permissions → Install to Workspace**.
Authorize. Slash commands will be active.

## 5. Run the Bot

### Direct (host process, recommended for first deploy)
```bash
cd dgx-spark-llm-stack/slack
dotnet publish -c Release -o /opt/llm-slackbot
sudo cp llm-slackbot.service /etc/systemd/system/
sudo systemctl enable --now llm-slackbot
```

### Docker (alternative)
```bash
docker build -t llm-slackbot .
docker run -d --restart unless-stopped --name llm-slackbot \
  -p 5050:5050 \
  -v /opt/dgx-spark-llm-stack/scripts:/scripts:ro \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -e Slack__SigningSecret=... \
  -e Slack__ScriptsDir=/scripts \
  llm-slackbot
```

## 6. Reverse Proxy (Slack must reach you over HTTPS)

Slack requires public HTTPS. Two patterns:

| Pattern | Use Case |
|---|---|
| Cloudflare Tunnel | Easiest, no public IP needed |
| Traefik on edge + DNS | If you have a public-facing edge |

Add Traefik label to docker-compose service (if running in docker):
```yaml
labels:
  - "traefik.http.routers.slack.rule=Host(`llm-bot.example.com`) && PathPrefix(`/slack`)"
  - "traefik.http.routers.slack.entrypoints=websecure"
  - "traefik.http.routers.slack.tls=true"
```

## 7. Test

```
/llm-state
```
Should return current container statuses.

```
/llm-swap reasoning
```
Bot acknowledges, runs swap async, posts result back to channel after 5–8 min.

## Security

- ✅ HMAC SHA-256 signature verification (per Slack spec)
- ✅ 5-minute replay window
- ✅ Constant-time signature comparison (no timing attacks)
- ✅ No secrets in logs
- ⚠️ Slash command authorization is workspace-wide; restrict by channel if needed via Slack App config
