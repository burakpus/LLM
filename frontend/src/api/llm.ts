// =============================================================================
// LLM client — talks to /api/llm/completions (server-side LiteLLM proxy)
// or directly to a vLLM endpoint (when an explicit baseUrl is provided).
// =============================================================================

// ── Built-in tool schemas ────────────────────────────────────────────────────
export const BUILTIN_TOOLS = [
  {
    type: 'function',
    function: {
      name: 'get_datetime',
      description: 'Returns current date, time and timezone.',
      parameters: { type: 'object', properties: {} },
    },
  },
  {
    type: 'function',
    function: {
      name: 'calculate',
      description: 'Evaluates a JS math expression. Math object available.',
      parameters: {
        type: 'object',
        properties: {
          expression: { type: 'string', description: 'e.g. "Math.sqrt(144)"' },
        },
        required: ['expression'],
      },
    },
  },
  {
    type: 'function',
    function: {
      name: 'http_get',
      description: 'HTTP GET via server-side proxy.',
      parameters: {
        type: 'object',
        properties: { url: { type: 'string' } },
        required: ['url'],
      },
    },
  },
  {
    type: 'function',
    function: {
      name: 'http_post',
      description: 'HTTP POST via server-side proxy.',
      parameters: {
        type: 'object',
        properties: {
          url:  { type: 'string' },
          body: { type: 'object' },
        },
        required: ['url', 'body'],
      },
    },
  },
] as const

// ── Stream event types ───────────────────────────────────────────────────────
export type StreamEvent =
  | { type: 'token';     text: string }
  | { type: 'thinking';  text: string }
  | { type: 'tool_call'; id: string; name: string; args: Record<string, unknown> }
  | { type: 'stats';     ttft: number | null; tokens: number; finishReason: string | null }
  | { type: 'error';     message: string }
  | { type: 'warning';   text: string }

// ── Completion parameters ────────────────────────────────────────────────────
export type ChatContent =
  | string
  | Array<{
      type:       string
      text?:      string
      image_url?: { url: string }
    }>

export interface ChatMessage {
  role:          string
  content:       ChatContent
  name?:         string
  tool_calls?:   unknown[]
  tool_call_id?: string
}

export interface CompletionParams {
  messages:    ChatMessage[]
  model?:      string
  temperature?: number
  maxTokens?:  number
  stream?:     boolean
  tools?:      unknown[]
  toolChoice?: string
  /**
   * Optional explicit base URL — when set, bypasses /api/llm/completions
   * and posts directly to `${baseUrl}/v1/chat/completions` (used for direct
   * vLLM endpoints; no auth header is added in that case).
   */
  baseUrl?:    string
}

// ── Streaming completion generator ───────────────────────────────────────────
//
// Posts to /api/llm/completions (or a direct baseUrl if provided) and yields:
//   • token     — content tokens (excluding <think>...</think>)
//   • thinking  — content inside <think>...</think>
//   • tool_call — accumulated tool call deltas
//   • stats     — emitted once at end (TTFT, tokens, finishReason)
//   • error     — on network / parse error
//
export async function* streamCompletion(
  params: CompletionParams,
  token:  string,
  signal: AbortSignal,
): AsyncGenerator<StreamEvent> {
  const start = performance.now()
  let ttft:  number | null = null
  let tokens = 0
  let finishReason: string | null = null

  // Build OpenAI-compatible request body
  const body: Record<string, unknown> = {
    messages:    params.messages,
    stream:      params.stream ?? true,
    temperature: params.temperature ?? 0.7,
  }
  if (params.model)      body.model       = params.model
  if (params.maxTokens)  body.max_tokens  = params.maxTokens
  if (params.tools && params.tools.length > 0) {
    body.tools       = params.tools
    body.tool_choice = params.toolChoice ?? 'auto'
  }

  // Decide endpoint + auth
  let url: string
  const headers: Record<string, string> = { 'Content-Type': 'application/json' }
  if (params.baseUrl) {
    url = params.baseUrl.replace(/\/$/, '') + '/v1/chat/completions'
    // direct vLLM endpoint — no auth
  } else {
    url = '/api/llm/completions'
    if (token) headers['Authorization'] = `Bearer ${token}`
  }

  const hasVision = params.messages.some(m =>
    Array.isArray(m.content) && (m.content as any[]).some((c: any) => c.type === 'image_url')
  )
  const rid = (window as any).__visionRid || '-'
  const reqBytes = JSON.stringify(body).length
  if (hasVision) console.log(`[VISION ${rid}] 5. fetch POST ${url} — body=${reqBytes}B`)

  let resp: Response
  try {
    resp = await fetch(url, {
      method: 'POST',
      headers,
      body:   JSON.stringify(body),
      signal,
    })
  } catch (e: unknown) {
    if (hasVision) console.error(`[VISION ${rid}] 6. fetch FAILED — ${(e as Error).message}`)
    yield { type: 'error', message: (e as Error).message || 'Network error' }
    return
  }

  if (hasVision) console.log(`[VISION ${rid}] 6. fetch DONE — status=${resp.status} ${resp.statusText}`)

  if (!resp.ok) {
    const errText = await resp.text().catch(() => resp.statusText)
    if (hasVision) console.warn(`[VISION ${rid}] 7. non-OK body=${errText.slice(0, 200)}`)
    // 503 = model warming up → friendly warning, not a hard error
    if (resp.status === 503) {
      let msg = '⏳ Model henüz yükleniyor, lütfen birkaç saniye sonra tekrar deneyin.'
      try {
        const parsed = JSON.parse(errText)
        if (parsed?.message) msg = `⏳ ${parsed.message}`
      } catch { /* use default */ }
      yield { type: 'warning', text: msg } as any
      return
    }
    yield { type: 'error', message: `HTTP ${resp.status}: ${errText.slice(0, 500)}` }
    return
  }
  if (hasVision) console.log(`[VISION ${rid}] 7. response OK — streaming...`)

  // ── Non-streaming path ─────────────────────────────────────────────────────
  if (params.stream === false) {
    try {
      const json: any = await resp.json()
      const choice = json.choices?.[0]
      const content: string = choice?.message?.content ?? ''
      finishReason = choice?.finish_reason ?? null
      ttft = performance.now() - start

      const { content: cleanContent, thinking } = stripThinking(content)
      if (thinking) yield { type: 'thinking', text: thinking }
      if (cleanContent) yield { type: 'token', text: cleanContent }
      tokens = approxTokens(cleanContent)

      // Tool calls (non-streaming)
      const toolCalls = choice?.message?.tool_calls
      if (Array.isArray(toolCalls)) {
        for (const tc of toolCalls) {
          let args: Record<string, unknown> = {}
          try { args = JSON.parse(tc.function?.arguments ?? '{}') } catch { /* ignore */ }
          yield {
            type: 'tool_call',
            id:   tc.id ?? '',
            name: tc.function?.name ?? '',
            args,
          }
        }
      }

      yield { type: 'stats', ttft, tokens, finishReason }
    } catch (e: unknown) {
      yield { type: 'error', message: (e as Error).message }
    }
    return
  }

  // ── Streaming SSE path ────────────────────────────────────────────────────
  if (!resp.body) {
    yield { type: 'error', message: 'No response body' }
    return
  }
  const reader  = resp.body.getReader()
  const decoder = new TextDecoder()
  let   buffer  = ''
  let   inThink = false
  const toolBuf: Record<string, { id: string; name: string; argsStr: string }> = {}

  try {
    while (true) {
      const { done, value } = await reader.read()
      if (done) break
      buffer += decoder.decode(value, { stream: true })

      const lines = buffer.split('\n')
      buffer = lines.pop() ?? ''

      for (const lineRaw of lines) {
        const line = lineRaw.trim()
        if (!line || !line.startsWith('data:')) continue
        const data = line.slice(5).trim()
        if (data === '[DONE]') {
          // flush any tool calls accumulated
          for (const k of Object.keys(toolBuf)) {
            const t = toolBuf[k]
            let args: Record<string, unknown> = {}
            try { args = JSON.parse(t.argsStr || '{}') } catch { /* ignore */ }
            yield { type: 'tool_call', id: t.id, name: t.name, args }
          }
          yield { type: 'stats', ttft, tokens, finishReason }
          return
        }

        let chunk: any
        try { chunk = JSON.parse(data) } catch { continue }

        const choice = chunk.choices?.[0]
        if (!choice) continue
        if (choice.finish_reason) finishReason = choice.finish_reason

        const delta = choice.delta ?? {}

        // ── Content tokens ────────────────────────────────────────────────
        const piece: string | undefined = delta.content
        if (typeof piece === 'string' && piece.length > 0) {
          if (ttft == null) ttft = performance.now() - start

          // walk the string and split into thinking / content based on <think> tags
          let i = 0
          while (i < piece.length) {
            if (!inThink) {
              const open = piece.indexOf('<think>', i)
              if (open === -1) {
                const out = piece.slice(i)
                tokens += approxTokens(out)
                yield { type: 'token', text: out }
                break
              } else {
                if (open > i) {
                  const out = piece.slice(i, open)
                  tokens += approxTokens(out)
                  yield { type: 'token', text: out }
                }
                inThink = true
                i = open + '<think>'.length
              }
            } else {
              const close = piece.indexOf('</think>', i)
              if (close === -1) {
                const out = piece.slice(i)
                yield { type: 'thinking', text: out }
                break
              } else {
                if (close > i) {
                  const out = piece.slice(i, close)
                  yield { type: 'thinking', text: out }
                }
                inThink = false
                i = close + '</think>'.length
              }
            }
          }
        }

        // ── Reasoning content (Anthropic-style, vLLM ext) ────────────────
        const reasoning: string | undefined =
          delta.reasoning_content ?? delta.reasoning
        if (typeof reasoning === 'string' && reasoning.length > 0) {
          if (ttft == null) ttft = performance.now() - start
          yield { type: 'thinking', text: reasoning }
        }

        // ── Tool call deltas ──────────────────────────────────────────────
        const tcs = delta.tool_calls
        if (Array.isArray(tcs)) {
          for (const tc of tcs) {
            const idx = tc.index ?? 0
            const key = String(idx)
            const entry = toolBuf[key] ?? { id: '', name: '', argsStr: '' }
            if (tc.id) entry.id = tc.id
            if (tc.function?.name) entry.name = tc.function.name
            if (tc.function?.arguments) entry.argsStr += tc.function.arguments
            toolBuf[key] = entry
          }
        }
      }
    }
    // stream ended without [DONE]
    for (const k of Object.keys(toolBuf)) {
      const t = toolBuf[k]
      let args: Record<string, unknown> = {}
      try { args = JSON.parse(t.argsStr || '{}') } catch { /* ignore */ }
      yield { type: 'tool_call', id: t.id, name: t.name, args }
    }
    yield { type: 'stats', ttft, tokens, finishReason }
  } catch (e: unknown) {
    if ((e as Error).name === 'AbortError') {
      yield { type: 'stats', ttft, tokens, finishReason }
      return
    }
    yield { type: 'error', message: (e as Error).message }
  }
}

// ── Utilities ────────────────────────────────────────────────────────────────

function approxTokens(s: string): number {
  // very rough — 4 chars/token
  return Math.max(0, Math.ceil(s.length / 4))
}

function stripThinking(content: string): { content: string; thinking: string } {
  let out = ''
  let think = ''
  let i = 0
  while (i < content.length) {
    const open = content.indexOf('<think>', i)
    if (open === -1) { out += content.slice(i); break }
    out += content.slice(i, open)
    const close = content.indexOf('</think>', open + 7)
    if (close === -1) { think += content.slice(open + 7); break }
    think += content.slice(open + 7, close)
    i = close + 8
  }
  return { content: out, thinking: think }
}
