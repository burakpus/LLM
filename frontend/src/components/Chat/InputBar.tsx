import { useState, useRef, useEffect, useCallback, useMemo } from 'react'
import type { KeyboardEvent, ClipboardEvent, ChangeEvent } from 'react'
import { useStore, t, DEFAULT_ENDPOINTS } from '../../store'
import type { OutputFormat } from '../../store'
import { proxyRequest, extractFileText } from '../../api'
import { improvePrompt } from '../../api/llm'
import { listTemplates } from '../../api/admin'
import type { PromptTemplate } from '../../api/admin'

// ── Format pill definitions ───────────────────────────────────────────────────
const FORMAT_PILLS: { id: OutputFormat; label: string; title: string }[] = [
  { id: 'free',     label: 'Serbest',  title: 'Serbest yanıt (varsayılan)'              },
  { id: 'json',     label: 'JSON',     title: 'Yanıtı yalnızca JSON olarak döndür'      },
  { id: 'markdown', label: 'Markdown', title: 'Yanıtı Markdown formatında yaz'          },
  { id: 'list',     label: 'Liste',    title: 'Yanıtı madde listesi olarak ver'         },
  { id: 'table',    label: 'Tablo',    title: 'Yanıtı Markdown tablosu olarak düzenle' },
]

// ── Token estimation (mirrors useGeneration logic) ────────────────────────────
function estimateInputTokens(text: string, hasImage: boolean, docText?: string): number {
  let n = Math.ceil(text.length / 4)
  if (hasImage) n += 512
  if (docText)  n += Math.ceil(docText.length / 4)
  return n
}

interface Props {
  onSend:     (text: string, image?: string) => void
  onStop:     () => void
  onRegenerate?: () => void
  generating: boolean
  fileContext?: string              // injected from project panel tab click
  onFileContextConsumed?: () => void
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

export default function InputBar({ onSend, onStop, onRegenerate, generating,
  fileContext, onFileContextConsumed }: Props) {
  const store = useStore()
  const conv  = store.currentConv()
  const [input, setInput] = useState('')
  const [attachedImage, setAttachedImage] = useState<string | null>(null)
  const [attachedDoc,     setAttachedDoc]     = useState<{ name: string; text: string; truncated: boolean } | null>(null)
  const [docLoading,      setDocLoading]      = useState(false)
  const [improving,       setImproving]       = useState(false)
  const [suggestion,      setSuggestion]      = useState<string | null>(null)
  const [suggestionEdit,  setSuggestionEdit]  = useState('')
  // slash picker
  const [templates,       setTemplates]       = useState<PromptTemplate[] | null>(null)
  const [showPicker,      setShowPicker]      = useState(false)
  // variable fill
  const [varFill, setVarFill] = useState<{ tmpl: PromptTemplate; vals: Record<string, string> } | null>(null)
  const ref     = useRef<HTMLTextAreaElement>(null)
  const fileRef = useRef<HTMLInputElement>(null)

  // Inject file context from project panel tab click
  useEffect(() => {
    if (fileContext) {
      setInput(prev => prev ? `${prev} ${fileContext}` : fileContext)
      ref.current?.focus()
      onFileContextConsumed?.()
    }
  }, [fileContext])

  const submit = () => {
    const text = input.trim()
    if ((!text && !attachedImage && !attachedDoc) || generating) return
    if (attachedImage && visionDebug()) {
      const rid = Math.random().toString(36).slice(2, 8)
      ;(window as any).__visionRid = rid
      console.log(`[VISION ${rid}] 1. submit() — text="${text.slice(0, 40)}" image=${attachedImage.length} chars`)
    }

    // Prepend document content to message as context block
    let fullText = text
    if (attachedDoc) {
      const truncNote = attachedDoc.truncated ? '\n...(dosya çok uzundu, ilk kısım alındı)' : ''
      fullText = `${text ? text + '\n\n' : ''}[Dosya eki: ${attachedDoc.name}]\n${attachedDoc.text}${truncNote}`
    }

    setInput('')
    setAttachedDoc(null)
    const img = attachedImage ?? undefined
    setAttachedImage(null)
    if (ref.current) ref.current.style.height = 'auto'
    onSend(fullText, img)
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
    e.target.value = ''

    if (f.type.startsWith('image/')) {
      // Image — attach as base64 for vision models
      const dataUrl = await fileToDataUrl(f)
      setAttachedImage(dataUrl)
    } else {
      // Document — extract text via backend
      setDocLoading(true)
      try {
        const result = await extractFileText(f)
        setAttachedDoc({ name: result.filename, text: result.text, truncated: result.truncated })
      } catch (err) {
        alert(`Dosya okunamadı: ${err instanceof Error ? err.message : 'Bilinmeyen hata'}`)
      } finally {
        setDocLoading(false)
      }
    }
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
      // Health check via .NET proxy — no direct vLLM access from browser
      const r = await proxyRequest({ url: `http://localhost:5080/health`, method: 'GET' })
      store.setActiveEndpoint(ep.model, idx)
      store.setSkill(null, null)
      if (conv) store.updateConvSettings(conv.id, {
        model:           ep.model,
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

  const hasText = !!input.trim() || !!attachedImage || !!attachedDoc

  // ── Token counter ──────────────────────────────────────────────────────────
  const inputTokens = useMemo(() =>
    estimateInputTokens(input, !!attachedImage, attachedDoc?.text),
    [input, attachedImage, attachedDoc]
  )
  const tokenColor = inputTokens > 5500 ? '#ef4444' : inputTokens > 3500 ? '#f59e0b' : 'var(--mute-2)'

  // ── Output format ──────────────────────────────────────────────────────────
  const outputFormat = settings?.outputFormat ?? 'free'
  const setOutputFormat = (fmt: OutputFormat) => {
    if (!conv) return
    store.updateConvSettings(conv.id, { outputFormat: fmt })
  }

  // ── Meta-prompt: improve current input ────────────────────────────────────
  const onImprove = async () => {
    const text = input.trim()
    if (!text || improving) return
    setImproving(true)
    setSuggestion(null)
    try {
      const tok = store.auth.token ?? localStorage.getItem('setllm-token') ?? ''
      const improved = await improvePrompt(text, tok)
      setSuggestion(improved)
      setSuggestionEdit(improved)
    } catch (e) {
      alert(`Prompt iyileştirme başarısız: ${e instanceof Error ? e.message : String(e)}`)
    } finally {
      setImproving(false)
    }
  }

  const applySuggestion = () => {
    setInput(suggestionEdit)
    setSuggestion(null)
    ref.current?.focus()
    setTimeout(resize, 0)
  }

  const dismissSuggestion = () => setSuggestion(null)

  // ── Slash picker ──────────────────────────────────────────────────────────
  const slashFilter = input.startsWith('/') ? input.slice(1).toLowerCase() : ''
  const pickerVisible = showPicker && input.startsWith('/')

  const filteredTemplates = (templates ?? []).filter(t =>
    !slashFilter ||
    t.name.toLowerCase().includes(slashFilter) ||
    t.collection.toLowerCase().includes(slashFilter)
  )

  const onInputChange = async (val: string) => {
    setInput(val)
    if (val.startsWith('/')) {
      setShowPicker(true)
      if (!templates) {
        try { setTemplates(await listTemplates()) } catch { setTemplates([]) }
      }
    } else {
      setShowPicker(false)
    }
  }

  const selectTemplate = (tmpl: PromptTemplate) => {
    setShowPicker(false)
    if (tmpl.variables.length === 0) {
      setInput(tmpl.content)
      ref.current?.focus()
      setTimeout(resize, 0)
    } else {
      const vals: Record<string, string> = {}
      for (const v of tmpl.variables) vals[v] = ''
      setVarFill({ tmpl, vals })
      setInput('')
    }
  }

  const applyVarFill = () => {
    if (!varFill) return
    let result = varFill.tmpl.content
    for (const [k, v] of Object.entries(varFill.vals))
      result = result.split(`{{${k}}}`).join(v)
    setInput(result)
    setVarFill(null)
    ref.current?.focus()
    setTimeout(resize, 0)
  }

  return (
    <div className="shrink-0 px-4 pt-2 pb-4 w-full" style={{ background: 'var(--bg)' }}>
      <div className="mx-auto" style={{ maxWidth: '760px' }}>
        {/* ── Variable fill panel ──────────────────────────────────────── */}
        {varFill && (
          <div className="mb-3 rounded-xl overflow-hidden"
               style={{ border: '1px solid rgba(138,180,248,0.4)', background: 'var(--surface)' }}>
            <div className="flex items-center gap-2 px-3 py-2"
                 style={{ background: 'rgba(138,180,248,0.08)', borderBottom: '1px solid rgba(138,180,248,0.2)' }}>
              <span className="text-sm">📝</span>
              <span className="text-xs font-semibold" style={{ color: 'var(--accent-hi)' }}>
                {varFill.tmpl.name}
              </span>
              <span className="text-[10px]" style={{ color: 'var(--mute)' }}>— değişkenleri doldurun</span>
              <button onClick={() => setVarFill(null)} className="ml-auto cursor-pointer text-sm" style={{ color: 'var(--mute)' }}>×</button>
            </div>
            <div className="px-3 py-3 space-y-2">
              {varFill.tmpl.variables.map(v => (
                <label key={v} className="flex items-center gap-2">
                  <span className="text-xs font-mono shrink-0 w-28 truncate"
                        style={{ color: 'var(--accent-hi)' }}>{`{{${v}}}`}</span>
                  <input
                    value={varFill.vals[v] ?? ''}
                    onChange={e => setVarFill(f => f ? { ...f, vals: { ...f.vals, [v]: e.target.value } } : null)}
                    placeholder={v}
                    className="flex-1 rounded-md px-2 py-1 text-sm outline-none"
                    style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
                  />
                </label>
              ))}
            </div>
            <div className="flex gap-2 px-3 pb-3">
              <button onClick={applyVarFill}
                      className="flex-1 py-1.5 rounded-lg text-xs font-semibold cursor-pointer"
                      style={{ background: 'var(--accent)', color: '#0b1929' }}>
                ✓ Uygula
              </button>
              <button onClick={() => setVarFill(null)}
                      className="px-4 py-1.5 rounded-lg text-xs cursor-pointer"
                      style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
                İptal
              </button>
            </div>
          </div>
        )}

        {/* ── Slash picker dropdown ─────────────────────────────────────── */}
        {pickerVisible && (
          <div className="mb-2 rounded-xl overflow-hidden shadow-lg"
               style={{ border: '1px solid var(--border)', background: 'var(--surface)', maxHeight: 260, overflowY: 'auto' }}>
            <div className="px-3 py-1.5 text-[10px] uppercase tracking-wider font-semibold"
                 style={{ color: 'var(--mute)', borderBottom: '1px solid var(--border)', background: 'var(--surface-2)' }}>
              Prompt Şablonları {slashFilter && `— "${slashFilter}"`}
            </div>
            {filteredTemplates.length === 0 ? (
              <div className="px-3 py-4 text-xs text-center" style={{ color: 'var(--mute)' }}>
                Şablon bulunamadı
              </div>
            ) : (
              filteredTemplates.map(tmpl => (
                <button key={tmpl.id} onClick={() => selectTemplate(tmpl)}
                        className="w-full text-left px-3 py-2.5 cursor-pointer transition"
                        style={{ borderBottom: '1px solid var(--border)' }}
                        onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
                        onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}>
                  <div className="flex items-center gap-2">
                    <span className="text-sm font-medium" style={{ color: 'var(--text)' }}>{tmpl.name}</span>
                    {tmpl.collection && (
                      <span className="text-[10px] px-1.5 py-0.5 rounded-full"
                            style={{ background: 'var(--surface-2)', color: 'var(--mute)' }}>
                        {tmpl.collection}
                      </span>
                    )}
                  </div>
                  {tmpl.variables.length > 0 && (
                    <div className="flex gap-1 mt-0.5">
                      {tmpl.variables.map(v => (
                        <span key={v} className="text-[9px] px-1 rounded font-mono"
                              style={{ background: 'rgba(138,180,248,0.12)', color: 'var(--accent-hi)' }}>
                          {`{{${v}}}`}
                        </span>
                      ))}
                    </div>
                  )}
                  <p className="text-[11px] mt-0.5 truncate" style={{ color: 'var(--mute)' }}>
                    {tmpl.content.slice(0, 80)}{tmpl.content.length > 80 ? '…' : ''}
                  </p>
                </button>
              ))
            )}
          </div>
        )}

        {/* ── Meta-prompt suggestion panel ─────────────────────────────── */}
        {suggestion !== null && (
          <div className="mb-3 rounded-xl overflow-hidden"
               style={{ border: '1px solid rgba(138,180,248,0.4)', background: 'var(--surface)' }}>
            {/* Header */}
            <div className="flex items-center gap-2 px-3 py-2"
                 style={{ background: 'rgba(138,180,248,0.08)', borderBottom: '1px solid rgba(138,180,248,0.2)' }}>
              <span className="text-sm">✨</span>
              <span className="text-xs font-semibold" style={{ color: 'var(--accent-hi)' }}>
                İyileştirilmiş Prompt
              </span>
              <span className="text-[10px] ml-1" style={{ color: 'var(--mute)' }}>
                — düzenleyebilirsiniz
              </span>
              <button onClick={dismissSuggestion} className="ml-auto cursor-pointer text-sm leading-none"
                      style={{ color: 'var(--mute)' }} title="Kapat">×</button>
            </div>
            {/* Editable suggestion */}
            <textarea
              value={suggestionEdit}
              onChange={e => setSuggestionEdit(e.target.value)}
              rows={Math.min(8, suggestionEdit.split('\n').length + 1)}
              className="w-full px-3 py-2.5 text-sm outline-none resize-none bg-transparent scrollbar-thin"
              style={{ color: 'var(--text)', lineHeight: 1.6 }}
            />
            {/* Actions */}
            <div className="flex gap-2 px-3 pb-3">
              <button
                onClick={applySuggestion}
                className="flex-1 py-1.5 rounded-lg text-xs font-semibold cursor-pointer transition"
                style={{ background: 'var(--accent)', color: '#0b1929' }}
              >
                ✓ Uygula
              </button>
              <button
                onClick={dismissSuggestion}
                className="px-4 py-1.5 rounded-lg text-xs cursor-pointer transition"
                style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}
              >
                İptal
              </button>
            </div>
          </div>
        )}

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
              × Kaldır
            </button>
          </div>
        )}

        {/* Attached document chip */}
        {attachedDoc && (
          <div className="mb-2 flex items-center gap-2 p-1.5 rounded-xl w-fit"
               style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)' }}>
            <span style={{ fontSize: 18, lineHeight: 1 }}>📄</span>
            <div className="flex flex-col min-w-0">
              <span className="text-xs font-medium truncate" style={{ color: 'var(--text)', maxWidth: 200 }}
                    title={attachedDoc.name}>
                {attachedDoc.name.length > 28 ? attachedDoc.name.slice(0, 26) + '…' : attachedDoc.name}
              </span>
              {attachedDoc.truncated && (
                <span className="text-[10px]" style={{ color: 'var(--mute)' }}>kısaltıldı</span>
              )}
            </div>
            <button
              onClick={() => setAttachedDoc(null)}
              className="text-xs px-2 py-1 rounded-full cursor-pointer transition shrink-0"
              style={{ color: 'var(--mute)' }}
              onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-2)'}
              onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}
            >
              × Kaldır
            </button>
          </div>
        )}

        {/* Document loading indicator */}
        {docLoading && (
          <div className="mb-2 text-xs flex items-center gap-1.5" style={{ color: 'var(--mute)' }}>
            <span className="animate-spin inline-block">⏳</span> Dosya okunuyor...
          </div>
        )}

        {/* Main input — rounded-full container */}
        <div className="gemini-input flex items-end gap-1 px-2 py-2">
          {/* Attach button — always visible; accepts images on vision models, docs always */}
          <button
            onClick={() => fileRef.current?.click()}
            disabled={docLoading}
            title={modelSupportsVision ? 'Resim veya dosya ekle (.docx, .xlsx, .pdf, .txt)' : 'Dosya ekle (.docx, .xlsx, .pdf, .txt)'}
            className="h-10 w-10 rounded-full flex items-center justify-center cursor-pointer transition shrink-0 disabled:opacity-40 disabled:cursor-not-allowed"
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
            accept={modelSupportsVision
              ? 'image/*,.docx,.doc,.xlsx,.xls,.pdf,.txt,.md,.csv'
              : '.docx,.doc,.xlsx,.xls,.pdf,.txt,.md,.csv'}
            className="hidden"
            onChange={onFile}
          />

          <textarea
            ref={ref}
            value={input}
            onChange={e => { onInputChange(e.target.value); resize() }}
            onKeyDown={onKey}
            onPaste={onPaste}
            placeholder={t('placeholder') || 'Ask SET LLM...'}
            rows={1}
            className="flex-1 bg-transparent px-2 py-2.5 text-[15px] resize-none outline-none leading-relaxed min-h-10 max-h-52 scrollbar-thin"
            style={{ color: 'var(--text)' }}
          />

          <button
            onClick={generating ? onStop : submit}
            disabled={(!generating && !hasText) || docLoading}
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
          {(store.endpoints.length ? store.endpoints : DEFAULT_ENDPOINTS).map((ep, i) => {
            const active = store.activeEpIdx === i
            return (
              <button
                key={ep.name + i}
                onClick={() => onEndpointPing(i)}
                className={`pill ${active ? 'active' : ''}`}
                title={ep.model}
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

        {/* Format + token row */}
        <div className="mt-1.5 flex items-center gap-1 flex-wrap">
          {/* Format pills */}
          {FORMAT_PILLS.map(fp => {
            const active = outputFormat === fp.id
            return (
              <button
                key={fp.id}
                onClick={() => setOutputFormat(fp.id)}
                title={fp.title}
                className="pill"
                style={{
                  background:  active ? (fp.id === 'free' ? 'var(--surface-hi)' : 'rgba(138,180,248,0.15)') : 'transparent',
                  color:       active ? (fp.id === 'free' ? 'var(--text-2)' : 'var(--accent-hi)') : 'var(--mute)',
                  border:      active ? `1px solid ${fp.id === 'free' ? 'var(--border)' : 'rgba(138,180,248,0.35)'}` : '1px solid transparent',
                  fontSize:    '10px',
                  padding:     '2px 7px',
                }}
              >
                {fp.label}
              </button>
            )
          })}

          {/* ✨ Improve prompt button — only when there's meaningful input */}
          {input.trim().length > 8 && (
            <button
              onClick={onImprove}
              disabled={improving}
              title="Promptu AI ile iyileştir"
              className="ml-auto flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] cursor-pointer transition disabled:opacity-50 disabled:cursor-not-allowed"
              style={{
                background: improving ? 'rgba(138,180,248,0.15)' : 'transparent',
                border:     '1px solid rgba(138,180,248,0.3)',
                color:      'var(--accent-hi)',
              }}
              onMouseEnter={e => { if (!improving) (e.currentTarget as HTMLElement).style.background = 'rgba(138,180,248,0.12)' }}
              onMouseLeave={e => { if (!improving) (e.currentTarget as HTMLElement).style.background = 'transparent' }}
            >
              {improving ? (
                <svg className="w-3 h-3 animate-spin" fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"/>
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z"/>
                </svg>
              ) : (
                <span>✨</span>
              )}
              {improving ? 'İyileştiriliyor…' : 'İyileştir'}
            </button>
          )}

          {/* Token counter */}
          {inputTokens > 0 && (
            <span
              className={`${input.trim().length <= 8 ? 'ml-auto' : ''} text-[10px] tabular-nums`}
              style={{ color: tokenColor }}
              title={`Tahmini ${inputTokens} token (6000 pencere sınırı)`}
            >
              ~{inputTokens.toLocaleString()} tok
            </span>
          )}
        </div>
      </div>
    </div>
  )
}
