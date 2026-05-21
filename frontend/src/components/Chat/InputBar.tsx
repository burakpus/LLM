import { useState, useRef, useEffect } from 'react'
import type { KeyboardEvent, ClipboardEvent, ChangeEvent } from 'react'
import { useStore, t } from '../../store'
import { proxyRequest } from '../../api'

interface Props {
  onSend:     (text: string, image?: string) => void
  onStop:     () => void
  onRegenerate?: () => void
  generating: boolean
}

/** Debug flag: enable with `localStorage.setItem('vision-debug','1')` or ?vision-debug=1 in URL */
function visionDebug(): boolean {
  try {
    if (localStorage.getItem('vision-debug') === '1') return true
    if (new URLSearchParams(location.search).get('vision-debug') === '1') return true
  } catch { /* ignore */ }
  return false
}

/** Resize image to max 768px and convert to JPEG base64 (reduces token usage) */
async function fileToDataUrl(file: File): Promise<string> {
  if (visionDebug()) console.log(`[VISION] 0a. fileToDataUrl — file=${file.name} type=${file.type} size=${file.size}B`)
  return new Promise((resolve, reject) => {
    const r = new FileReader()
    r.onerror = () => reject(r.error)
    r.onload = () => {
      const img = new Image()
      img.onload = () => {
        const origW = img.width, origH = img.height
        const MAX = 768
        let { width, height } = img
        if (width > MAX || height > MAX) {
          if (width > height) { height = Math.round(height * MAX / width); width = MAX }
          else                { width  = Math.round(width  * MAX / height); height = MAX }
        }
        const canvas = document.createElement('canvas')
        canvas.width = width; canvas.height = height
        canvas.getContext('2d')!.drawImage(img, 0, 0, width, height)
        const dataUrl = canvas.toDataURL('image/jpeg', 0.85)
        if (visionDebug()) console.log(`[VISION] 0b. resized ${origW}x${origH} → ${width}x${height}, JPEG dataURL=${dataUrl.length} chars`)
        resolve(dataUrl)
      }
      img.onerror = () => reject(new Error('Image load failed'))
      img.src = String(r.result)
    }
    r.readAsDataURL(file)
  })
}

export default function InputBar({ onSend, onStop, onRegenerate, generating }: Props) {
  const store = useStore()
  const conv  = store.currentConv()
  const [input, setInput] = useState('')
  const [attachedImage, setAttachedImage] = useState<string | null>(null)
  const ref     = useRef<HTMLTextAreaElement>(null)
  const fileRef = useRef<HTMLInputElement>(null)

  const submit = () => {
    const text = input.trim()
    if ((!text && !attachedImage) || generating) return
    if (attachedImage && visionDebug()) {
      const rid = Math.random().toString(36).slice(2, 8)
      ;(window as any).__visionRid = rid
      console.log(`[VISION ${rid}] 1. submit() — text="${text.slice(0, 40)}" image=${attachedImage.length} chars`)
    }
    setInput('')
    const img = attachedImage ?? undefined
    setAttachedImage(null)
    if (ref.current) ref.current.style.height = 'auto'
    onSend(text, img)
  }

  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      submit()
    }
  }

  const onPaste = async (e: ClipboardEvent<HTMLTextAreaElement>) => {
    const items = e.clipboardData?.items
    if (!items) return
    for (const it of Array.from(items)) {
      if (it.type.startsWith('image/')) {
        const file = it.getAsFile()
        if (file) {
          e.preventDefault()
          const dataUrl = await fileToDataUrl(file)
          setAttachedImage(dataUrl)
          return
        }
      }
    }
  }

  const onFile = async (e: ChangeEvent<HTMLInputElement>) => {
    const f = e.target.files?.[0]
    if (!f) return
    const dataUrl = await fileToDataUrl(f)
    setAttachedImage(dataUrl)
    e.target.value = ''
  }

  const resize = () => {
    const el = ref.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = Math.min(el.scrollHeight, 200) + 'px'
  }

  useEffect(() => { resize() }, [input])

  const onEndpointPing = async (idx: number) => {
    const ep = store.endpoints[idx]
    if (!ep) return
    store.setStatus('connecting', null)
    try {
      const r = await proxyRequest({
        url: `http://${ep.host}:${ep.port}/health/liveliness`,
        method: 'GET',
      })
      const base = ep.port === 4000 ? null : `http://${ep.host}:${ep.port}`
      store.setActiveEndpoint(base, ep.model, idx)
      store.setSkill(null, null)
      if (conv) store.updateConvSettings(conv.id, {
        baseUrl:         base,
        model:           ep.model,
        endpointIdx:     idx,
        skillId:         null,
        skillName:       null,
        skillCollection: null,
        systemPrompt:    '',
        agentMode:       false,
      })
      store.setStatus(r.ok ? 'connected' : 'unreachable', r.ok)
    } catch {
      store.setStatus('unreachable', false)
    }
  }

  const settings    = conv?.settings
  const agentMode   = !!settings?.agentMode
  const toggleAgent = () => {
    if (!conv) return
    store.updateConvSettings(conv.id, { agentMode: !agentMode })
  }

  // Check if active model supports vision (default: allow if caps not loaded yet)
  const activeModelId = settings?.model ?? store.activeModel ?? 'chat'
  const caps = store.modelCapabilities[activeModelId]
  const modelSupportsVision = caps ? caps.supportsVision : true

  const hasText = !!input.trim() || !!attachedImage

  return (
    <div className="shrink-0 px-4 pt-2 pb-4 w-full" style={{ background: 'var(--bg)' }}>
      <div className="mx-auto" style={{ maxWidth: '760px' }}>
        {/* Attached image preview */}
        {attachedImage && (
          <div className="mb-2 flex items-center gap-2 p-1.5 rounded-xl w-fit"
               style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)' }}>
            <img src={attachedImage} alt="attached" className="h-12 w-12 rounded-lg object-cover" />
            <button
              onClick={() => setAttachedImage(null)}
              className="text-xs px-2 py-1 rounded-full cursor-pointer transition"
              style={{ color: 'var(--mute)' }}
              onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-2)'}
              onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}
            >
              × Remove
            </button>
          </div>
        )}

        {/* Main input — rounded-full container */}
        <div className="gemini-input flex items-end gap-1 px-2 py-2">
          {/* Attach button — only shown if active model supports vision */}
          {modelSupportsVision && (
            <>
              <button
                onClick={() => fileRef.current?.click()}
                title={t('attach')}
                className="h-10 w-10 rounded-full flex items-center justify-center cursor-pointer transition shrink-0"
                style={{ color: 'var(--text-2)' }}
                onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
                onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}
              >
                <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round"
                    d="M15.172 7l-6.586 6.586a2 2 0 102.828 2.828l6.414-6.586a4 4 0 00-5.656-5.656l-6.415 6.585a6 6 0 108.486 8.486L20.5 13" />
                </svg>
              </button>
              <input
                ref={fileRef}
                type="file"
                accept="image/*"
                className="hidden"
                onChange={onFile}
              />
            </>
          )}

          <textarea
            ref={ref}
            value={input}
            onChange={e => { setInput(e.target.value); resize() }}
            onKeyDown={onKey}
            onPaste={onPaste}
            placeholder={t('placeholder') || 'Ask SET LLM...'}
            rows={1}
            className="flex-1 bg-transparent px-2 py-2.5 text-[15px] resize-none outline-none leading-relaxed min-h-10 max-h-52 scrollbar-thin"
            style={{ color: 'var(--text)' }}
          />

          <button
            onClick={generating ? onStop : submit}
            disabled={!generating && !hasText}
            className="h-10 w-10 rounded-full flex items-center justify-center shrink-0 transition cursor-pointer disabled:cursor-not-allowed"
            style={generating
              ? { background: 'rgba(234,67,53,0.18)', color: '#ea4335' }
              : hasText
                ? { background: 'var(--accent)', color: '#0b1929' }
                : { background: 'transparent', color: 'var(--mute-2)' }
            }
            title={generating ? t('stop') : t('send')}
          >
            {generating ? (
              <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
                <rect x="6" y="6" width="12" height="12" rx="1.5" />
              </svg>
            ) : (
              <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 19V5m-7 7l7-7 7 7" />
              </svg>
            )}
          </button>
        </div>

        {/* Bottom row — endpoints + agent + status */}
        <div className="mt-2 flex items-center gap-1.5 flex-wrap">
          {store.endpoints.map((ep, i) => {
            const active = store.activeEpIdx === i
            return (
              <button
                key={ep.name + i}
                onClick={() => onEndpointPing(i)}
                className={`pill ${active ? 'active' : ''}`}
                title={`http://${ep.host}:${ep.port} (${ep.model})`}
              >
                <span className={`status-dot ${active && store.statusOk ? 'ok' : active && store.statusOk === false ? 'bad' : ''}`} />
                {ep.name}
              </button>
            )
          })}

          <button onClick={toggleAgent} className={`pill ${agentMode ? 'active' : ''}`}>
            <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round"
                d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
            </svg>
            RAG
          </button>

          {onRegenerate && (
            <button onClick={onRegenerate}
                    disabled={generating}
                    className="pill disabled:opacity-50 disabled:cursor-not-allowed">
              <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round"
                  d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
              </svg>
              {t('regenerate')}
            </button>
          )}

          <div className="ml-auto flex items-center gap-1.5 text-[11px]" style={{ color: 'var(--mute-2)' }}>
            <span className={`status-dot ${store.statusOk ? 'ok' : store.statusOk === false ? 'bad' : ''}`} />
            <span>{store.status}</span>
          </div>
        </div>
      </div>
    </div>
  )
}
