import { useRef, useCallback } from 'react'
import {
  useStore,
  type ChatApiMessage,
  type Conversation,
} from '../store'
import { streamCompletion, BUILTIN_TOOLS } from '../api/llm'
import type { StreamEvent } from '../api/llm'
import { proxyRequest } from '../api'

// ── Helpers ───────────────────────────────────────────────────────────────────

/** Vision debug flag — enable with `localStorage.setItem('vision-debug','1')` */
function isVisionDebug(): boolean {
  try {
    if (localStorage.getItem('vision-debug') === '1') return true
    if (new URLSearchParams(location.search).get('vision-debug') === '1') return true
  } catch { /* ignore */ }
  return false
}

/**
 * Estimate token cost of a message.
 * - Image:  ~512 tokens (Gemma 4 tile-based, ~256-512 depending on resolution)
 * - Text:   length / 4  (rough 4 chars/token approximation)
 */
function estimateTokens(msg: ChatApiMessage): number {
  if (Array.isArray(msg.content)) {
    return (msg.content as any[]).reduce((n: number, p: any) => {
      if (p.type === 'image_url') return n + 512
      if (p.type === 'text')      return n + Math.ceil((p.text?.length ?? 0) / 4)
      return n
    }, 0)
  }
  if (typeof msg.content === 'string') return Math.ceil(msg.content.length / 4)
  return 0
}

function tokensPerSec(tokens: number, ms: number): number {
  if (ms <= 0) return 0
  return Math.round((tokens / (ms / 1000)) * 10) / 10
}

function buildToolList(custom: unknown[]): unknown[] {
  return [...BUILTIN_TOOLS, ...(Array.isArray(custom) ? custom : [])]
}

// ── Hook ──────────────────────────────────────────────────────────────────────

export function useGeneration() {
  const abortRef = useRef<AbortController | null>(null)

  // ── Tool execution (built-in tools) ────────────────────────────────────────
  const executeToolCall = useCallback(
    async (name: string, args: Record<string, unknown>): Promise<unknown> => {
      try {
        if (name === 'get_datetime') {
          const d = new Date()
          return {
            iso:      d.toISOString(),
            local:    d.toLocaleString(),
            timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
          }
        }

        if (name === 'calculate') {
          const expr = String(args.expression ?? '')
          // eslint-disable-next-line no-new-func
          const result = new Function('"use strict"; return (' + expr + ')')()
          return { expression: expr, result }
        }

        if (name === 'http_get') {
          const r = await proxyRequest({
            url:    String(args.url ?? ''),
            method: 'GET',
          })
          return { ok: r.ok, status: r.status, body: r.text }
        }

        if (name === 'http_post') {
          const r = await proxyRequest({
            url:    String(args.url ?? ''),
            method: 'POST',
            body:   typeof args.body === 'string'
                    ? (args.body as string)
                    : JSON.stringify(args.body ?? {}),
          })
          return { ok: r.ok, status: r.status, body: r.text }
        }

        return { error: `Unknown tool: ${name}` }
      } catch (e: unknown) {
        return { error: (e as Error).message }
      }
    },
    [],
  )

  // ── Stop the in-flight generation ──────────────────────────────────────────
  const stop = useCallback(() => {
    abortRef.current?.abort()
    abortRef.current = null
  }, [])

  // ── Build message list for the next LLM call ───────────────────────────────
  const buildMessages = (conv: Conversation): ChatApiMessage[] => {
    // Token budget for conversation history.
    // Model context = 16384. Reserve 750 for system prompt, 2048 for response.
    const MAX_HISTORY_TOKENS = 6000
    const systemPrompt = conv.settings.systemPrompt?.trim() ?? ''
    const systemTokens = Math.ceil(systemPrompt.length / 4)
    const history = conv.apiHistory

    // Sliding window: newest → oldest, keep messages that fit in token budget.
    // The LAST message is always included regardless of size (e.g. large image).
    let budget = MAX_HISTORY_TOKENS - systemTokens
    const kept: ChatApiMessage[] = []
    for (let i = history.length - 1; i >= 0; i--) {
      const cost = estimateTokens(history[i])
      if (i === history.length - 1 || budget - cost >= 0) {
        kept.unshift(history[i])
        budget -= cost
      } else {
        break
      }
    }

    const msgs: ChatApiMessage[] = []
    if (systemPrompt) msgs.push({ role: 'system', content: systemPrompt })
    for (const m of kept) msgs.push(m)
    return msgs
  }

  // ── Run one LLM call, drive a single placeholder assistant message ─────────
  const runOne = useCallback(
    async (
      convId: string,
      assistantMsgId: string,
    ): Promise<{ wasAborted: boolean; toolCalls: { id: string; name: string; args: Record<string, unknown> }[]; errorOccurred: boolean }> => {
      const store = useStore.getState()
      const conv = store.getConv(convId)
      if (!conv) {
        return { wasAborted: false, toolCalls: [], errorOccurred: true }
      }

      abortRef.current = new AbortController()
      const signal = abortRef.current.signal
      const start  = performance.now()

      // Capture content already present in the placeholder (used by Continue,
      // so we can isolate the new delta for the api history)
      const initialMsg = conv.messages.find(m => m.id === assistantMsgId)
      const baseContent = initialMsg?.content ?? ''

      let aborted       = false
      let errorOccurred = false
      const toolCalls: { id: string; name: string; args: Record<string, unknown> }[] = []

      const tokenStart = performance.now()
      let totalTokens  = 0
      let ttft: number | null = null
      let finishReason: string | null = null

      try {
        if (conv.settings.agentMode) {
          // ── RAG / agent pipeline (uses /api/chat/stream) ────────────────────
          const token = store.auth.token ?? localStorage.getItem('setllm-token') ?? ''
          const lastUser = [...conv.apiHistory].reverse().find(m => m.role === 'user')
          const userText = typeof lastUser?.content === 'string'
            ? lastUser.content
            : Array.isArray(lastUser?.content)
              ? (lastUser!.content as any[]).find((p: any) => p?.type === 'text')?.text ?? ''
              : ''

          const r = await fetch('/api/chat/stream', {
            method: 'POST',
            headers: {
              'Content-Type':  'application/json',
              'Authorization': `Bearer ${token}`,
            },
            body: JSON.stringify({
              sessionId:   convId,
              agentId:     'default',
              skillName:   conv.settings.skillId ?? 'chat',
              message:     userText,
              collections: conv.settings.skillCollection
                           ? [conv.settings.skillCollection]
                           : undefined,
            }),
            signal,
          })
          if (!r.ok || !r.body) {
            const errText = await r.text().catch(() => r.statusText)
            store.updateMessage(convId, assistantMsgId, {
              content: `⚠️ Error: ${r.status} ${errText.slice(0, 300)}`,
            })
            return { wasAborted: false, toolCalls: [], errorOccurred: true }
          }
          const reader  = r.body.getReader()
          const decoder = new TextDecoder()
          let   buffer  = ''
          while (true) {
            const { done, value } = await reader.read()
            if (done) break
            buffer += decoder.decode(value, { stream: true })
            const lines = buffer.split('\n')
            buffer = lines.pop() ?? ''
            for (const lineRaw of lines) {
              const line = lineRaw.trim()
              if (!line.startsWith('data:')) continue
              const data = line.slice(5).trim()
              if (data === '[DONE]') continue
              try {
                const { token: tok } = JSON.parse(data) as { token: string }
                if (typeof tok === 'string' && tok.length > 0) {
                  if (ttft == null) ttft = performance.now() - tokenStart
                  totalTokens += Math.max(1, Math.ceil(tok.length / 4))
                  store.appendToken(convId, assistantMsgId, tok)
                }
              } catch { /* ignore parse errors */ }
            }
          }
        } else {
          // ── Direct mode ─
          const token = store.auth.token ?? localStorage.getItem('setllm-token') ?? ''
          // Only LiteLLM aliases are valid — raw vLLM names (gemma4-26b etc.) fall back to 'chat'
          const VALID_MODELS = ['chat', 'code', 'reason', 'embed']
          const rawModel = conv.settings.model ?? store.activeModel
          const activeModelId = (rawModel && VALID_MODELS.includes(rawModel)) ? rawModel : 'chat'
          const caps = store.modelCapabilities[activeModelId]

          // Disable tools if: model doesn't support them OR latest message has image
          const lastUserMsg = [...conv.apiHistory].reverse().find(m => m.role === 'user')
          const lastMsgHasImage = Array.isArray(lastUserMsg?.content) &&
            (lastUserMsg!.content as any[]).some((c: any) => c.type === 'image_url')
          const modelSupportsTools = caps ? caps.supportsTools : true // default: allow
          const tools = (conv.settings.agenticEnabled && modelSupportsTools && !lastMsgHasImage)
            ? buildToolList(conv.settings.customTools)
            : undefined

          const rid = (window as any).__visionRid || '-'
          const builtMsgs = buildMessages(conv)
          if (lastMsgHasImage && isVisionDebug()) {
            const imgCount = builtMsgs.reduce((n, m) => n + (Array.isArray(m.content) ? (m.content as any[]).filter((c: any) => c.type === 'image_url').length : 0), 0)
            console.log(`[VISION ${rid}] 4. streamCompletion params — model=${conv.settings.model ?? 'chat'}, msgs=${builtMsgs.length}, images=${imgCount}, tools=${tools ? tools.length : 0}, stream=${conv.settings.stream}`)
          }

          const gen = streamCompletion(
            {
              messages:    builtMsgs,
              model:       activeModelId,
              temperature: conv.settings.temperature,
              maxTokens:   conv.settings.maxTokens,
              stream:      conv.settings.stream,
              tools,
            },
            token,
            signal,
          )

          for await (const ev of gen as AsyncGenerator<StreamEvent>) {
            if (ev.type === 'token') {
              if (ttft == null) ttft = performance.now() - tokenStart
              totalTokens += Math.max(0, Math.ceil(ev.text.length / 4))
              store.appendToken(convId, assistantMsgId, ev.text)
            } else if (ev.type === 'thinking') {
              if (ttft == null) ttft = performance.now() - tokenStart
              store.appendThinking(convId, assistantMsgId, ev.text)
            } else if (ev.type === 'tool_call') {
              toolCalls.push({ id: ev.id, name: ev.name, args: ev.args })
            } else if (ev.type === 'stats') {
              if (ev.ttft != null) ttft = ev.ttft
              if (ev.tokens > totalTokens) totalTokens = ev.tokens
              finishReason = ev.finishReason
            } else if (ev.type === 'warning') {
              store.updateMessage(convId, assistantMsgId, {
                content: ev.text,
                isWarning: true,
              })
            } else if (ev.type === 'error') {
              errorOccurred = true
              store.updateMessage(convId, assistantMsgId, {
                content: `⚠️ Error: ${ev.message}`,
              })
            }
          }
        }
      } catch (e: unknown) {
        if ((e as Error).name === 'AbortError') {
          aborted = true
        } else {
          errorOccurred = true
          store.updateMessage(convId, assistantMsgId, {
            content: `⚠️ Error: ${(e as Error).message}`,
          })
        }
      } finally {
        const elapsed = performance.now() - start
        const conv2 = store.getConv(convId)
        const msg = conv2?.messages.find(m => m.id === assistantMsgId)
        const truncated = finishReason === 'length'
        store.updateMessage(convId, assistantMsgId, {
          streaming: false,
          tokens:    totalTokens,
          truncated,
          meta:      finishReason ?? undefined,
        })
        store.setStats(convId, {
          ttft,
          tokens:       totalTokens,
          tokensPerSec: ttft != null ? tokensPerSec(totalTokens, elapsed - ttft) : null,
          elapsed,
          finishReason,
        })

        // Push assistant content to apiHistory (for follow-up turns).
        // Use only the newly generated delta (so Continue doesn't duplicate
        // the previous truncated content).
        const delta = msg && msg.content.startsWith(baseContent)
          ? msg.content.slice(baseContent.length)
          : msg?.content ?? ''
        if (msg && delta && !errorOccurred && !aborted) {
          store.addApiHistory(convId, {
            role:    'assistant',
            content: delta,
            ...(toolCalls.length > 0 ? {
              tool_calls: toolCalls.map(tc => ({
                id:   tc.id,
                type: 'function',
                function: { name: tc.name, arguments: JSON.stringify(tc.args) },
              })),
            } : {}),
          })
        }
      }

      return { wasAborted: aborted, toolCalls, errorOccurred }
    },
    [],
  )

  // ── Agentic loop (run, execute tools, repeat) ──────────────────────────────
  const runAgenticLoop = useCallback(
    async (convId: string, firstAssistantMsgId: string) => {
      const store = useStore.getState()
      let conv = store.getConv(convId)
      if (!conv) return

      let assistantMsgId = firstAssistantMsgId
      const maxLoops = conv.settings.maxAgentLoops ?? 10

      for (let iter = 0; iter < maxLoops; iter++) {
        const { wasAborted, toolCalls, errorOccurred } = await runOne(convId, assistantMsgId)
        if (wasAborted || errorOccurred) break
        if (toolCalls.length === 0) break

        // Render tool call messages + execute
        for (const tc of toolCalls) {
          const toolMsgId = store.addMessage(convId, {
            role:     'tool_call',
            content:  '',
            streaming: false,
            toolCall: { id: tc.id, name: tc.name, args: tc.args, status: 'running' },
          })

          const result = await executeToolCall(tc.name, tc.args)
          const status: 'done' | 'error' =
            (result && typeof result === 'object' && (result as any).error) ? 'error' : 'done'
          store.setToolCallResult(
            convId, toolMsgId, result, status,
            status === 'error' ? String((result as any).error) : undefined,
          )

          // Push to apiHistory: the tool's result
          store.addApiHistory(convId, {
            role:         'tool',
            tool_call_id: tc.id,
            name:         tc.name,
            content:      typeof result === 'string' ? result : JSON.stringify(result),
          })
        }

        // Start a fresh assistant placeholder for the next turn
        conv = store.getConv(convId)
        if (!conv) break
        assistantMsgId = store.addMessage(convId, {
          role:     'assistant',
          content:  '',
          streaming: true,
        })
      }
    },
    [executeToolCall, runOne],
  )

  // ── Auto-complete loop ─────────────────────────────────────────────────────
  const doContinue = useCallback(
    async (convId: string, _assistantIdx: number) => {
      const store = useStore.getState()
      const conv  = store.getConv(convId)
      if (!conv) return

      // Find the last assistant message
      const lastAssistant = [...conv.messages].reverse().find(m => m.role === 'assistant')
      if (!lastAssistant) return

      // Add "Continue" user turn to api history
      store.addApiHistory(convId, { role: 'user', content: 'Continue' })

      // Mark as streaming again, then run
      store.updateMessage(convId, lastAssistant.id, { streaming: true, truncated: false })
      const before = lastAssistant.content
      const result = await runOne(convId, lastAssistant.id)

      // Merge: prepend the previous content to what was streamed
      const conv2 = store.getConv(convId)
      const msg   = conv2?.messages.find(m => m.id === lastAssistant.id)
      if (msg) {
        // runOne replaced the streamed delta into msg.content; we want before + new
        // appendToken keeps appending, so msg.content is "before" + delta already
        // (because we marked it streaming=true without clearing it before runOne).
        // Wait — runOne appends to existing content (it never clears), so this is fine.
        // No further merge needed.
      }
      void before
      return result
    },
    [runOne],
  )

  const autoCompleteLoop = useCallback(
    async (convId: string) => {
      const store = useStore.getState()
      for (let i = 0; i < 6; i++) {
        const conv = store.getConv(convId)
        if (!conv) return
        const last = [...conv.messages].reverse().find(m => m.role === 'assistant')
        if (!last || !last.truncated) return
        await doContinue(convId, conv.messages.indexOf(last))
      }
    },
    [doContinue],
  )

  // ── Public API ─────────────────────────────────────────────────────────────

  const send = useCallback(
    async (text: string, image?: string): Promise<void> => {
      const rid = (window as any).__visionRid || '-'
      const dbg = isVisionDebug()
      if (image && dbg) console.log(`[VISION ${rid}] 2. send() — text="${text.slice(0,40)}" image=${image.length}c`)

      const store = useStore.getState()
      // Auto-create conversation if none is active
      const convId = store.currentId ?? store.newConversation()
      const conv = store.getConv(convId)
      if (!conv || conv.generating) return

      // Append user message (UI)
      store.addMessage(convId, {
        role: 'user', content: text, streaming: false, image,
      })

      // Append user message (api history)
      const apiContent: any = image
        ? [
            { type: 'text', text },
            { type: 'image_url', image_url: { url: image } },
          ]
        : text
      if (image && dbg) console.log(`[VISION ${rid}] 3. apiContent built — items=${(apiContent as any[]).map((p: any) => p.type).join(',')}`)
      store.addApiHistory(convId, { role: 'user', content: apiContent })

      // Assistant placeholder
      const assistantId = store.addMessage(convId, {
        role: 'assistant', content: '', streaming: true,
      })

      store.setGenerating(convId, true)

      try {
        const conv2 = store.getConv(convId)
        if (conv2?.settings.agenticEnabled && !conv2.settings.agentMode) {
          await runAgenticLoop(convId, assistantId)
        } else {
          await runOne(convId, assistantId)
        }

        // Auto-title
        const cAfter = store.getConv(convId)
        if (cAfter && cAfter.title === 'New conversation' && text) {
          store.renameConversation(convId, text.slice(0, 60))
        }

        // Auto-complete loop
        if (cAfter?.settings.autoComplete) {
          await autoCompleteLoop(convId)
        }
      } finally {
        store.setGenerating(convId, false)
        abortRef.current = null
      }
    },
    [runOne, runAgenticLoop, autoCompleteLoop],
  )

  const regenerate = useCallback(async () => {
    const store = useStore.getState()
    const convId = store.currentId
    if (!convId) return
    const conv = store.getConv(convId)
    if (!conv || conv.generating) return

    // Strip trailing assistant message(s) until we hit a user message
    const msgs = [...conv.messages]
    while (msgs.length && msgs[msgs.length - 1].role !== 'user') {
      const removed = msgs.pop()!
      store.removeMessage(convId, removed.id)
    }

    // Also strip trailing assistant from apiHistory
    const api = [...conv.apiHistory]
    while (api.length && api[api.length - 1].role !== 'user') {
      api.pop()
    }
    store.setApiHistory(convId, api)

    // New assistant placeholder
    const assistantId = store.addMessage(convId, {
      role: 'assistant', content: '', streaming: true,
    })
    store.setGenerating(convId, true)
    try {
      const conv2 = store.getConv(convId)
      if (conv2?.settings.agenticEnabled && !conv2.settings.agentMode) {
        await runAgenticLoop(convId, assistantId)
      } else {
        await runOne(convId, assistantId)
      }
    } finally {
      store.setGenerating(convId, false)
      abortRef.current = null
    }
  }, [runOne, runAgenticLoop])

  const continueResponse = useCallback(
    async (msgIdx: number) => {
      const store = useStore.getState()
      const convId = store.currentId
      if (!convId) return
      const conv = store.getConv(convId)
      if (!conv || conv.generating) return

      store.setGenerating(convId, true)
      try {
        await doContinue(convId, msgIdx)
      } finally {
        store.setGenerating(convId, false)
        abortRef.current = null
      }
    },
    [doContinue],
  )

  return { send, regenerate, continueResponse, stop, executeToolCall }
}
