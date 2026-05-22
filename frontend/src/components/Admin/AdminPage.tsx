import { useEffect, useState, useRef, useCallback } from 'react'
import { useStore, t, DEFAULT_ENDPOINTS } from '../../store'
import type { Endpoint } from '../../store'
import {
  uploadFiles, listDocuments, listCollections,
  deleteDocument, listSkills, getSkill, uploadSkills, deleteSkill,
  getUsageUsers, getUsageModels, getUsageLogs,
  listTemplates, createTemplate, updateTemplate, deleteTemplate,
  getRatingStats,
  listSkillExamples, createSkillExample, updateSkillExample, deleteSkillExample,
} from '../../api/admin'
import type {
  UploadResult, DocumentsPage, CollectionRow, SkillRow,
  UserSpend, ModelSpend, SpendLog, PromptTemplate, RatingStats, SkillExample,
} from '../../api/admin'
import SetLogo from '../SetLogo'

type Tab = 'upload' | 'documents' | 'skills' | 'templates' | 'usage' | 'settings'

async function pingProxy(): Promise<boolean> {
  try {
    const r = await fetch('/health', { signal: AbortSignal.timeout(3000) })
    return r.ok
  } catch { return false }
}

// =============================================================================
// AdminPage — RAG admin panel (3 tabs: upload / documents / skills)
// =============================================================================

export default function AdminPage() {
  const { auth } = useStore()
  const [tab, setTab] = useState<Tab>('upload')

  useEffect(() => {
    const stored = localStorage.getItem('setllm-theme')
    document.documentElement.setAttribute('data-theme', stored === 'light' ? 'light' : 'dark')
  }, [])

  // Guard: non-admin users who navigate directly to /admin see a 403 page
  if (!auth.isAdmin) {
    return (
      <div className="min-h-dvh flex flex-col items-center justify-center gap-5"
           style={{ background: 'var(--bg)', color: 'var(--text)' }}>
        <svg className="w-16 h-16 opacity-25" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
          <path strokeLinecap="round" strokeLinejoin="round"
            d="M16.5 10.5V6.75a4.5 4.5 0 10-9 0v3.75m-.75 11.25h10.5a2.25 2.25 0 002.25-2.25v-6.75a2.25 2.25 0 00-2.25-2.25H6.75a2.25 2.25 0 00-2.25 2.25v6.75a2.25 2.25 0 002.25 2.25z"/>
        </svg>
        <div>
          <div className="text-2xl font-semibold text-center">Erişim Yetkiniz Yok</div>
          <p className="text-sm mt-2 text-center max-w-xs" style={{ color: 'var(--mute)' }}>
            Bu sayfayı görüntülemek için yönetici yetkisi gereklidir.
          </p>
        </div>
        <div className="flex flex-col items-center gap-2 mt-1">
          <p className="text-xs" style={{ color: 'var(--mute-2)' }}>
            Yetkiniz varsa çıkış yapıp tekrar giriş yapın.
          </p>
          <div className="flex gap-2">
            <a href="/" className="px-4 py-2 rounded-full text-sm cursor-pointer"
               style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
              ← Ana Sayfa
            </a>
            <a href="/login"
               onClick={() => { localStorage.removeItem('setllm-token'); localStorage.removeItem('setllm-user') }}
               className="px-4 py-2 rounded-full text-sm cursor-pointer"
               style={{ background: 'var(--accent)', color: '#0b1929' }}>
              Yeniden Giriş Yap
            </a>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="min-h-dvh flex flex-col" style={{ background: 'var(--bg)', color: 'var(--text)' }}>
      {/* Header */}
      <header
        className="h-14 flex items-center gap-3 px-4 shrink-0"
        style={{ background: 'var(--bg)', borderBottom: '1px solid var(--border)' }}
      >
        <a href="/" className="p-2 rounded-full cursor-pointer transition"
           style={{ color: 'var(--text-2)' }}
           title="Back to chat"
           onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
           onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}>
          <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M10 19l-7-7m0 0l7-7m-7 7h18" />
          </svg>
        </a>
        <SetLogo className="h-6 w-auto" />
        <span className="text-[15px] font-medium tracking-tight">SET LLM Admin</span>

        <div className="flex-1" />

        <nav className="flex items-center gap-1">
          {(['upload', 'documents', 'skills', 'templates', 'usage', 'settings'] as Tab[]).map(tb => (
            <button
              key={tb}
              onClick={() => setTab(tb)}
              className="px-3 py-1.5 text-sm rounded-full transition cursor-pointer"
              style={{
                background: tab === tb ? 'var(--surface-hi)' : 'transparent',
                color:      tab === tb ? 'var(--accent-hi)' : 'var(--text-2)',
                border:     tab === tb ? '1px solid var(--border)' : '1px solid transparent',
              }}
            >
              {tb === 'upload' ? 'Upload' : tb === 'documents' ? 'Documents' : tb === 'skills' ? 'Skills' : tb === 'templates' ? 'Şablonlar' : tb === 'usage' ? 'Kullanım' : '⚙ Ayarlar'}
            </button>
          ))}
        </nav>
      </header>

      {/* Body */}
      <main className="flex-1 overflow-auto p-6">
        <div className="max-w-6xl mx-auto">
          {tab === 'upload'    && <UploadTab />}
          {tab === 'documents' && <DocumentsTab />}
          {tab === 'skills'    && <SkillsTab />}
          {tab === 'templates' && <TemplatesTab />}
          {tab === 'usage'     && <UsageTab />}
          {tab === 'settings'  && <SettingsTab />}
        </div>
      </main>
    </div>
  )
}

// =============================================================================
// Tab 1 — Upload
// =============================================================================

function UploadTab() {
  const [collection, setCollection] = useState('default')
  const [files, setFiles]           = useState<File[]>([])
  const [results, setResults]       = useState<UploadResult[]>([])
  const [busy, setBusy]             = useState(false)
  const [dragOver, setDragOver]     = useState(false)
  const inputRef = useRef<HTMLInputElement>(null)

  const onPick = (list: FileList | null) => {
    if (!list) return
    setFiles(Array.from(list))
    setResults([])
  }

  const onDrop = (e: React.DragEvent) => {
    e.preventDefault()
    setDragOver(false)
    onPick(e.dataTransfer.files)
  }

  const onUpload = async () => {
    if (!files.length) return
    setBusy(true)
    setResults([])
    try {
      const r = await uploadFiles(files, collection.trim() || 'default')
      setResults(r)
      setFiles([])
      if (inputRef.current) inputRef.current.value = ''
    } catch (err: any) {
      setResults([{ file: '(batch)', ok: false, error: err.message ?? String(err) }])
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="space-y-5">
      <div>
        <h2 className="text-lg font-medium">Upload documents</h2>
        <p className="text-xs mt-1" style={{ color: 'var(--mute)' }}>
          Supported file types: .txt, .md, .pdf, .docx
        </p>
      </div>

      <div className="rounded-xl p-5 space-y-4"
           style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>

        <label className="block">
          <div className="text-xs mb-1.5" style={{ color: 'var(--mute)' }}>Collection</div>
          <input
            value={collection}
            onChange={e => setCollection(e.target.value)}
            className="w-full px-3 py-2 rounded-md text-sm outline-none"
            style={{
              background: 'var(--input-bg)',
              border:     '1px solid var(--border)',
              color:      'var(--text)',
            }}
            placeholder="default"
          />
        </label>

        <div
          onDragOver={e => { e.preventDefault(); setDragOver(true) }}
          onDragLeave={() => setDragOver(false)}
          onDrop={onDrop}
          onClick={() => inputRef.current?.click()}
          className="rounded-lg p-8 text-center cursor-pointer transition"
          style={{
            background: dragOver ? 'var(--surface-hi)' : 'var(--surface-2)',
            border:     `2px dashed ${dragOver ? 'var(--accent)' : 'var(--border)'}`,
          }}
        >
          <svg className="w-8 h-8 mx-auto mb-2" fill="none" viewBox="0 0 24 24"
               stroke="currentColor" strokeWidth={1.5} style={{ color: 'var(--accent)' }}>
            <path strokeLinecap="round" strokeLinejoin="round"
                  d="M7 16a4 4 0 01-.88-7.9 5 5 0 019.9-1A4.5 4.5 0 0117 16M12 12v9m0-9l-3 3m3-3l3 3" />
          </svg>
          <div className="text-sm" style={{ color: 'var(--text)' }}>
            Drag & drop files here, or click to browse
          </div>
          <div className="text-xs mt-1" style={{ color: 'var(--mute)' }}>
            .txt, .md, .pdf, .docx
          </div>
          <input
            ref={inputRef}
            type="file"
            multiple
            accept=".txt,.md,.pdf,.docx"
            onChange={e => onPick(e.target.files)}
            className="hidden"
          />
        </div>

        {files.length > 0 && (
          <div className="rounded-md p-3 text-xs"
               style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
            <div className="font-medium mb-2" style={{ color: 'var(--text)' }}>
              {files.length} file{files.length === 1 ? '' : 's'} selected
            </div>
            <ul className="space-y-1" style={{ color: 'var(--mute)' }}>
              {files.map(f => (
                <li key={f.name} className="flex justify-between">
                  <span className="truncate">{f.name}</span>
                  <span className="ml-2 shrink-0">{formatBytes(f.size)}</span>
                </li>
              ))}
            </ul>
          </div>
        )}

        <div className="flex justify-end">
          <button
            disabled={busy || !files.length}
            onClick={onUpload}
            className="px-4 py-2 rounded-md text-sm font-medium cursor-pointer transition disabled:opacity-50 disabled:cursor-not-allowed"
            style={{
              background: 'var(--accent)',
              color:      '#0a0a0a',
            }}
          >
            {busy ? 'Uploading…' : 'Upload & ingest'}
          </button>
        </div>
      </div>

      {results.length > 0 && (
        <div className="rounded-xl overflow-hidden"
             style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
          <table className="w-full text-sm">
            <thead style={{ background: 'var(--surface-hi)', color: 'var(--mute)' }}>
              <tr>
                <th className="text-left px-4 py-2 font-medium">File</th>
                <th className="text-left px-4 py-2 font-medium">Status</th>
                <th className="text-right px-4 py-2 font-medium">Chunks</th>
                <th className="text-left px-4 py-2 font-medium">Notes</th>
              </tr>
            </thead>
            <tbody>
              {results.map((r, i) => (
                <tr key={i} style={{ borderTop: '1px solid var(--border)' }}>
                  <td className="px-4 py-2 truncate">{r.file}</td>
                  <td className="px-4 py-2">
                    <span style={{ color: r.ok ? '#34a853' : '#ea4335' }}>
                      {r.ok ? 'OK' : 'Failed'}
                    </span>
                  </td>
                  <td className="px-4 py-2 text-right" style={{ color: 'var(--mute)' }}>
                    {r.chunks ?? '—'}
                  </td>
                  <td className="px-4 py-2 text-xs" style={{ color: 'var(--mute)' }}>
                    {r.error ?? (r.tokens ? `${r.tokens} tokens` : '')}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}

// =============================================================================
// Tab 2 — Documents
// =============================================================================

function DocumentsTab() {
  const [collections, setCollections] = useState<CollectionRow[]>([])
  const [filter, setFilter]           = useState<string>('')
  const [page, setPage]               = useState(1)
  const [data, setData]               = useState<DocumentsPage | null>(null)
  const [loading, setLoading]         = useState(false)
  const [error, setError]             = useState<string | null>(null)
  const [confirm, setConfirm]         = useState<{ collection: string; source: string } | null>(null)

  const pageSize = 20

  const refresh = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [cols, page1] = await Promise.all([
        listCollections(),
        listDocuments(filter || null, page, pageSize),
      ])
      setCollections(cols)
      setData(page1)
    } catch (e: any) {
      setError(e.message ?? String(e))
    } finally {
      setLoading(false)
    }
  }, [filter, page])

  useEffect(() => { refresh() }, [refresh])

  const onDelete = async (collection: string, source: string) => {
    try {
      await deleteDocument(collection, source)
      setConfirm(null)
      refresh()
    } catch (e: any) {
      setError(e.message ?? String(e))
    }
  }

  const totalPages = data ? Math.max(1, Math.ceil(data.total / pageSize)) : 1

  return (
    <section className="space-y-5">
      <div className="flex items-end gap-4 flex-wrap">
        <div>
          <h2 className="text-lg font-medium">Documents</h2>
          <p className="text-xs mt-1" style={{ color: 'var(--mute)' }}>
            Ingested files grouped by source. Deleting removes all chunks for that source.
          </p>
        </div>

        <div className="flex-1" />

        <label className="block">
          <div className="text-xs mb-1.5" style={{ color: 'var(--mute)' }}>Collection</div>
          <select
            value={filter}
            onChange={e => { setFilter(e.target.value); setPage(1) }}
            className="px-3 py-2 rounded-md text-sm outline-none min-w-[12rem]"
            style={{
              background: 'var(--input-bg)',
              border:     '1px solid var(--border)',
              color:      'var(--text)',
            }}
          >
            <option value="">All collections</option>
            {collections.map(c => (
              <option key={c.collection} value={c.collection}>
                {c.collection} ({c.sources} sources, {c.chunks} chunks)
              </option>
            ))}
          </select>
        </label>

        <button
          onClick={refresh}
          className="px-3 py-2 rounded-md text-sm cursor-pointer transition"
          style={{
            background: 'var(--surface-hi)',
            border:     '1px solid var(--border)',
            color:      'var(--text)',
          }}
        >
          Refresh
        </button>
      </div>

      {error && (
        <div className="rounded-md px-3 py-2 text-xs"
             style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}>
          {error}
        </div>
      )}

      <div className="rounded-xl overflow-hidden"
           style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
        <table className="w-full text-sm">
          <thead style={{ background: 'var(--surface-hi)', color: 'var(--mute)' }}>
            <tr>
              <th className="text-left px-4 py-2 font-medium">Collection</th>
              <th className="text-left px-4 py-2 font-medium">Source</th>
              <th className="text-left px-4 py-2 font-medium">Title</th>
              <th className="text-right px-4 py-2 font-medium">Chunks</th>
              <th className="text-left px-4 py-2 font-medium">Updated</th>
              <th className="text-right px-4 py-2 font-medium"></th>
            </tr>
          </thead>
          <tbody>
            {loading && (
              <tr><td colSpan={6} className="px-4 py-6 text-center text-xs"
                      style={{ color: 'var(--mute)' }}>Loading…</td></tr>
            )}
            {!loading && data?.items.length === 0 && (
              <tr><td colSpan={6} className="px-4 py-6 text-center text-xs"
                      style={{ color: 'var(--mute)' }}>No documents.</td></tr>
            )}
            {!loading && data?.items.map(d => {
              const isConfirm = confirm?.collection === d.collection && confirm?.source === d.source
              return (
                <tr key={`${d.collection}/${d.source}`} style={{ borderTop: '1px solid var(--border)' }}>
                  <td className="px-4 py-2" style={{ color: 'var(--mute)' }}>{d.collection}</td>
                  <td className="px-4 py-2 truncate max-w-[16rem]" title={d.source}>{d.source}</td>
                  <td className="px-4 py-2 truncate max-w-[14rem]" style={{ color: 'var(--mute)' }} title={d.title}>{d.title}</td>
                  <td className="px-4 py-2 text-right">{d.chunks}</td>
                  <td className="px-4 py-2 text-xs" style={{ color: 'var(--mute)' }}>
                    {formatDate(d.updatedAt)}
                  </td>
                  <td className="px-4 py-2 text-right">
                    {isConfirm ? (
                      <div className="inline-flex gap-1">
                        <button
                          onClick={() => onDelete(d.collection, d.source)}
                          className="px-2 py-1 rounded text-xs cursor-pointer"
                          style={{ background: '#ea4335', color: '#fff' }}
                        >
                          Confirm
                        </button>
                        <button
                          onClick={() => setConfirm(null)}
                          className="px-2 py-1 rounded text-xs cursor-pointer"
                          style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}
                        >
                          Cancel
                        </button>
                      </div>
                    ) : (
                      <button
                        onClick={() => setConfirm({ collection: d.collection, source: d.source })}
                        className="px-2 py-1 rounded text-xs cursor-pointer transition"
                        style={{ color: '#ea4335' }}
                        onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'rgba(234,67,53,0.1)'}
                        onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}
                      >
                        Delete
                      </button>
                    )}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>

      {data && data.total > pageSize && (
        <div className="flex items-center justify-between text-xs" style={{ color: 'var(--mute)' }}>
          <div>
            Showing {(page - 1) * pageSize + 1}–{Math.min(page * pageSize, data.total)} of {data.total}
          </div>
          <div className="flex gap-1">
            <button
              disabled={page <= 1}
              onClick={() => setPage(p => Math.max(1, p - 1))}
              className="px-3 py-1.5 rounded-md cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed"
              style={{
                background: 'var(--surface-hi)',
                border:     '1px solid var(--border)',
                color:      'var(--text-2)',
              }}
            >
              Prev
            </button>
            <span className="px-3 py-1.5">Page {page} / {totalPages}</span>
            <button
              disabled={page >= totalPages}
              onClick={() => setPage(p => p + 1)}
              className="px-3 py-1.5 rounded-md cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed"
              style={{
                background: 'var(--surface-hi)',
                border:     '1px solid var(--border)',
                color:      'var(--text-2)',
              }}
            >
              Next
            </button>
          </div>
        </div>
      )}
    </section>
  )
}

// =============================================================================
// Tab 3 — Skills
// =============================================================================

function SkillsTab() {
  const [skills, setSkills]     = useState<SkillRow[]>([])
  const [selected, setSelected] = useState<string | null>(null)
  const [content, setContent]   = useState<string>('')
  const [loading, setLoading]   = useState(false)
  const [error, setError]       = useState<string | null>(null)
  const [uploadMsg, setUploadMsg] = useState<string | null>(null)
  // Few-shot examples state
  const [examples,    setExamples]    = useState<SkillExample[]>([])
  const [exLoading,   setExLoading]   = useState(false)
  const [showExForm,  setShowExForm]  = useState(false)
  const [editEx,      setEditEx]      = useState<SkillExample | null>(null)
  const [exUser,      setExUser]      = useState('')
  const [exAssistant, setExAssistant] = useState('')
  const [exSaving,    setExSaving]    = useState(false)
  const fileRef = useRef<HTMLInputElement>(null)

  const reload = () => listSkills().then(setSkills).catch(e => setError(e.message ?? String(e)))

  useEffect(() => { reload() }, [])

  const loadExamples = async (id: string) => {
    setExLoading(true)
    try { setExamples(await listSkillExamples(id)) }
    catch { setExamples([]) }
    finally { setExLoading(false) }
  }

  const openSkill = async (id: string) => {
    setSelected(id)
    setShowExForm(false); setEditEx(null); setExUser(''); setExAssistant('')
    loadExamples(id)
    setLoading(true)
    setError(null)
    try {
      setContent(await getSkill(id))
    } catch (e: any) {
      setError(e.message ?? String(e))
      setContent('')
    } finally {
      setLoading(false)
    }
  }

  const onUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = e.target.files
    if (!files || files.length === 0) return
    setError(null)
    try {
      const results = await uploadSkills(files)
      const ok  = results.filter(r => r.ok).length
      const bad = results.filter(r => !r.ok)
      setUploadMsg(`${ok} skill(s) uploaded.${bad.length > 0 ? ' Errors: ' + bad.map(r => r.error).join(', ') : ''}`)
      reload()
    } catch (e: any) {
      setError(e.message ?? String(e))
    } finally {
      e.target.value = ''
    }
  }

  const onDelete = async (id: string) => {
    if (!confirm(`"${id}" skill'ini sil?`)) return
    setError(null)
    try {
      await deleteSkill(id)
      if (selected === id) { setSelected(null); setContent('') }
      reload()
    } catch (e: any) {
      setError(e.message ?? String(e))
    }
  }

  return (
    <section className="space-y-5">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h2 className="text-lg font-medium">Skills</h2>
          <p className="text-xs mt-1" style={{ color: 'var(--mute)' }}>
            System prompts loaded from the Skills directory. Click to view.
          </p>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          <input ref={fileRef} type="file" accept=".md" multiple className="hidden" onChange={onUpload} />
          <button
            onClick={() => fileRef.current?.click()}
            className="px-3 py-1.5 rounded-lg text-xs font-medium cursor-pointer transition"
            style={{ background: 'var(--accent)', color: '#0b1929' }}
          >
            + Skill Yükle (.md)
          </button>
        </div>
      </div>

      {uploadMsg && (
        <div className="rounded-md px-3 py-2 text-xs"
             style={{ background: 'rgba(52,168,83,0.1)', color: '#34a853', border: '1px solid rgba(52,168,83,0.3)' }}>
          {uploadMsg}
        </div>
      )}

      {error && (
        <div className="rounded-md px-3 py-2 text-xs"
             style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}>
          {error}
        </div>
      )}

      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        {/* List */}
        <div className="rounded-xl overflow-hidden md:col-span-1"
             style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
          <div className="px-3 py-2 text-[11px] uppercase tracking-wider font-semibold"
               style={{ color: 'var(--mute)', borderBottom: '1px solid var(--border)' }}>
            {skills.length} skill{skills.length === 1 ? '' : 's'}
          </div>
          <ul className="max-h-[60vh] overflow-y-auto">
            {skills.map(s => {
              const active = selected === s.id
              return (
                <li key={s.id}>
                  <div
                    className="flex items-center group"
                    style={{ borderBottom: '1px solid var(--border)', background: active ? 'rgba(138,180,248,0.15)' : 'transparent' }}
                  >
                    <button
                      onClick={() => openSkill(s.id)}
                      className="flex-1 text-left px-3 py-2.5 text-sm cursor-pointer transition flex items-center justify-between"
                      style={{ color: active ? 'var(--accent-hi)' : 'var(--text)' }}
                    >
                      <span className="truncate">{s.id}</span>
                      <span className="text-[10px] shrink-0 ml-2" style={{ color: 'var(--mute)' }}>
                        {formatBytes(s.size)}
                      </span>
                    </button>
                    <button
                      onClick={() => onDelete(s.id)}
                      className="opacity-0 group-hover:opacity-100 px-2 py-2.5 cursor-pointer transition shrink-0"
                      style={{ color: '#ea4335' }}
                      title="Sil"
                    >
                      <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6M1 7h22M9 7V4a1 1 0 011-1h4a1 1 0 011 1v3"/>
                      </svg>
                    </button>
                  </div>
                </li>
              )
            })}
            {skills.length === 0 && (
              <li className="px-3 py-6 text-center text-xs" style={{ color: 'var(--mute)' }}>
                No skills found.
              </li>
            )}
          </ul>
        </div>

        {/* Viewer */}
        <div className="rounded-xl md:col-span-2 flex flex-col"
             style={{ background: 'var(--surface)', border: '1px solid var(--border)', minHeight: '60vh' }}>
          <div className="px-3 py-2 text-xs flex items-center justify-between"
               style={{ color: 'var(--mute)', borderBottom: '1px solid var(--border)' }}>
            <span className="truncate">{selected ?? 'Select a skill to view'}</span>
            {loading && <span>Loading…</span>}
          </div>
          <pre className="flex-1 overflow-auto p-4 text-xs whitespace-pre-wrap font-mono"
               style={{ color: 'var(--text-2)', maxHeight: '40vh' }}>
            {content || (selected ? '' : 'No skill selected.')}
          </pre>

          {/* ── Few-Shot Examples Panel ──────────────────────────────── */}
          {selected && (
            <div style={{ borderTop: '1px solid var(--border)' }}>
              <div className="px-3 py-2 flex items-center justify-between"
                   style={{ borderBottom: examples.length > 0 || showExForm ? '1px solid var(--border)' : 'none' }}>
                <span className="text-xs font-semibold" style={{ color: 'var(--mute)' }}>
                  FEW-SHOT ÖRNEKLER {examples.length > 0 && `(${examples.length})`}
                </span>
                {!showExForm && !editEx && (
                  <button onClick={() => { setShowExForm(true); setExUser(''); setExAssistant('') }}
                          className="text-[10px] px-2 py-0.5 rounded cursor-pointer"
                          style={{ background: 'var(--accent)', color: '#0b1929' }}>
                    + Ekle
                  </button>
                )}
              </div>

              {/* Example list */}
              {exLoading ? (
                <div className="px-3 py-3 text-xs" style={{ color: 'var(--mute)' }}>Yükleniyor…</div>
              ) : (
                <div className="max-h-64 overflow-y-auto scrollbar-thin">
                  {examples.map((ex, i) => (
                    editEx?.id === ex.id ? (
                      // Inline edit form
                      <ExampleForm key={ex.id}
                        userVal={exUser} assistantVal={exAssistant}
                        saving={exSaving}
                        onUserChange={setExUser} onAssistantChange={setExAssistant}
                        onSave={async () => {
                          if (!exUser.trim() || !exAssistant.trim()) return
                          setExSaving(true)
                          try {
                            await updateSkillExample(selected, ex.id, exUser, exAssistant)
                            setEditEx(null); loadExamples(selected)
                          } catch (e: any) { setError(e.message) }
                          finally { setExSaving(false) }
                        }}
                        onCancel={() => setEditEx(null)}
                      />
                    ) : (
                      <div key={ex.id} className="px-3 py-2 group flex gap-2 items-start text-xs"
                           style={{ borderBottom: i < examples.length-1 ? '1px solid var(--border)' : 'none' }}>
                        <span className="shrink-0 w-4 text-center font-bold" style={{ color: 'var(--mute)' }}>{i+1}</span>
                        <div className="flex-1 min-w-0 space-y-1">
                          <div className="flex items-start gap-1">
                            <span className="shrink-0 text-[9px] uppercase font-semibold px-1 rounded mt-0.5"
                                  style={{ background: 'rgba(138,180,248,0.15)', color: 'var(--accent-hi)' }}>U</span>
                            <span className="truncate" style={{ color: 'var(--text-2)' }}>{ex.userMessage}</span>
                          </div>
                          <div className="flex items-start gap-1">
                            <span className="shrink-0 text-[9px] uppercase font-semibold px-1 rounded mt-0.5"
                                  style={{ background: 'rgba(52,168,83,0.15)', color: '#34a853' }}>A</span>
                            <span className="truncate" style={{ color: 'var(--text-2)' }}>{ex.assistantMessage}</span>
                          </div>
                        </div>
                        <div className="flex gap-1 opacity-0 group-hover:opacity-100 shrink-0">
                          <button onClick={() => { setEditEx(ex); setExUser(ex.userMessage); setExAssistant(ex.assistantMessage); setShowExForm(false) }}
                                  className="px-1.5 py-0.5 rounded text-[10px] cursor-pointer"
                                  style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>✏️</button>
                          <button onClick={async () => {
                                    if (!confirm('Bu örneği sil?')) return
                                    await deleteSkillExample(selected, ex.id).catch(e => setError(e.message))
                                    loadExamples(selected)
                                  }}
                                  className="px-1.5 py-0.5 rounded text-[10px] cursor-pointer"
                                  style={{ color: '#ea4335' }}>✕</button>
                        </div>
                      </div>
                    )
                  ))}
                  {examples.length === 0 && !showExForm && (
                    <div className="px-3 py-4 text-xs text-center" style={{ color: 'var(--mute)' }}>
                      Henüz örnek yok. + Ekle ile başlayın.
                    </div>
                  )}
                </div>
              )}

              {/* New example form */}
              {showExForm && (
                <ExampleForm
                  userVal={exUser} assistantVal={exAssistant} saving={exSaving}
                  onUserChange={setExUser} onAssistantChange={setExAssistant}
                  onSave={async () => {
                    if (!exUser.trim() || !exAssistant.trim()) return
                    setExSaving(true)
                    try {
                      await createSkillExample(selected, exUser, exAssistant)
                      setShowExForm(false); setExUser(''); setExAssistant('')
                      loadExamples(selected)
                    } catch (e: any) { setError(e.message) }
                    finally { setExSaving(false) }
                  }}
                  onCancel={() => { setShowExForm(false); setExUser(''); setExAssistant('') }}
                />
              )}
            </div>
          )}
        </div>
      </div>
    </section>
  )
}

function ExampleForm({ userVal, assistantVal, saving, onUserChange, onAssistantChange, onSave, onCancel }: {
  userVal: string; assistantVal: string; saving: boolean
  onUserChange: (v: string) => void; onAssistantChange: (v: string) => void
  onSave: () => void; onCancel: () => void
}) {
  return (
    <div className="px-3 py-3 space-y-2" style={{ borderTop: '1px solid var(--border)', background: 'var(--surface-2)' }}>
      <div>
        <div className="text-[10px] font-semibold mb-1 uppercase" style={{ color: 'var(--accent-hi)' }}>Kullanıcı mesajı</div>
        <textarea value={userVal} onChange={e => onUserChange(e.target.value)} rows={2}
                  placeholder="Örnek kullanıcı sorusu…"
                  className="w-full rounded px-2 py-1.5 text-xs outline-none resize-none"
                  style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
      </div>
      <div>
        <div className="text-[10px] font-semibold mb-1 uppercase" style={{ color: '#34a853' }}>Asistan yanıtı</div>
        <textarea value={assistantVal} onChange={e => onAssistantChange(e.target.value)} rows={3}
                  placeholder="Beklenen örnek yanıt…"
                  className="w-full rounded px-2 py-1.5 text-xs outline-none resize-none"
                  style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
      </div>
      <div className="flex gap-2">
        <button onClick={onSave} disabled={saving || !userVal.trim() || !assistantVal.trim()}
                className="flex-1 py-1 rounded text-xs font-semibold cursor-pointer disabled:opacity-50"
                style={{ background: 'var(--accent)', color: '#0b1929' }}>
          {saving ? 'Kaydediliyor…' : '✓ Kaydet'}
        </button>
        <button onClick={onCancel} className="px-3 py-1 rounded text-xs cursor-pointer"
                style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
          İptal
        </button>
      </div>
    </div>
  )
}

// =============================================================================
// Tab 4 — Usage
// =============================================================================

function UsageTab() {
  const [users,  setUsers]  = useState<UserSpend[]>([])
  const [models, setModels] = useState<ModelSpend[]>([])
  const [logs,    setLogs]    = useState<SpendLog[]>([])
  const [ratings, setRatings] = useState<RatingStats | null>(null)
  const [loading,  setLoading]  = useState(true)
  const [error,    setError]    = useState<string | null>(null)

  const load = async () => {
    setLoading(true); setError(null)
    try {
      const [u, m, l, r] = await Promise.all([
        getUsageUsers(), getUsageModels(), getUsageLogs(50), getRatingStats().catch(() => null)
      ])
      setUsers(u); setModels(m); setLogs(l); setRatings(r)
    } catch (e: any) {
      setError(e.message ?? String(e))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => { load() }, [])

  const totalTokens = (arr: UserSpend[]) =>
    arr.reduce((s, u) => s + (u.total_tokens ?? 0), 0)

  return (
    <section className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-lg font-medium">Token Kullanımı</h2>
          <p className="text-xs mt-1" style={{ color: 'var(--mute)' }}>LiteLLM spend log verisi</p>
        </div>
        <button onClick={load} className="px-3 py-1.5 rounded-lg text-xs cursor-pointer"
                style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text)' }}>
          ↺ Yenile
        </button>
      </div>

      {error && (
        <div className="rounded-md px-3 py-2 text-xs"
             style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}>
          {error}
        </div>
      )}

      {loading ? (
        <div className="text-center py-12 text-xs" style={{ color: 'var(--mute)' }}>Yükleniyor…</div>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-5">

          {/* Kullanıcı bazlı */}
          <div className="rounded-xl overflow-hidden md:col-span-2"
               style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
            <div className="px-4 py-3 flex items-center justify-between"
                 style={{ borderBottom: '1px solid var(--border)' }}>
              <span className="text-xs font-semibold uppercase tracking-wider" style={{ color: 'var(--mute)' }}>
                Kullanıcı Bazlı Token Kullanımı
              </span>
              <span className="text-xs font-medium" style={{ color: 'var(--accent-hi)' }}>
                Toplam {totalTokens(users).toLocaleString()} token
              </span>
            </div>
            <table className="w-full text-sm">
              <thead>
                <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--mute)', fontSize: 11 }}>
                  <th className="px-4 py-2 text-left font-medium">Kullanıcı</th>
                  <th className="px-4 py-2 text-right font-medium">Mesaj</th>
                  <th className="px-4 py-2 text-right font-medium">Prompt</th>
                  <th className="px-4 py-2 text-right font-medium">Completion</th>
                  <th className="px-4 py-2 text-right font-medium">Toplam</th>
                  <th className="px-4 py-2 text-left font-medium" style={{ minWidth: 120 }}>Pay</th>
                  <th className="px-4 py-2 text-left font-medium">Son Aktif</th>
                </tr>
              </thead>
              <tbody>
                {users.length === 0 && (
                  <tr><td colSpan={7} className="px-4 py-6 text-center text-xs" style={{ color: 'var(--mute)' }}>Veri yok</td></tr>
                )}
                {(() => {
                  const sorted  = [...users].sort((a,b) => (b.total_tokens??0)-(a.total_tokens??0))
                  const grandTotal = totalTokens(sorted) || 1
                  return sorted.map((u, i) => {
                    const pct = Math.round(((u.total_tokens ?? 0) / grandTotal) * 100)
                    return (
                      <tr key={i} style={{ borderBottom: '1px solid var(--border)' }}>
                        <td className="px-4 py-2.5 text-xs font-medium" style={{ color: 'var(--text)' }}>
                          {u.user_id || '(anonymous)'}
                        </td>
                        <td className="px-4 py-2.5 text-xs text-right" style={{ color: 'var(--mute)' }}>
                          {(u.messages ?? 0).toLocaleString()}
                        </td>
                        <td className="px-4 py-2.5 text-xs text-right" style={{ color: 'var(--mute)' }}>
                          {(u.prompt_tokens ?? 0).toLocaleString()}
                        </td>
                        <td className="px-4 py-2.5 text-xs text-right" style={{ color: 'var(--mute)' }}>
                          {(u.completion_tokens ?? 0).toLocaleString()}
                        </td>
                        <td className="px-4 py-2.5 text-xs text-right font-semibold" style={{ color: 'var(--accent-hi)' }}>
                          {(u.total_tokens ?? 0).toLocaleString()}
                        </td>
                        <td className="px-4 py-2.5">
                          <div className="flex items-center gap-2">
                            <div className="flex-1 h-1.5 rounded-full overflow-hidden"
                                 style={{ background: 'var(--surface-hi)', minWidth: 60 }}>
                              <div className="h-full rounded-full"
                                   style={{ width: `${pct}%`, background: 'var(--accent)' }} />
                            </div>
                            <span className="text-[10px] w-8 text-right shrink-0" style={{ color: 'var(--mute)' }}>
                              {pct}%
                            </span>
                          </div>
                        </td>
                        <td className="px-4 py-2.5 text-xs" style={{ color: 'var(--mute)' }}>
                          {u.last_active ? formatDate(u.last_active) : '—'}
                        </td>
                      </tr>
                    )
                  })
                })()}
              </tbody>
            </table>
          </div>

          {/* Model bazlı */}
          <div className="rounded-xl overflow-hidden"
               style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
            <div className="px-4 py-3 text-xs font-semibold uppercase tracking-wider"
                 style={{ color: 'var(--mute)', borderBottom: '1px solid var(--border)' }}>
              Model Bazlı
            </div>
            <table className="w-full text-sm">
              <thead>
                <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--mute)', fontSize: 11 }}>
                  <th className="px-4 py-2 text-left font-medium">Model</th>
                  <th className="px-4 py-2 text-right font-medium">Token</th>
                  <th className="px-4 py-2 text-right font-medium">İstek</th>
                </tr>
              </thead>
              <tbody>
                {models.length === 0 && (
                  <tr><td colSpan={3} className="px-4 py-6 text-center text-xs" style={{ color: 'var(--mute)' }}>Veri yok</td></tr>
                )}
                {[...models].sort((a,b) => (b.total_tokens??0)-(a.total_tokens??0)).map((m, i) => (
                  <tr key={i} style={{ borderBottom: '1px solid var(--border)' }}>
                    <td className="px-4 py-2.5 text-xs" style={{ color: 'var(--text)' }}>{m.model}</td>
                    <td className="px-4 py-2.5 text-xs text-right" style={{ color: 'var(--accent-hi)' }}>
                      {(m.total_tokens ?? 0).toLocaleString()}
                    </td>
                    <td className="px-4 py-2.5 text-xs text-right" style={{ color: 'var(--mute)' }}>
                      {(m.total_count ?? 0).toLocaleString()}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* Yanıt Puanlaması */}
          {ratings && (
            <div className="rounded-xl overflow-hidden md:col-span-2"
                 style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
              <div className="px-4 py-3 flex items-center gap-4"
                   style={{ borderBottom: '1px solid var(--border)' }}>
                <span className="text-xs font-semibold uppercase tracking-wider" style={{ color: 'var(--mute)' }}>
                  Yanıt Puanlaması
                </span>
                <span className="text-sm font-bold" style={{ color: '#34a853' }}>👍 {ratings.ups}</span>
                <span className="text-sm font-bold" style={{ color: '#ea4335' }}>👎 {ratings.downs}</span>
                <span className="text-xs ml-auto" style={{ color: 'var(--mute)' }}>
                  Toplam {ratings.total} puan
                  {ratings.total > 0 && ` — %${Math.round((ratings.ups / ratings.total) * 100)} olumlu`}
                </span>
              </div>
              {/* By model */}
              {ratings.byModel.length > 0 && (
                <div className="px-4 py-3 flex gap-6 flex-wrap"
                     style={{ borderBottom: '1px solid var(--border)' }}>
                  {ratings.byModel.map(bm => (
                    <div key={bm.model} className="text-xs">
                      <span className="font-medium" style={{ color: 'var(--text)' }}>{bm.model}</span>
                      <span className="ml-2" style={{ color: '#34a853' }}>👍{bm.ups}</span>
                      <span className="ml-1" style={{ color: '#ea4335' }}>👎{bm.downs}</span>
                    </div>
                  ))}
                </div>
              )}
              {/* Recent */}
              <div className="overflow-x-auto">
                <table className="w-full text-xs">
                  <thead>
                    <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--mute)' }}>
                      <th className="px-4 py-2 text-left font-medium">Kullanıcı</th>
                      <th className="px-4 py-2 text-left font-medium">Puan</th>
                      <th className="px-4 py-2 text-left font-medium">Model</th>
                      <th className="px-4 py-2 text-left font-medium">Zaman</th>
                    </tr>
                  </thead>
                  <tbody>
                    {ratings.recent.map((r, i) => (
                      <tr key={i} style={{ borderBottom: '1px solid var(--border)' }}>
                        <td className="px-4 py-2" style={{ color: 'var(--text)' }}>{r.username}</td>
                        <td className="px-4 py-2 text-lg">{r.rating === 1 ? '👍' : '👎'}</td>
                        <td className="px-4 py-2" style={{ color: 'var(--mute)' }}>{r.model}</td>
                        <td className="px-4 py-2" style={{ color: 'var(--mute)' }}>{formatDate(r.createdAt)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}

          {/* Son istekler */}
          <div className="rounded-xl overflow-hidden md:col-span-2"
               style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
            <div className="px-4 py-3 text-xs font-semibold uppercase tracking-wider"
                 style={{ color: 'var(--mute)', borderBottom: '1px solid var(--border)' }}>
              Son 50 İstek
            </div>
            <div className="overflow-x-auto">
              <table className="w-full text-xs">
                <thead>
                  <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--mute)' }}>
                    <th className="px-3 py-2 text-left font-medium">Zaman</th>
                    <th className="px-3 py-2 text-left font-medium">Kullanıcı</th>
                    <th className="px-3 py-2 text-left font-medium">Model</th>
                    <th className="px-3 py-2 text-right font-medium">Prompt</th>
                    <th className="px-3 py-2 text-right font-medium">Completion</th>
                    <th className="px-3 py-2 text-right font-medium">Toplam</th>
                  </tr>
                </thead>
                <tbody>
                  {logs.length === 0 && (
                    <tr><td colSpan={6} className="px-3 py-6 text-center" style={{ color: 'var(--mute)' }}>Veri yok</td></tr>
                  )}
                  {logs.map((l, i) => (
                    <tr key={i} style={{ borderBottom: '1px solid var(--border)' }}>
                      <td className="px-3 py-2" style={{ color: 'var(--mute)' }}>
                        {l.startTime ? new Date(l.startTime).toLocaleString() : '-'}
                      </td>
                      <td className="px-3 py-2" style={{ color: 'var(--text)' }}>{(l as any).end_user || l.user || '-'}</td>
                      <td className="px-3 py-2" style={{ color: 'var(--accent-hi)' }}>{l.model}</td>
                      <td className="px-3 py-2 text-right" style={{ color: 'var(--mute)' }}>{l.prompt_tokens ?? 0}</td>
                      <td className="px-3 py-2 text-right" style={{ color: 'var(--mute)' }}>{l.completion_tokens ?? 0}</td>
                      <td className="px-3 py-2 text-right font-medium" style={{ color: 'var(--text)' }}>{l.total_tokens ?? 0}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      )}
    </section>
  )
}

// =============================================================================
// Tab 5 — Prompt Templates
// =============================================================================

// Extract {{variable}} names from content
function extractVars(content: string): string[] {
  const matches = content.matchAll(/\{\{(\w+)\}\}/g)
  return [...new Set([...matches].map(m => m[1]))]
}

function TemplatesTab() {
  const [templates, setTemplates]   = useState<PromptTemplate[]>([])
  const [selected,  setSelected]    = useState<PromptTemplate | null>(null)
  const [isNew,     setIsNew]       = useState(false)
  const [name,      setName]        = useState('')
  const [collection,setCollection]  = useState('')
  const [content,   setContent]     = useState('')
  const [loading,   setLoading]     = useState(false)
  const [saving,    setSaving]      = useState(false)
  const [error,     setError]       = useState<string | null>(null)
  const [msg,       setMsg]         = useState<string | null>(null)

  const load = async () => {
    setLoading(true)
    try { setTemplates(await listTemplates()) }
    catch (e: any) { setError(e.message) }
    finally { setLoading(false) }
  }

  useEffect(() => { load() }, [])

  const openNew = () => {
    setSelected(null); setIsNew(true)
    setName(''); setCollection(''); setContent(''); setError(null); setMsg(null)
  }

  const openEdit = (t: PromptTemplate) => {
    setSelected(t); setIsNew(false)
    setName(t.name); setCollection(t.collection); setContent(t.content)
    setError(null); setMsg(null)
  }

  const onSave = async () => {
    if (!name.trim() || !content.trim()) { setError('Ad ve içerik zorunludur'); return }
    setSaving(true); setError(null)
    try {
      if (isNew) {
        await createTemplate(name.trim(), content, collection.trim())
        setMsg('Şablon oluşturuldu.')
      } else if (selected) {
        await updateTemplate(selected.id, name.trim(), content, collection.trim())
        setMsg('Şablon güncellendi.')
      }
      await load()
      setIsNew(false); setSelected(null)
    } catch (e: any) { setError(e.message) }
    finally { setSaving(false) }
  }

  const onDelete = async (t: PromptTemplate) => {
    if (!confirm(`"${t.name}" şablonunu sil?`)) return
    try {
      await deleteTemplate(t.id)
      if (selected?.id === t.id) { setSelected(null); setIsNew(false) }
      await load()
    } catch (e: any) { setError(e.message) }
  }

  const vars      = extractVars(content)
  const hasForm   = isNew || selected !== null
  const collections = [...new Set(templates.map(t => t.collection).filter(Boolean))]

  return (
    <section className="space-y-5">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h2 className="text-lg font-medium">Prompt Şablonları</h2>
          <p className="text-xs mt-1" style={{ color: 'var(--mute)' }}>
            {'{{değişken}} sözdizimi ile şablonlar. Chat\'te / yazarak çağrılır.'}
          </p>
        </div>
        <button onClick={openNew}
                className="px-3 py-1.5 rounded-lg text-xs font-medium cursor-pointer shrink-0"
                style={{ background: 'var(--accent)', color: '#0b1929' }}>
          + Yeni Şablon
        </button>
      </div>

      {error && <div className="rounded-md px-3 py-2 text-xs" style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}>{error}</div>}
      {msg   && <div className="rounded-md px-3 py-2 text-xs" style={{ background: 'rgba(52,168,83,0.1)',  color: '#34a853', border: '1px solid rgba(52,168,83,0.3)'  }}>{msg}</div>}

      <div className="grid grid-cols-1 md:grid-cols-5 gap-4">
        {/* List */}
        <div className="md:col-span-2 rounded-xl overflow-hidden"
             style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
          <div className="px-3 py-2 text-[11px] uppercase tracking-wider font-semibold flex items-center justify-between"
               style={{ color: 'var(--mute)', borderBottom: '1px solid var(--border)' }}>
            <span>{templates.length} şablon</span>
            {loading && <span>Yükleniyor…</span>}
          </div>
          {collections.length > 0 ? (
            // Grouped by collection
            [...collections, ''].filter(c => templates.some(t => t.collection === c)).map(col => (
              <div key={col || '__none__'}>
                {col && (
                  <div className="px-3 py-1 text-[10px] uppercase tracking-wider"
                       style={{ color: 'var(--mute)', background: 'var(--surface-2)', borderBottom: '1px solid var(--border)' }}>
                    {col}
                  </div>
                )}
                {templates.filter(t => t.collection === col).map(tmpl => (
                  <TemplateRow key={tmpl.id} tmpl={tmpl} active={selected?.id === tmpl.id}
                               onEdit={() => openEdit(tmpl)} onDelete={() => onDelete(tmpl)} />
                ))}
              </div>
            ))
          ) : (
            templates.map(tmpl => (
              <TemplateRow key={tmpl.id} tmpl={tmpl} active={selected?.id === tmpl.id}
                           onEdit={() => openEdit(tmpl)} onDelete={() => onDelete(tmpl)} />
            ))
          )}
          {!loading && templates.length === 0 && (
            <div className="px-3 py-8 text-center text-xs" style={{ color: 'var(--mute)' }}>
              Henüz şablon yok. + Yeni Şablon ile başlayın.
            </div>
          )}
        </div>

        {/* Form */}
        {hasForm ? (
          <div className="md:col-span-3 rounded-xl p-5 space-y-4"
               style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
            <div className="text-sm font-semibold" style={{ color: 'var(--text)' }}>
              {isNew ? '+ Yeni Şablon' : `Düzenle: ${selected?.name}`}
            </div>

            <div className="grid grid-cols-2 gap-3">
              <label className="block col-span-2 md:col-span-1">
                <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Ad *</div>
                <input value={name} onChange={e => setName(e.target.value)} placeholder="SQL Sorgu Şablonu"
                       className="w-full rounded-md px-3 py-2 text-sm outline-none"
                       style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
              </label>
              <label className="block col-span-2 md:col-span-1">
                <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Koleksiyon (isteğe bağlı)</div>
                <input value={collection} onChange={e => setCollection(e.target.value)} placeholder="SQL, Genel, CRM…"
                       className="w-full rounded-md px-3 py-2 text-sm outline-none"
                       style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
              </label>
            </div>

            <label className="block">
              <div className="text-xs mb-1 flex items-center justify-between" style={{ color: 'var(--mute)' }}>
                <span>İçerik * <span style={{ opacity: 0.6 }}>— {`{{değişken}}`} kullanabilirsiniz</span></span>
                <span style={{ color: 'var(--mute-2)' }}>{content.length} karakter</span>
              </div>
              <textarea value={content} onChange={e => setContent(e.target.value)}
                        rows={10} placeholder={"{{tablo_adı}} tablosundan {{koşul}} koşuluna göre sorgu yaz."}
                        className="w-full rounded-md px-3 py-2 text-sm outline-none resize-y scrollbar-thin font-mono"
                        style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
            </label>

            {/* Variable chips */}
            {vars.length > 0 && (
              <div>
                <div className="text-xs mb-1.5" style={{ color: 'var(--mute)' }}>Tespit edilen değişkenler:</div>
                <div className="flex flex-wrap gap-1.5">
                  {vars.map(v => (
                    <span key={v} className="px-2 py-0.5 rounded-full text-xs font-mono"
                          style={{ background: 'rgba(138,180,248,0.15)', color: 'var(--accent-hi)', border: '1px solid rgba(138,180,248,0.3)' }}>
                      {`{{${v}}}`}
                    </span>
                  ))}
                </div>
              </div>
            )}

            <div className="flex gap-2 pt-1">
              <button onClick={onSave} disabled={saving}
                      className="flex-1 py-2 rounded-lg text-sm font-semibold cursor-pointer disabled:opacity-50"
                      style={{ background: 'var(--accent)', color: '#0b1929' }}>
                {saving ? 'Kaydediliyor…' : (isNew ? 'Oluştur' : 'Güncelle')}
              </button>
              <button onClick={() => { setSelected(null); setIsNew(false) }}
                      className="px-4 py-2 rounded-lg text-sm cursor-pointer"
                      style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
                İptal
              </button>
            </div>
          </div>
        ) : (
          <div className="md:col-span-3 flex items-center justify-center rounded-xl"
               style={{ background: 'var(--surface)', border: '1px solid var(--border)', minHeight: 200 }}>
            <p className="text-sm" style={{ color: 'var(--mute)' }}>Düzenlemek için sol listeden seçin</p>
          </div>
        )}
      </div>
    </section>
  )
}

function TemplateRow({ tmpl, active, onEdit, onDelete }: {
  tmpl: PromptTemplate; active: boolean
  onEdit: () => void; onDelete: () => void
}) {
  return (
    <div className="flex items-center group"
         style={{ borderBottom: '1px solid var(--border)', background: active ? 'rgba(138,180,248,0.1)' : 'transparent' }}>
      <button onClick={onEdit}
              className="flex-1 text-left px-3 py-2.5 cursor-pointer transition"
              style={{ color: active ? 'var(--accent-hi)' : 'var(--text)' }}>
        <div className="text-sm truncate">{tmpl.name}</div>
        {tmpl.variables.length > 0 && (
          <div className="flex gap-1 mt-0.5 flex-wrap">
            {tmpl.variables.map(v => (
              <span key={v} className="text-[9px] px-1 rounded font-mono"
                    style={{ background: 'rgba(138,180,248,0.15)', color: 'var(--accent-hi)' }}>
                {`{{${v}}}`}
              </span>
            ))}
          </div>
        )}
      </button>
      <button onClick={onDelete}
              className="opacity-0 group-hover:opacity-100 px-2.5 py-2 cursor-pointer transition shrink-0"
              style={{ color: '#ea4335' }} title="Sil">
        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6M1 7h22M9 7V4a1 1 0 011-1h4a1 1 0 011 1v3"/>
        </svg>
      </button>
    </div>
  )
}

// =============================================================================
// Tab 6 — Settings (Connection + System Prompt — moved from SettingsPanel)
// =============================================================================

function SettingsTab() {
  const store    = useStore()
  const conv     = store.currentConv()
  const settings = conv?.settings

  const [customModel,  setCustomModel]  = useState(settings?.model ?? '')
  const [keyDraft,     setKeyDraft]     = useState(store.apiKey)
  const [systemPrompt, setSystemPrompt] = useState(settings?.systemPrompt ?? '')
  const [connectBusy,  setConnectBusy]  = useState(false)

  useEffect(() => {
    setCustomModel(settings?.model ?? '')
    setSystemPrompt(settings?.systemPrompt ?? '')
  }, [conv?.id])

  const setPatch = (patch: Parameters<typeof store.updateConvSettings>[1]) => {
    if (!conv) return
    store.updateConvSettings(conv.id, patch)
  }

  const onConnectCustom = async () => {
    setConnectBusy(true)
    store.setStatus('connecting', null)
    try {
      store.setActiveEndpoint(customModel.trim() || null, null)
      setPatch({ model: customModel.trim() || null })
      store.setStatus('connected', true)
    } finally { setConnectBusy(false) }
  }

  const onConnectEndpoint = async (ep: Endpoint, idx: number) => {
    setConnectBusy(true)
    store.setStatus('connecting', null)
    const ok = await pingProxy()
    store.setActiveEndpoint(ep.model, idx)
    setPatch({ model: ep.model })
    store.setStatus(ok ? 'connected' : 'unreachable', ok)
    setConnectBusy(false)
  }

  const onDisconnect = () => {
    store.setActiveEndpoint(null, null)
    setPatch({ model: null })
    store.setStatus('disconnected', false)
  }

  return (
    <section className="space-y-8 max-w-xl">
      <div>
        <h2 className="text-lg font-medium">Gelişmiş Ayarlar</h2>
        <p className="text-xs mt-1" style={{ color: 'var(--mute)' }}>
          Bağlantı ve sistem komutu ayarları. Değişiklikler aktif sohbete uygulanır.
        </p>
      </div>

      {/* ── Connection ─────────────────────────────────────────────────── */}
      <div className="rounded-xl p-5 space-y-4"
           style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
        <div className="text-xs font-semibold uppercase tracking-wider"
             style={{ color: 'var(--mute)' }}>
          Connection
        </div>

        <label className="block">
          <div className="text-xs mb-1.5" style={{ color: 'var(--mute)' }}>{t('modelName')}</div>
          <input
            value={customModel}
            onChange={e => setCustomModel(e.target.value)}
            placeholder="chat / code / reason / qwen3.6-27b"
            className="w-full rounded-md px-3 py-2 text-sm outline-none"
            style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
          />
        </label>

        <label className="block">
          <div className="text-xs mb-1.5" style={{ color: 'var(--mute)' }}>{t('apiKey')}</div>
          <div className="flex gap-2">
            <input
              value={keyDraft}
              type="password"
              onChange={e => setKeyDraft(e.target.value)}
              placeholder="sk-..."
              className="flex-1 rounded-md px-3 py-2 text-sm outline-none"
              style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
            />
            <button
              onClick={() => store.setApiKey(keyDraft)}
              className="px-3 py-2 rounded-md text-xs cursor-pointer"
              style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text)' }}
            >
              Kaydet
            </button>
          </div>
        </label>

        <div className="flex gap-2">
          <button
            onClick={onConnectCustom}
            disabled={connectBusy}
            className="flex-1 rounded-md py-2 text-sm font-medium cursor-pointer disabled:opacity-50"
            style={{ background: 'var(--accent)', color: '#0b1929' }}
          >
            {connectBusy ? t('connecting') : t('connect')}
          </button>
          <button
            onClick={onDisconnect}
            className="rounded-md px-4 py-2 text-xs cursor-pointer"
            style={{ border: '1px solid #ef4444', color: '#ef4444', background: 'transparent' }}
          >
            {t('disconnect')}
          </button>
        </div>

        <div className="flex flex-wrap gap-1.5">
          {(store.endpoints.length ? store.endpoints : DEFAULT_ENDPOINTS).map((ep, i) => {
            const active = store.activeEpIdx === i
            return (
              <button key={ep.name + i}
                      onClick={() => onConnectEndpoint(ep, i)}
                      className={`pill ${active ? 'active' : ''}`}>
                <span className={`status-dot ${active && store.statusOk ? 'ok' : ''}`} />
                {ep.name}
              </button>
            )
          })}
        </div>
      </div>

      {/* ── System Prompt ──────────────────────────────────────────────── */}
      <div className="rounded-xl p-5 space-y-3"
           style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
        <div className="text-xs font-semibold uppercase tracking-wider"
             style={{ color: 'var(--mute)' }}>
          {t('systemPrompt')}
        </div>
        <p className="text-xs" style={{ color: 'var(--mute)' }}>
          Aktif sohbet için sistem komutu. Skill seçilince skill prompt'u devralır.
        </p>
        <textarea
          value={systemPrompt}
          onChange={e => {
            setSystemPrompt(e.target.value)
            setPatch({ systemPrompt: e.target.value })
          }}
          rows={8}
          placeholder="You are a helpful assistant..."
          className="w-full rounded-md px-3 py-2 text-sm outline-none resize-y scrollbar-thin font-mono"
          style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
        />
        {!conv && (
          <p className="text-xs" style={{ color: '#f59e0b' }}>
            ⚠ Aktif sohbet yok — chat ekranında bir sohbet açın.
          </p>
        )}
      </div>
    </section>
  )
}

// =============================================================================
// Helpers
// =============================================================================

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / 1024 / 1024).toFixed(2)} MB`
}

function formatDate(iso: string): string {
  try {
    const d = new Date(iso)
    return d.toLocaleString()
  } catch {
    return iso
  }
}
