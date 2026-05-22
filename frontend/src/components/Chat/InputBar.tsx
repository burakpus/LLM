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
  // bottom bar dropdowns
  const [showEndDrop, setShowEndDrop] = useState(false)
  const [showModDrop, setShowModDrop] = useState(false)
  const [showFmtDrop, setShowFmtDrop] = useState(false)
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

  const closeDropdowns = useCallback(() => {
    setShowEndDrop(false); setShowModDrop(false); setShowFmtDrop(false)
  }, [])

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

  const endpoints   = store.endpoints.length ? store.endpoints : DEFAULT_ENDPOINTS
  const activeEp    = endpoints[store.activeEpIdx ?? 0] ?? endpoints[0]
  const formatLabel = FORMAT_PILLS.find(f => f.id === outputFormat)?.label ?? 'Serbest'

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

        {/* ── Single action row ─────────────────────────────────────────── */}
        {/* Transparent overlay — closes any open dropdown on outside click */}
        {(showEndDrop || showModDrop || showFmtDrop) && (
          <div className="fixed inset-0 z-40" onClick={closeDropdowns} />
        )}

        <div className="mt-2 flex items-center gap-1.5">

          {/* 1. Endpoint dropdown */}
          <div className="relative z-50">
            <button
              onClick={() => { setShowEndDrop(o => !o); setShowModDrop(false); setShowFmtDrop(false) }}
              className="pill active"
            >
              <span className={`status-dot ${store.statusOk ? 'ok' : store.statusOk === false ? 'bad' : ''}`} />
              {activeEp?.name ?? 'Chating'}
              <svg className="w-2.5 h-2.5 ml-0.5 opacity-50" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
              </svg>
            </button>
            {showEndDrop && (
              <div className="absolute left-0 bottom-full mb-2 rounded-xl shadow-2xl overflow-hidden z-50"
                   style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', minWidth: 140 }}>
                {endpoints.map((ep, i) => {
                  const isAct = store.activeEpIdx === i
                  return (
                    <button key={ep.name + i}
                            onClick={() => { onEndpointPing(i); setShowEndDrop(false) }}
                            className="w-full flex items-center gap-2 px-3 py-2.5 text-xs cursor-pointer text-left transition"
                            style={{ background: isAct ? 'rgba(138,180,248,0.15)' : 'transparent',
                                     color: isAct ? 'var(--accent-hi)' : 'var(--text-2)',
                                     borderBottom: i < endpoints.length - 1 ? '1px solid var(--border)' : 'none' }}
                            onMouseEnter={e => { if (!isAct) (e.currentTarget as HTMLElement).style.background = 'var(--surface-2)' }}
                            onMouseLeave={e => { if (!isAct) (e.currentTarget as HTMLElement).style.background = 'transparent' }}>
                      <span className={`status-dot ${isAct && store.statusOk ? 'ok' : ''}`} />
                      {ep.name}
                      <span className="ml-auto opacity-50 text-[10px]">{ep.model}</span>
                    </button>
                  )
                })}
              </div>
            )}
          </div>

          {/* 2. Mod dropdown (RAG + Regenerate) */}
          <div className="relative z-50">
            <button
              onClick={() => { setShowModDrop(o => !o); setShowEndDrop(false); setShowFmtDrop(false) }}
              className={`pill ${agentMode ? 'active' : ''}`}
            >
              <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 6V4m0 2a2 2 0 100 4m0-4a2 2 0 110 4m-6 8a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4m6 6v10m6-2a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4" />
              </svg>
              Mod
              <svg className="w-2.5 h-2.5 ml-0.5 opacity-50" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
              </svg>
            </button>
            {showModDrop && (
              <div className="absolute left-0 bottom-full mb-2 rounded-xl shadow-2xl overflow-hidden z-50"
                   style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', minWidth: 160 }}>
                {/* RAG toggle */}
                <button
                  onClick={toggleAgent}
                  className="w-full flex items-center justify-between px-3 py-2.5 text-xs cursor-pointer transition"
                  style={{ color: agentMode ? 'var(--accent-hi)' : 'var(--text-2)' }}
                  onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-2)'}
                  onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}
                >
                  <span className="flex items-center gap-2">
                    <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
                    </svg>
                    RAG
                  </span>
                  <span className="w-3.5 h-3.5 rounded-full border-2 flex items-center justify-center transition-colors"
                        style={{ borderColor: agentMode ? 'var(--accent)' : 'var(--mute)',
                                 background:  agentMode ? 'var(--accent)' : 'transparent' }}>
                    {agentMode && <span className="w-1.5 h-1.5 rounded-full bg-[#0b1929]" />}
                  </span>
                </button>
              </div>
            )}
          </div>

          {/* 3. Format dropdown */}
          <div className="relative z-50">
            <button
              onClick={() => { setShowFmtDrop(o => !o); setShowEndDrop(false); setShowModDrop(false) }}
              className="pill"
              style={ outputFormat !== 'free'
                ? { background: 'rgba(138,180,248,0.15)', color: 'var(--accent-hi)', border: '1px solid rgba(138,180,248,0.35)' }
                : {} }
            >
              {formatLabel}
              <svg className="w-2.5 h-2.5 ml-0.5 opacity-50" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
              </svg>
            </button>
            {showFmtDrop && (
              <div className="absolute left-0 bottom-full mb-2 rounded-xl shadow-2xl overflow-hidden z-50"
                   style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', minWidth: 130 }}>
                {FORMAT_PILLS.map((fp, i) => {
                  const isAct = outputFormat === fp.id
                  return (
                    <button key={fp.id}
                            onClick={() => { setOutputFormat(fp.id); setShowFmtDrop(false) }}
                            title={fp.title}
                            className="w-full text-left px-3 py-2.5 text-xs cursor-pointer transition"
                            style={{ background: isAct ? 'rgba(138,180,248,0.15)' : 'transparent',
                                     color: isAct ? 'var(--accent-hi)' : 'var(--text-2)',
                                     borderBottom: i < FORMAT_PILLS.length - 1 ? '1px solid var(--border)' : 'none' }}
                            onMouseEnter={e => { if (!isAct) (e.currentTarget as HTMLElement).style.background = 'var(--surface-2)' }}
                            onMouseLeave={e => { if (!isAct) (e.currentTarget as HTMLElement).style.background = 'transparent' }}>
                      {fp.label}
                    </button>
                  )
                })}
              </div>
            )}
          </div>

          {/* ✨ improve — only when meaningful input */}
          {input.trim().length > 8 && (
            <button onClick={onImprove} disabled={improving} title="Promptu AI ile iyileştir"
                    className="flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] cursor-pointer transition disabled:opacity-50"
                    style={{ background: improving ? 'rgba(138,180,248,0.15)' : 'transparent',
                             border: '1px solid rgba(138,180,248,0.3)', color: 'var(--accent-hi)' }}
                    onMouseEnter={e => { if (!improving) (e.currentTarget as HTMLElement).style.background = 'rgba(138,180,248,0.12)' }}
                    onMouseLeave={e => { if (!improving) (e.currentTarget as HTMLElement).style.background = 'transparent' }}>
              {improving
                ? <svg className="w-3 h-3 animate-spin" fill="none" viewBox="0 0 24 24"><circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"/><path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z"/></svg>
                : <span>✨</span>}
              {improving ? 'İyileştiriliyor…' : 'İyileştir'}
            </button>
          )}

          {/* Token counter */}
          {inputTokens > 0 && (
            <span className="text-[10px] tabular-nums"
                  style={{ color: tokenColor, marginLeft: input.trim().length <= 8 ? 'auto' : '4px' }}
                  title={`Tahmini ${inputTokens} token (6000 pencere sınırı)`}>
              ~{inputTokens.toLocaleString()} tok
            </span>
          )}

          {/* Connection status */}
          <div className="ml-auto flex items-center gap-1 text-[11px]" style={{ color: 'var(--mute-2)' }}>
            <span className={`status-dot ${store.statusOk ? 'ok' : store.statusOk === false ? 'bad' : ''}`} />
            <span>{store.status}</span>
          </div>
        </div>
      </div>
    </div>
  )
}
