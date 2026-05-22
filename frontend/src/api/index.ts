// =============================================================================
// API client — all calls go to /api (proxied to .NET Core by Vite in dev,
// served by the same origin in production)
// =============================================================================

const BASE = ''  // same-origin in both dev (proxy) and prod

function token(): string {
  return localStorage.getItem('setllm-token') ?? ''
}

function headers(extra?: Record<string, string>) {
  return {
    'Content-Type': 'application/json',
    Authorization: `Bearer ${token()}`,
    ...extra,
  }
}

async function apiPost<T>(path: string, body: unknown): Promise<T> {
  const r = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: headers(),
    body: JSON.stringify(body),
  })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}

async function apiGet<T>(path: string): Promise<T> {
  const r = await fetch(`${BASE}${path}`, { headers: headers() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

// ── Auth ─────────────────────────────────────────────────────────────────────

export interface TokenResult {
  token:     string
  username:  string
  domain:    string
  expiresAt: number
}

export async function login(username: string, password: string, domain: string) {
  const r: TokenResult = await apiPost('/api/auth/login', { username, password, domain })
  localStorage.setItem('setllm-token', r.token)
  localStorage.setItem('setllm-user',  JSON.stringify({ username: r.username, domain: r.domain }))
  return r
}

export async function me(): Promise<{ username: string; domain: string }> {
  return apiGet('/api/auth/me')
}

export function logout() {
  localStorage.removeItem('setllm-token')
  localStorage.removeItem('setllm-user')
}

export async function getDomains(): Promise<string[]> {
  const r = await fetch('/api/auth/domains')
  return r.json()
}

// ── Skills ───────────────────────────────────────────────────────────────────

export interface Skill {
  id:          string
  name:        string
  description: string
  icon:        string
  collection?: string  // if set, RAG is scoped to this collection
}

export async function listSkills(): Promise<Skill[]> {
  return apiGet('/api/skills')
}

// ── Chat (streaming) ──────────────────────────────────────────────────────────

export interface ChatPayload {
  sessionId:      string
  agentId:        string
  skillName:      string
  message:        string
  collections?:   string[]
  metadataFilter?: string
  tokenBudget?:   number
}

export interface ChatResult {
  content:    string
  sessionId:  string
  kbHits:     number
  memoryHits: number
  estTokens:  number
}

export async function chatBlocking(payload: ChatPayload): Promise<ChatResult> {
  return apiPost('/api/chat', payload)
}

/** Streaming: returns an AbortController + async generator of tokens */
export function chatStream(payload: ChatPayload): {
  abort: () => void
  stream: AsyncGenerator<string>
} {
  const controller = new AbortController()

  async function* gen() {
    const r = await fetch('/api/chat/stream', {
      method:  'POST',
      headers: headers(),
      body:    JSON.stringify(payload),
      signal:  controller.signal,
    })
    if (!r.ok) throw new Error(`Stream error: HTTP ${r.status}`)

    const reader  = r.body!.getReader()
    const decoder = new TextDecoder()
    let   buffer  = ''

    while (true) {
      const { done, value } = await reader.read()
      if (done) break

      buffer += decoder.decode(value, { stream: true })
      const lines = buffer.split('\n')
      buffer = lines.pop() ?? ''

      for (const line of lines) {
        if (!line.startsWith('data: ')) continue
        const raw = line.slice(6).trim()
        if (raw === '[DONE]') return
        try {
          const { token } = JSON.parse(raw) as { token: string }
          yield token
        } catch {
          // skip malformed
        }
      }
    }
  }

  return { abort: () => controller.abort(), stream: gen() }
}

// ── Ingest ────────────────────────────────────────────────────────────────────

export interface IngestPayload {
  collection:   string
  source:       string
  title:        string
  content:      string
  metadata?:    string
  chunkSize?:   number
  chunkOverlap?: number
}

export async function ingest(payload: IngestPayload) {
  return apiPost('/api/ingest', payload)
}

// ── Model capabilities ────────────────────────────────────────────────────────

export interface ModelCapabilities {
  supportsVision: boolean
  supportsTools:  boolean
  contextWindow:  number
  description:    string
}

export async function getModelCapabilities(): Promise<Record<string, ModelCapabilities>> {
  return apiGet('/api/models/capabilities')
}

// ── File extract ─────────────────────────────────────────────────────────────

export interface ExtractResult {
  filename:  string
  text:      string
  truncated: boolean
}

/** Upload a document (.docx/.xlsx/.pdf/.txt) and get extracted plain text back */
export async function extractFileText(file: File): Promise<ExtractResult> {
  const form = new FormData()
  form.append('file', file)
  const r = await fetch('/api/files/extract', {
    method:  'POST',
    headers: { Authorization: `Bearer ${token()}` },
    body:    form,
  })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}

// ── Health ────────────────────────────────────────────────────────────────────

export async function health(): Promise<boolean> {
  try {
    const r = await fetch('/health', { signal: AbortSignal.timeout(4000) })
    return r.ok
  } catch {
    return false
  }
}

// ── Server-side HTTP proxy (CORS bypass for tool calls) ───────────────────────

export interface ProxyRequestInit {
  url:          string
  method?:      string
  body?:        string
  contentType?: string
}

export async function proxyRequest(req: ProxyRequestInit): Promise<{
  ok: boolean
  status: number
  text: string
}> {
  const r = await fetch('/api/proxy', {
    method:  'POST',
    headers: headers(),
    body:    JSON.stringify({
      url:         req.url,
      method:      req.method      ?? 'GET',
      body:        req.body        ?? null,
      contentType: req.contentType ?? 'application/json',
    }),
  })
  const text = await r.text()
  return { ok: r.ok, status: r.status, text }
}
