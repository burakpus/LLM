import { useEffect, useState, useRef, useCallback, Fragment } from 'react'
import { useStore, t, DEFAULT_ENDPOINTS } from '../../store'
import type { Endpoint } from '../../store'
import {
  uploadFiles, listDocuments, listCollections,
  deleteDocument, listSkills, getSkill, uploadSkills, deleteSkill,
  importAnthropicSkills, ANTHROPIC_SKILLS, setSkillOrder,
  getUsageUsers, getUsageModels, getUsageLogs,
  listTemplates, createTemplate, updateTemplate, deleteTemplate,
  getRatingStats,
  listSkillExamples, createSkillExample, updateSkillExample, deleteSkillExample,
  getActivityLog,
  listSqlConnections, createSqlConnection, updateSqlConnection,
  deleteSqlConnection, testSqlConnection, testSqlCredentials,
  listSqlObjects, ingestSqlSchema, syncSqlSchema,
  listSqlTables, ingestSqlData,
  getLatestJobForConnection,
  getSqlIngestedStats,
  listAdminJobs, cancelJob, retryJob,
  getEventLog, getEventLogSummary,
} from '../../api/admin'
import type {
  UploadResult, DocumentsPage, CollectionRow, SkillRow,
  UserSpend, ModelSpend, SpendLog, PromptTemplate, RatingStats, SkillExample,
  ActivityPage, SqlConnection, SqlConnectionUpsert, SqlDbType,
  SqlObjectSummary, SqlIngestResult, SqlSyncResult,
  SqlTable, SqlTableSpec, SqlDataIngestResult,
  JobInfo, SqlIngestedStats, JobsPage,
  ImportAnthropicResult,
  EventLogEntry, EventLogPage, EventLogFilters, EventLogSummary,
} from '../../api/admin'
import SetLogo from '../SetLogo'
import JobProgressModal from './JobProgressModal'
import SqlDataDialog from './SqlDataDialog'

type Tab = 'upload' | 'documents' | 'skills' | 'templates' | 'sql' | 'jobs' | 'usage' | 'activity' | 'security' | 'settings'

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
  const store = useStore()
  const { auth } = store
  const [tab,      setTab]      = useState<Tab>('upload')
  const [checking, setChecking] = useState(!auth.isAdmin)

  useEffect(() => {
    const stored = localStorage.getItem('setllm-theme')
    document.documentElement.setAttribute('data-theme', stored === 'light' ? 'light' : 'dark')
  }, [])

  // Re-check admin status from server on every /admin visit.
  // Fixes stale JWT tokens that were issued before AdminUsers config was added.
  useEffect(() => {
    if (auth.isAdmin) { setChecking(false); return }
    const tok = localStorage.getItem('setllm-token')
    if (!tok) { setChecking(false); return }
    fetch('/api/auth/me', { headers: { Authorization: `Bearer ${tok}` } })
      .then(r => r.ok ? r.json() : null)
      .then(data => {
        if (data?.isAdmin) {
          store.setAuth({ ...auth, isAdmin: true })
        }
      })
      .catch(() => {})
      .finally(() => setChecking(false))
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  if (checking) {
    return (
      <div className="min-h-dvh flex items-center justify-center"
           style={{ background: 'var(--bg)', color: 'var(--mute)' }}>
        <span className="text-sm">Yetki kontrol ediliyor…</span>
      </div>
    )
  }

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
          {(['upload', 'documents', 'skills', 'templates', 'sql', 'jobs', 'usage', 'activity', 'security', 'settings'] as Tab[]).map(tb => (
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
              {tb === 'upload' ? 'Upload' : tb === 'documents' ? 'Documents' : tb === 'skills' ? 'Skills' : tb === 'templates' ? 'Şablonlar' : tb === 'sql' ? 'SQL' : tb === 'jobs' ? 'İşler' : tb === 'usage' ? 'Kullanım' : tb === 'activity' ? 'Aktivite' : tb === 'security' ? '🛡 Güvenlik' : '⚙ Ayarlar'}
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
          {tab === 'sql'       && <SqlConnectionsTab />}
          {tab === 'jobs'      && <JobsTab />}
          {tab === 'usage'     && <UsageTab />}
          {tab === 'activity'  && <ActivityTab />}
          {tab === 'security'  && <SecurityTab />}
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
  // Anthropic Import modal
  const [showImport,    setShowImport]    = useState(false)
  const [importSelected, setImportSelected] = useState<Set<string>>(new Set())
  const [importOverwrite, setImportOverwrite] = useState(false)
  const [importing,      setImporting]     = useState(false)
  const [importResult,   setImportResult]  = useState<ImportAnthropicResult | null>(null)

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
          <input ref={fileRef} type="file" accept=".md,.zip" multiple className="hidden" onChange={onUpload} />
          <button
            onClick={() => { setShowImport(true); setImportResult(null) }}
            className="px-3 py-1.5 rounded-lg text-xs font-medium cursor-pointer transition"
            style={{ background: 'rgba(138,180,248,0.15)', color: 'var(--accent-hi)', border: '1px solid rgba(138,180,248,0.3)' }}
            title="anthropics/skills GitHub repo'sundan resmi skill'leri indir"
          >
            📥 Anthropic Import
          </button>
          <button
            onClick={() => fileRef.current?.click()}
            className="px-3 py-1.5 rounded-lg text-xs font-medium cursor-pointer transition"
            style={{ background: 'var(--accent)', color: '#0b1929' }}
          >
            + Skill Yükle (.md/.zip)
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
          <div className="px-3 py-1.5 text-[10px] flex items-center justify-between"
               style={{ background: 'var(--surface-2)', color: 'var(--mute)', borderBottom: '1px solid var(--border)' }}>
            <span>SIRA</span>
            <span>SKILL</span>
            <span>BOYUT</span>
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
                    {/* Order input — inline edit */}
                    <SkillOrderInput
                      skillId={s.id}
                      initialOrder={s.order ?? 999}
                      onSaved={(newOrder) => setSkills(prev => {
                        const next = prev.map(x => x.id === s.id ? { ...x, order: newOrder } : x)
                        // Re-sort: order asc, name asc
                        next.sort((a, b) => (a.order ?? 999) - (b.order ?? 999) || (a.name || a.id).localeCompare(b.name || b.id))
                        return next
                      })}
                    />
                    <button
                      onClick={() => openSkill(s.id)}
                      className="flex-1 text-left px-3 py-2.5 text-sm cursor-pointer transition flex items-center justify-between gap-2 min-w-0"
                      style={{ color: active ? 'var(--accent-hi)' : 'var(--text)' }}
                    >
                      <span className="truncate flex items-center gap-1.5 min-w-0">
                        <span className="truncate">{s.name || s.id}</span>
                        {s.isFolder && (
                          <span className="px-1 py-0.5 text-[9px] rounded shrink-0"
                                style={{ background: 'rgba(52,168,83,0.15)', color: '#34a853', border: '1px solid rgba(52,168,83,0.25)' }}
                                title={`Folder skill: ${s.referenceCount ?? 0} referans dosyası`}>
                            📁 {s.referenceCount ?? 0}
                          </span>
                        )}
                      </span>
                      <span className="text-[10px] shrink-0" style={{ color: 'var(--mute)' }}>
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

      {/* Anthropic Skills Import Modal */}
      {showImport && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4"
             style={{ background: 'rgba(0,0,0,0.6)', backdropFilter: 'blur(4px)' }}
             onClick={e => { if (e.target === e.currentTarget && !importing) setShowImport(false) }}>
          <div className="w-full max-w-2xl rounded-2xl overflow-hidden shadow-2xl flex flex-col"
               style={{ background: 'var(--bg)', border: '1px solid var(--border)', maxHeight: '85vh' }}>
            <div className="px-5 py-4 flex items-center gap-3 shrink-0"
                 style={{ borderBottom: '1px solid var(--border)', background: 'var(--surface)' }}>
              <span className="text-2xl">📥</span>
              <div className="flex-1">
                <div className="font-semibold" style={{ color: 'var(--text)' }}>Anthropic Skills Import</div>
                <div className="text-xs" style={{ color: 'var(--mute)' }}>
                  anthropics/skills GitHub repo&apos;sundan direkt indir (SKILL.md + referans .md dosyaları)
                </div>
              </div>
              <button onClick={() => !importing && setShowImport(false)}
                      disabled={importing}
                      className="w-8 h-8 rounded-full flex items-center justify-center cursor-pointer text-lg disabled:opacity-30"
                      style={{ color: 'var(--mute)' }}>×</button>
            </div>

            <div className="flex-1 overflow-y-auto p-5 space-y-3">
              <div className="flex items-center gap-3 text-xs flex-wrap" style={{ color: 'var(--mute)' }}>
                <button onClick={() => setImportSelected(new Set(Object.keys(ANTHROPIC_SKILLS)))}
                        className="cursor-pointer hover:underline"
                        style={{ color: 'var(--accent-hi)' }}>
                  Tümünü seç (17)
                </button>
                <span>·</span>
                <button onClick={() => setImportSelected(new Set())}
                        className="cursor-pointer hover:underline">
                  Tümünü kaldır
                </button>
                <span>·</span>
                <label className="flex items-center gap-1.5 cursor-pointer">
                  <input type="checkbox" checked={importOverwrite}
                         onChange={e => setImportOverwrite(e.target.checked)} />
                  Mevcut olanların üzerine yaz
                </label>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-2 gap-2">
                {Object.entries(ANTHROPIC_SKILLS).map(([id, info]) => {
                  const checked = importSelected.has(id)
                  return (
                    <label key={id}
                           className="flex items-start gap-2 p-2 rounded-lg cursor-pointer text-xs"
                           style={{
                             background: checked ? 'rgba(138,180,248,0.10)' : 'var(--surface-2)',
                             border: `1px solid ${checked ? 'rgba(138,180,248,0.35)' : 'var(--border)'}`,
                           }}>
                      <input type="checkbox" checked={checked} className="mt-0.5 cursor-pointer"
                             onChange={e => setImportSelected(prev => {
                               const next = new Set(prev)
                               if (e.target.checked) next.add(id); else next.delete(id)
                               return next
                             })} />
                      <div className="min-w-0 flex-1">
                        <div className="font-medium flex items-center gap-1.5" style={{ color: 'var(--text)' }}>
                          <span className="font-mono">{id}</span>
                          {info.hasRefs && (
                            <span className="px-1 py-0.5 text-[9px] rounded"
                                  style={{ background: 'rgba(52,168,83,0.15)', color: '#34a853' }}>
                              +refs
                            </span>
                          )}
                        </div>
                        <div className="truncate" style={{ color: 'var(--mute)' }}>{info.description}</div>
                      </div>
                    </label>
                  )
                })}
              </div>

              {importResult && (
                <div className="rounded-xl p-3 space-y-1 text-xs"
                     style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
                  <div className="font-semibold mb-2" style={{ color: 'var(--text)' }}>
                    İçe aktarıldı: {importResult.imported} / {importResult.results.length}
                  </div>
                  <div className="max-h-48 overflow-y-auto space-y-0.5">
                    {importResult.results.map((r, i) => (
                      <div key={i} className="flex items-center gap-2"
                           style={{ color: r.ok ? '#34a853' : '#ea4335' }}>
                        <span>{r.ok ? '✓' : '✕'}</span>
                        <span className="font-mono">{r.skill}</span>
                        <span style={{ color: 'var(--mute)' }}>
                          {r.action ?? r.error}
                          {r.files ? ` (${r.files} dosya)` : ''}
                        </span>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>

            <div className="px-5 py-4 flex gap-2 shrink-0"
                 style={{ borderTop: '1px solid var(--border)', background: 'var(--surface)' }}>
              <button onClick={async () => {
                        if (importSelected.size === 0) return
                        setImporting(true); setImportResult(null); setError(null)
                        try {
                          const res = await importAnthropicSkills(Array.from(importSelected), importOverwrite)
                          setImportResult(res)
                          reload()
                        } catch (e: any) {
                          setError(`Import hatası: ${e.message}`)
                        } finally {
                          setImporting(false)
                        }
                      }}
                      disabled={importing || importSelected.size === 0}
                      className="flex-1 py-2 rounded-lg text-sm font-semibold cursor-pointer disabled:opacity-50"
                      style={{ background: 'var(--accent)', color: '#0b1929' }}>
                {importing
                  ? `İndiriliyor… (${importSelected.size} skill)`
                  : `📥 ${importSelected.size > 0 ? importSelected.size : 'Seçili'} Skill İndir`}
              </button>
              <button onClick={() => !importing && setShowImport(false)}
                      disabled={importing}
                      className="px-4 py-2 rounded-lg text-sm cursor-pointer disabled:opacity-50"
                      style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
                Kapat
              </button>
            </div>
          </div>
        </div>
      )}
    </section>
  )
}

// Small inline-editable order input for skill list
function SkillOrderInput({ skillId, initialOrder, onSaved }: {
  skillId: string
  initialOrder: number
  onSaved: (order: number) => void
}) {
  const [val, setVal] = useState(String(initialOrder))
  const [saving, setSaving] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  useEffect(() => { setVal(String(initialOrder)) }, [initialOrder])

  const commit = async () => {
    const n = parseInt(val, 10)
    if (isNaN(n) || n === initialOrder) { setVal(String(initialOrder)); return }
    setSaving(true); setErr(null)
    try {
      const res = await setSkillOrder(skillId, n)
      onSaved(res.order)
    } catch (e: any) {
      setErr(e.message)
      setVal(String(initialOrder))
    } finally {
      setSaving(false)
    }
  }

  return (
    <input type="number" min={0} max={9999}
           value={val}
           onChange={e => setVal(e.target.value)}
           onBlur={commit}
           onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }}
           disabled={saving}
           title={err ?? 'Skill sırası (düşük = önce). Enter veya Tab ile kaydet.'}
           className="w-12 ml-2 my-1 px-1 py-0.5 text-[11px] text-center rounded outline-none font-mono shrink-0"
           style={{
             background: err ? 'rgba(234,67,53,0.12)' : 'var(--input-bg)',
             border: `1px solid ${err ? '#ea4335' : 'var(--border)'}`,
             color: 'var(--text)',
             opacity: saving ? 0.5 : 1,
           }} />
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
// Tab — Jobs (background job history with filter / cancel / retry)
// =============================================================================

const JOB_TYPE_LABELS: Record<string, string> = {
  'sql.ingest-schema':  'SQL Şema Çıkarımı',
  'sql.sync-schema':    'SQL Şema Senkron',
  'sql.ingest-data':    'SQL Veri Çıkarımı',
  'sql.sync-data':      'SQL Veri Senkron',
}

const JOB_STATUS_LABELS: Record<string, { label: string; color: string }> = {
  queued:    { label: 'Kuyrukta',   color: '#f9ab00' },
  running:   { label: 'Çalışıyor',  color: 'var(--accent-hi)' },
  completed: { label: 'Tamamlandı', color: '#34a853' },
  failed:    { label: 'Hata',       color: '#ea4335' },
  cancelled: { label: 'İptal',      color: '#9aa0a6' },
}

function jobTypeLabel(t: string) { return JOB_TYPE_LABELS[t] ?? t }
function fmtDuration(ms: number) {
  if (ms < 1000) return `${ms}ms`
  const s = Math.round(ms / 1000)
  if (s < 60) return `${s}sn`
  const m = Math.floor(s / 60)
  const rs = s % 60
  if (m < 60) return rs ? `${m}d ${rs}sn` : `${m}d`
  const h = Math.floor(m / 60)
  return `${h}sa ${m % 60}d`
}

function JobsTab() {
  const [page,     setPage]     = useState<JobsPage | null>(null)
  const [pageNum,  setPageNum]  = useState(1)
  const [typeFlt,  setTypeFlt]  = useState<string>('')
  const [statFlt,  setStatFlt]  = useState<string>('')
  const [loading,  setLoading]  = useState(true)
  const [busyId,   setBusyId]   = useState<number | null>(null)
  const [error,    setError]    = useState<string | null>(null)
  const [msg,      setMsg]      = useState<string | null>(null)
  const [activeJob, setActiveJob] = useState<number | null>(null)

  const load = useCallback(async () => {
    setLoading(true); setError(null)
    try {
      const p = await listAdminJobs({
        page: pageNum, pageSize: 50,
        type:   typeFlt || undefined,
        status: statFlt || undefined,
      })
      setPage(p)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setLoading(false)
    }
  }, [pageNum, typeFlt, statFlt])

  useEffect(() => { load() }, [load])

  // Auto-refresh every 4s when there are running/queued jobs
  useEffect(() => {
    if (!page) return
    const hasLive = page.items.some(j => j.status === 'running' || j.status === 'queued')
    if (!hasLive) return
    const t = window.setInterval(load, 4000)
    return () => window.clearInterval(t)
  }, [page, load])

  const onCancel = async (id: number) => {
    setBusyId(id); setError(null); setMsg(null)
    try {
      const r = await cancelJob(id)
      if (r.ok) { setMsg(`#${id} iptal edildi`); await load() }
      else      { setError(r.error ?? 'İptal başarısız') }
    } finally { setBusyId(null) }
  }

  const onRetry = async (id: number) => {
    setBusyId(id); setError(null); setMsg(null)
    try {
      const r = await retryJob(id)
      if (r.ok) { setMsg(`#${id} → yeni iş #${r.newId} kuyruğa alındı`); await load() }
      else      { setError(r.error ?? 'Tekrar deneme başarısız') }
    } finally { setBusyId(null) }
  }

  const total      = page?.total ?? 0
  const pageSize   = page?.pageSize ?? 50
  const totalPages = Math.max(1, Math.ceil(total / pageSize))

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between flex-wrap gap-2">
        <h2 className="text-base font-semibold" style={{ color: 'var(--text)' }}>
          Arka Plan İşleri
        </h2>
        <div className="flex items-center gap-2 flex-wrap">
          <select value={typeFlt} onChange={e => { setTypeFlt(e.target.value); setPageNum(1) }}
                  className="rounded-md px-2 py-1.5 text-xs outline-none cursor-pointer"
                  style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}>
            <option value="">Tüm tipler</option>
            {Object.entries(JOB_TYPE_LABELS).map(([k, v]) => (
              <option key={k} value={k}>{v}</option>
            ))}
          </select>
          <select value={statFlt} onChange={e => { setStatFlt(e.target.value); setPageNum(1) }}
                  className="rounded-md px-2 py-1.5 text-xs outline-none cursor-pointer"
                  style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}>
            <option value="">Tüm durumlar</option>
            {Object.entries(JOB_STATUS_LABELS).map(([k, v]) => (
              <option key={k} value={k}>{v.label}</option>
            ))}
          </select>
          <button onClick={load} disabled={loading}
                  className="px-3 py-1.5 rounded-md text-xs cursor-pointer disabled:opacity-50"
                  style={{ background: 'var(--surface-hi)', color: 'var(--text-2)', border: '1px solid var(--border)' }}>
            {loading ? '…' : '🔄 Yenile'}
          </button>
        </div>
      </div>

      {error && <div className="text-xs px-3 py-2 rounded" style={{ background: 'rgba(234,67,53,0.12)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}>{error}</div>}
      {msg   && <div className="text-xs px-3 py-2 rounded" style={{ background: 'rgba(52,168,83,0.12)', color: '#34a853', border: '1px solid rgba(52,168,83,0.3)' }}>{msg}</div>}

      <div className="rounded-lg overflow-hidden" style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
        <table className="w-full text-sm">
          <thead style={{ background: 'var(--surface-hi)', color: 'var(--mute)' }}>
            <tr>
              <th className="px-3 py-2 text-left font-medium">#</th>
              <th className="px-3 py-2 text-left font-medium">Tip</th>
              <th className="px-3 py-2 text-left font-medium">Durum</th>
              <th className="px-3 py-2 text-left font-medium">İlerleme</th>
              <th className="px-3 py-2 text-left font-medium">Süre</th>
              <th className="px-3 py-2 text-left font-medium">Kullanıcı</th>
              <th className="px-3 py-2 text-left font-medium">Başlangıç</th>
              <th className="px-3 py-2 text-right font-medium">İşlemler</th>
            </tr>
          </thead>
          <tbody>
            {loading && <tr><td colSpan={8} className="px-3 py-6 text-center text-xs" style={{ color: 'var(--mute)' }}>Yükleniyor…</td></tr>}
            {!loading && (!page || page.items.length === 0) && (
              <tr><td colSpan={8} className="px-3 py-6 text-center text-xs" style={{ color: 'var(--mute)' }}>Bu filtreye uyan iş yok.</td></tr>
            )}
            {!loading && page?.items.map(j => {
              const meta = JOB_STATUS_LABELS[j.status] ?? { label: j.status, color: 'var(--mute)' }
              const pct  = j.progressTot > 0 ? Math.round((j.progressCur / j.progressTot) * 100) : 0
              const dur  = (() => {
                const start = j.startedAt ? new Date(j.startedAt).getTime() : null
                const end   = j.completedAt ? new Date(j.completedAt).getTime() : (j.status === 'running' ? Date.now() : null)
                if (!start || !end) return '—'
                return fmtDuration(end - start)
              })()
              return (
                <tr key={j.id} style={{ borderTop: '1px solid var(--border)' }}>
                  <td className="px-3 py-2 font-mono text-xs" style={{ color: 'var(--text-2)' }}>{j.id}</td>
                  <td className="px-3 py-2 text-xs" style={{ color: 'var(--text)' }}>{jobTypeLabel(j.type)}</td>
                  <td className="px-3 py-2 text-xs">
                    <span className="px-1.5 py-0.5 rounded font-medium" style={{ background: 'transparent', color: meta.color, border: `1px solid ${meta.color}` }}>
                      {meta.label}
                    </span>
                  </td>
                  <td className="px-3 py-2 text-xs" style={{ color: 'var(--mute)' }}>
                    {j.progressTot > 0
                      ? <span>{j.progressCur}/{j.progressTot} <span style={{ opacity: 0.6 }}>(%{pct})</span></span>
                      : <span style={{ opacity: 0.6 }}>—</span>}
                    {j.message && <div className="text-xs truncate max-w-xs" style={{ opacity: 0.7 }}>{j.message}</div>}
                  </td>
                  <td className="px-3 py-2 text-xs font-mono" style={{ color: 'var(--mute)' }}>{dur}</td>
                  <td className="px-3 py-2 text-xs" style={{ color: 'var(--mute)' }}>{j.createdBy}</td>
                  <td className="px-3 py-2 text-xs font-mono" style={{ color: 'var(--mute)' }}>
                    {j.startedAt ? new Date(j.startedAt).toLocaleString() : new Date(j.createdAt).toLocaleString()}
                  </td>
                  <td className="px-3 py-2 text-right">
                    <div className="inline-flex gap-1">
                      <button onClick={() => setActiveJob(j.id)}
                              className="px-2 py-1 rounded text-xs cursor-pointer"
                              style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}
                              title="Detay / canlı izle">
                        🔍
                      </button>
                      {j.status === 'queued' && (
                        <button onClick={() => onCancel(j.id)} disabled={busyId === j.id}
                                className="px-2 py-1 rounded text-xs cursor-pointer disabled:opacity-50"
                                style={{ background: 'rgba(234,67,53,0.12)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}
                                title="İptal et">
                          {busyId === j.id ? '…' : '🛑'}
                        </button>
                      )}
                      {(j.status === 'failed' || j.status === 'cancelled') && (
                        <button onClick={() => onRetry(j.id)} disabled={busyId === j.id}
                                className="px-2 py-1 rounded text-xs cursor-pointer disabled:opacity-50"
                                style={{ background: 'rgba(138,180,248,0.15)', color: 'var(--accent-hi)', border: '1px solid rgba(138,180,248,0.3)' }}
                                title="Aynı parametrelerle tekrar dene">
                          {busyId === j.id ? '…' : '↻'}
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>

      {/* Pagination */}
      {total > pageSize && (
        <div className="flex items-center justify-between text-xs" style={{ color: 'var(--mute)' }}>
          <div>Toplam: <span style={{ color: 'var(--text-2)' }}>{total}</span></div>
          <div className="flex items-center gap-2">
            <button disabled={pageNum <= 1} onClick={() => setPageNum(n => Math.max(1, n - 1))}
                    className="px-2 py-1 rounded cursor-pointer disabled:opacity-40"
                    style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>‹</button>
            <span>Sayfa {pageNum} / {totalPages}</span>
            <button disabled={pageNum >= totalPages} onClick={() => setPageNum(n => Math.min(totalPages, n + 1))}
                    className="px-2 py-1 rounded cursor-pointer disabled:opacity-40"
                    style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>›</button>
          </div>
        </div>
      )}

      {activeJob != null && (
        <JobProgressModal
          jobId={activeJob}
          title={`İş #${activeJob}`}
          onClose={() => { setActiveJob(null); load() }} />
      )}
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
// Tab — SQL Connections (Phase 1: CRUD + Test)
// =============================================================================

const DB_TYPE_OPTIONS: { id: SqlDbType; label: string; defaultPort: number }[] = [
  { id: 'mssql',    label: 'MS SQL Server', defaultPort: 1433 },
  { id: 'postgres', label: 'PostgreSQL',    defaultPort: 5432 },
  { id: 'mysql',    label: 'MySQL',         defaultPort: 3306 },
  { id: 'oracle',   label: 'Oracle',        defaultPort: 1521 },
]

const EMPTY_CONN: SqlConnectionUpsert = {
  name: '', dbType: 'mssql', host: '', port: 1433, database: '', username: '', password: '',
  queryTimeoutSec: 120, autoSyncIntervalMin: 0,
}

const OBJ_TYPES = ['table', 'view', 'procedure', 'function', 'trigger'] as const

function SqlConnectionsTab() {
  const [items,     setItems]     = useState<SqlConnection[]>([])
  const [loading,   setLoading]   = useState(true)
  const [error,     setError]     = useState<string | null>(null)
  const [msg,       setMsg]       = useState<string | null>(null)
  const [editingId, setEditingId] = useState<number | null>(null)
  const [draft,     setDraft]     = useState<SqlConnectionUpsert>(EMPTY_CONN)
  const [showForm,  setShowForm]  = useState(false)
  const [busy,      setBusy]      = useState(false)
  const [testing,   setTesting]   = useState<number | 'draft' | null>(null)
  // Schema ingest modal
  const [ingestConn,    setIngestConn]    = useState<SqlConnection | null>(null)
  const [ingestPreview, setIngestPreview] = useState<SqlObjectSummary | null>(null)
  const [ingestStats,   setIngestStats]   = useState<SqlIngestedStats | null>(null)
  const [ingestTypes,   setIngestTypes]   = useState<string[]>([...OBJ_TYPES])
  const [ingestCollection, setIngestCollection] = useState('')
  const [ingestRunning, setIngestRunning] = useState(false)
  const [ingestLastJob, setIngestLastJob] = useState<JobInfo | null>(null)
  // Data sync dialog (new — replaces old data sampling modal)
  const [dataConn, setDataConn] = useState<SqlConnection | null>(null)
  // Active background job (single shared modal)
  const [activeJob, setActiveJob] = useState<{ id: number; title: string; subtitle?: string } | null>(null)
  // Sync start dialog
  const [syncDialog, setSyncDialog] = useState<{ conn: SqlConnection; lastJob: JobInfo | null; loading: boolean } | null>(null)

  const load = async () => {
    setLoading(true); setError(null)
    try { setItems(await listSqlConnections()) }
    catch (e: any) { setError(e.message) }
    finally { setLoading(false) }
  }

  useEffect(() => { load() }, [])

  const openNew = () => {
    setEditingId(null); setDraft(EMPTY_CONN); setShowForm(true); setMsg(null); setError(null)
  }

  const openEdit = (c: SqlConnection) => {
    setEditingId(c.id)
    setDraft({
      name: c.name, dbType: c.dbType, host: c.host, port: c.port,
      database: c.database, username: c.username, password: '',
      queryTimeoutSec: c.queryTimeoutSec ?? 120,
      autoSyncIntervalMin: c.autoSyncIntervalMin ?? 0,
    })
    setShowForm(true); setMsg(null); setError(null)
  }

  const onDbTypeChange = (id: SqlDbType) => {
    const def = DB_TYPE_OPTIONS.find(d => d.id === id)
    setDraft(d => ({ ...d, dbType: id, port: def?.defaultPort ?? 0 }))
  }

  const onSave = async () => {
    if (!draft.name.trim() || !draft.host.trim() || !draft.database.trim()) {
      setError('Ad, sunucu ve veritabanı zorunludur'); return
    }
    setBusy(true); setError(null); setMsg(null)
    try {
      if (editingId == null) {
        const created = await createSqlConnection(draft)
        // Surgical: prepend new record to local state (no full list refetch)
        setItems(prev => [...prev, created].sort((a, b) => a.name.localeCompare(b.name)))
        setMsg(`"${created.name}" oluşturuldu.`)
      } else {
        const updated = await updateSqlConnection(editingId, draft)
        // Surgical: replace just the edited record
        setItems(prev => prev.map(c => c.id === updated.id ? updated : c))
        setMsg(`"${updated.name}" güncellendi.`)
      }
      setShowForm(false); setEditingId(null); setDraft(EMPTY_CONN)
    } catch (e: any) { setError(e.message) }
    finally { setBusy(false) }
  }

  const onDelete = async (c: SqlConnection) => {
    if (!confirm(`"${c.name}" bağlantısını sil?`)) return
    try {
      await deleteSqlConnection(c.id)
      // Surgical: filter out just the deleted record
      setItems(prev => prev.filter(x => x.id !== c.id))
      setMsg(`"${c.name}" silindi.`)
    } catch (e: any) { setError(e.message) }
  }

  const onTest = async (c: SqlConnection) => {
    setTesting(c.id); setMsg(null); setError(null)
    try {
      const r = await testSqlConnection(c.id)
      if (r.ok) setMsg(`✓ "${c.name}" bağlantısı başarılı`)
      else setError(`"${c.name}" bağlantı hatası: ${r.error}`)
    } catch (e: any) { setError(e.message) }
    finally { setTesting(null) }
  }

  const onTestDraft = async () => {
    setTesting('draft'); setMsg(null); setError(null)
    try {
      const r = await testSqlCredentials(draft)
      if (r.ok) setMsg('✓ Bağlantı başarılı (form üzerinden test)')
      else setError(`Bağlantı hatası: ${r.error}`)
    } catch (e: any) { setError(e.message) }
    finally { setTesting(null) }
  }

  const openIngest = async (c: SqlConnection) => {
    setIngestConn(c)
    setIngestPreview(null); setIngestStats(null); setIngestLastJob(null)
    setIngestTypes([...OBJ_TYPES])
    setIngestCollection(`sql-${c.name.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`)
    try {
      const [preview, stats, lastJob] = await Promise.all([
        listSqlObjects(c.id),
        getSqlIngestedStats(c.id).catch(() => null),
        getLatestJobForConnection(c.id, 'sql.ingest-schema').catch(() => null),
      ])
      setIngestPreview(preview)
      setIngestStats(stats)
      setIngestLastJob(lastJob)
      // If RAG already has data, prefer its existing collection name
      if (stats?.collection) setIngestCollection(stats.collection)
    } catch (e: any) {
      setError(`Önizleme hatası: ${e.message}`)
      setIngestConn(null)
    }
  }

  const onIngestRun = async () => {
    if (!ingestConn) return
    setIngestRunning(true)
    try {
      // Backend now enqueues a background job
      const r = await fetch(`/api/admin/sql-connections/${ingestConn.id}/ingest-schema`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${localStorage.getItem('setllm-token')}` },
        body:   JSON.stringify({ collection: ingestCollection.trim(), includeTypes: ingestTypes }),
      })
      if (!r.ok) throw new Error(`HTTP ${r.status}`)
      const { jobId } = await r.json()
      // Close ingest modal, open job progress modal
      const conn = ingestConn
      setIngestConn(null); setIngestPreview(null); setIngestLastJob(null)
      setActiveJob({ id: jobId, title: `Şema Çıkarımı — ${conn.name}`,
        subtitle: 'Tüm CREATE script\'leri RAG\'a yazılıyor (arkada çalışıyor)' })
    } catch (e: any) {
      setError(e.message)
    } finally {
      setIngestRunning(false)
    }
  }

  const openData = (c: SqlConnection) => setDataConn(c)

  // Sync button → open dialog showing last/current state + Start button
  const onSyncRun = async (c: SqlConnection) => {
    setError(null); setMsg(null)
    setSyncDialog({ conn: c, lastJob: null, loading: true })
    try {
      const lastJob = await getLatestJobForConnection(c.id, 'sql.sync-schema')
      setSyncDialog({ conn: c, lastJob, loading: false })
    } catch (e: any) {
      setError(e.message)
      setSyncDialog(null)
    }
  }

  // Actually start a new sync job from the dialog
  const onSyncStart = async () => {
    if (!syncDialog) return
    const c = syncDialog.conn
    try {
      const r = await fetch(`/api/admin/sql-connections/${c.id}/sync-schema`, {
        method: 'POST',
        headers: { Authorization: `Bearer ${localStorage.getItem('setllm-token')}` },
      })
      if (!r.ok) {
        const err = await r.json().catch(() => ({ error: r.statusText }))
        throw new Error(err?.error ?? `HTTP ${r.status}`)
      }
      const { jobId } = await r.json()
      setSyncDialog(null)
      setActiveJob({ id: jobId, title: `Senkronizasyon — ${c.name}`,
        subtitle: 'Değişen/yeni/silinen objeler güncelleniyor (arkada çalışıyor)' })
    } catch (e: any) {
      setError(e.message)
    }
  }

  return (
    <section className="space-y-5">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h2 className="text-lg font-medium">SQL Kaynakları</h2>
          <p className="text-xs mt-1" style={{ color: 'var(--mute)' }}>
            MS SQL, PostgreSQL, MySQL, Oracle — şema ve veri çıkarımı için bağlantılar.
            Şifreler sunucuda şifrelenmiş saklanır.
          </p>
        </div>
        <button onClick={openNew}
                className="px-3 py-1.5 rounded-lg text-xs font-medium cursor-pointer shrink-0"
                style={{ background: 'var(--accent)', color: '#0b1929' }}>
          + Yeni Bağlantı
        </button>
      </div>

      {error && <div className="rounded-md px-3 py-2 text-xs" style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}>{error}</div>}
      {msg   && <div className="rounded-md px-3 py-2 text-xs" style={{ background: 'rgba(52,168,83,0.1)',  color: '#34a853', border: '1px solid rgba(52,168,83,0.3)'  }}>{msg}</div>}

      {/* Form */}
      {showForm && (
        <div className="rounded-xl p-5 space-y-3"
             style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
          <div className="text-sm font-semibold" style={{ color: 'var(--text)' }}>
            {editingId == null ? '+ Yeni Bağlantı' : `Düzenle: ${draft.name}`}
          </div>

          <div className="grid grid-cols-2 gap-3">
            <label className="block col-span-2 md:col-span-1">
              <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Ad *</div>
              <input value={draft.name} onChange={e => setDraft(d => ({ ...d, name: e.target.value }))}
                     placeholder="Production CFS DB"
                     className="w-full rounded-md px-3 py-2 text-sm outline-none"
                     style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
            </label>
            <label className="block col-span-2 md:col-span-1">
              <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Tip</div>
              <select value={draft.dbType} onChange={e => onDbTypeChange(e.target.value as SqlDbType)}
                      className="w-full rounded-md px-3 py-2 text-sm outline-none"
                      style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}>
                {DB_TYPE_OPTIONS.map(o => <option key={o.id} value={o.id}>{o.label}</option>)}
              </select>
            </label>
          </div>

          <div className="grid grid-cols-3 gap-3">
            <label className="block col-span-2">
              <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Sunucu *</div>
              <input value={draft.host} onChange={e => setDraft(d => ({ ...d, host: e.target.value }))}
                     placeholder="172.16.0.10 veya db.example.com"
                     className="w-full rounded-md px-3 py-2 text-sm outline-none"
                     style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
            </label>
            <label className="block">
              <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Port</div>
              <input type="number" value={draft.port} onChange={e => setDraft(d => ({ ...d, port: parseInt(e.target.value) || 0 }))}
                     className="w-full rounded-md px-3 py-2 text-sm outline-none"
                     style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
            </label>
          </div>

          <label className="block">
            <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>
              {draft.dbType === 'oracle' ? 'Servis Adı (SERVICE_NAME) *' : 'Veritabanı *'}
            </div>
            <input value={draft.database} onChange={e => setDraft(d => ({ ...d, database: e.target.value }))}
                   placeholder={draft.dbType === 'oracle' ? 'ORCLCDB' : 'CFS'}
                   className="w-full rounded-md px-3 py-2 text-sm outline-none"
                   style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
          </label>

          <div className="grid grid-cols-2 gap-3">
            <label className="block">
              <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Kullanıcı</div>
              <input value={draft.username} onChange={e => setDraft(d => ({ ...d, username: e.target.value }))}
                     placeholder="sa / postgres / system"
                     className="w-full rounded-md px-3 py-2 text-sm outline-none"
                     style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
            </label>
            <label className="block">
              <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>
                Şifre {editingId != null && <span style={{ opacity: 0.6 }}>(boş = değiştirme)</span>}
              </div>
              <input type="password" value={draft.password} onChange={e => setDraft(d => ({ ...d, password: e.target.value }))}
                     className="w-full rounded-md px-3 py-2 text-sm outline-none"
                     style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
            </label>
          </div>

          <div className="grid grid-cols-2 gap-3">
            <label className="block">
              <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>
                Sorgu Zaman Aşımı (saniye) <span style={{ opacity: 0.6 }}>— 5..3600</span>
              </div>
              <input type="number" min={5} max={3600} value={draft.queryTimeoutSec ?? 120}
                     onChange={e => {
                       const n = parseInt(e.target.value, 10)
                       setDraft(d => ({ ...d, queryTimeoutSec: isNaN(n) ? 120 : Math.max(5, Math.min(3600, n)) }))
                     }}
                     className="w-full rounded-md px-3 py-2 text-sm outline-none"
                     style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
            </label>
            <label className="block">
              <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>
                Otomatik Veri Sync (dakika) <span style={{ opacity: 0.6 }}>— 0 = kapalı</span>
              </div>
              <select value={draft.autoSyncIntervalMin ?? 0}
                      onChange={e => setDraft(d => ({ ...d, autoSyncIntervalMin: parseInt(e.target.value, 10) }))}
                      className="w-full rounded-md px-3 py-2 text-sm outline-none cursor-pointer"
                      style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}>
                <option value={0}>Kapalı</option>
                <option value={15}>15 dakika</option>
                <option value={30}>30 dakika</option>
                <option value={60}>Saatlik (60 dk)</option>
                <option value={180}>3 saatte bir</option>
                <option value={360}>6 saatte bir</option>
                <option value={720}>12 saatte bir</option>
                <option value={1440}>Günlük (1440 dk)</option>
                <option value={10080}>Haftalık</option>
              </select>
            </label>
          </div>

          <div className="flex gap-2 pt-1">
            <button onClick={onSave} disabled={busy}
                    className="flex-1 py-2 rounded-lg text-sm font-semibold cursor-pointer disabled:opacity-50"
                    style={{ background: 'var(--accent)', color: '#0b1929' }}>
              {busy ? 'Kaydediliyor…' : (editingId == null ? 'Oluştur' : 'Güncelle')}
            </button>
            <button onClick={onTestDraft} disabled={testing === 'draft' || !draft.host || !draft.database}
                    className="px-4 py-2 rounded-lg text-sm cursor-pointer disabled:opacity-50"
                    style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
              {testing === 'draft' ? 'Test ediliyor…' : '🔌 Test'}
            </button>
            <button onClick={() => { setShowForm(false); setEditingId(null) }}
                    className="px-4 py-2 rounded-lg text-sm cursor-pointer"
                    style={{ background: 'transparent', border: '1px solid var(--border)', color: 'var(--mute)' }}>
              İptal
            </button>
          </div>
        </div>
      )}

      {/* List */}
      <div className="rounded-xl overflow-hidden"
           style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
        <table className="w-full text-sm">
          <thead style={{ background: 'var(--surface-hi)', color: 'var(--mute)', fontSize: 11 }}>
            <tr>
              <th className="px-4 py-2 text-left font-medium">Ad</th>
              <th className="px-4 py-2 text-left font-medium">Tip</th>
              <th className="px-4 py-2 text-left font-medium">Sunucu</th>
              <th className="px-4 py-2 text-left font-medium">Veritabanı</th>
              <th className="px-4 py-2 text-left font-medium">Kullanıcı</th>
              <th className="px-4 py-2 text-right font-medium" title="Sorgu Zaman Aşımı (saniye)">Timeout</th>
              <th className="px-4 py-2 text-right font-medium">İşlemler</th>
            </tr>
          </thead>
          <tbody>
            {loading && <tr><td colSpan={7} className="px-4 py-6 text-center text-xs" style={{ color: 'var(--mute)' }}>Yükleniyor…</td></tr>}
            {!loading && items.length === 0 && <tr><td colSpan={7} className="px-4 py-6 text-center text-xs" style={{ color: 'var(--mute)' }}>Henüz bağlantı yok. + Yeni Bağlantı ile başlayın.</td></tr>}
            {!loading && items.map(c => (
              <tr key={c.id} style={{ borderTop: '1px solid var(--border)' }}>
                <td className="px-4 py-2 font-medium align-top" style={{ color: 'var(--text)' }}>
                  {c.name}
                  {(c.autoSyncIntervalMin ?? 0) > 0 && (
                    <span className="ml-2 px-1.5 py-0.5 rounded text-[10px] font-medium"
                          title={`Otomatik sync: ${c.autoSyncIntervalMin} dakikada bir`}
                          style={{ background: 'rgba(245,158,11,0.15)', color: '#f59e0b', border: '1px solid rgba(245,158,11,0.3)' }}>
                      ⏱ {c.autoSyncIntervalMin}dk
                    </span>
                  )}
                </td>
                <td className="px-4 py-2 text-xs">
                  <span className="px-1.5 py-0.5 rounded font-mono"
                        style={{ background: 'rgba(138,180,248,0.15)', color: 'var(--accent-hi)' }}>
                    {DB_TYPE_OPTIONS.find(o => o.id === c.dbType)?.label ?? c.dbType}
                  </span>
                </td>
                <td className="px-4 py-2 text-xs" style={{ color: 'var(--mute)' }}>{c.host}:{c.port}</td>
                <td className="px-4 py-2 text-xs" style={{ color: 'var(--mute)' }}>{c.database}</td>
                <td className="px-4 py-2 text-xs" style={{ color: 'var(--mute)' }}>{c.username}</td>
                <td className="px-4 py-2 text-xs text-right font-mono" style={{ color: 'var(--mute)' }}>{c.queryTimeoutSec ?? 120}s</td>
                <td className="px-4 py-2 text-right">
                  <div className="inline-flex gap-1">
                    <button onClick={() => onTest(c)} disabled={testing === c.id}
                            className="px-2 py-1 rounded text-xs cursor-pointer disabled:opacity-50"
                            style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>
                      {testing === c.id ? '…' : '🔌'}
                    </button>
                    <button onClick={() => openIngest(c)}
                            className="px-2 py-1 rounded text-xs cursor-pointer"
                            style={{ background: 'rgba(138,180,248,0.15)', color: 'var(--accent-hi)', border: '1px solid rgba(138,180,248,0.3)' }}
                            title="Tüm şemayı RAG'a çıkar (ilk kez)">
                      📜 Şema
                    </button>
                    <button onClick={() => onSyncRun(c)}
                            className="px-2 py-1 rounded text-xs cursor-pointer"
                            style={{ background: 'rgba(52,168,83,0.15)', color: '#34a853', border: '1px solid rgba(52,168,83,0.3)' }}
                            title="Sadece değişen/yeni objeleri güncelle">
                      🔄 Sync
                    </button>
                    <button onClick={() => openData(c)}
                            className="px-2 py-1 rounded text-xs cursor-pointer"
                            style={{ background: 'rgba(245,158,11,0.15)', color: '#f59e0b', border: '1px solid rgba(245,158,11,0.3)' }}
                            title="Tablo verilerini seçip RAG'a aktar">
                      💾 Veri
                    </button>
                    <button onClick={() => openEdit(c)}
                            className="px-2 py-1 rounded text-xs cursor-pointer"
                            style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>
                      ✏️
                    </button>
                    <button onClick={() => onDelete(c)}
                            className="px-2 py-1 rounded text-xs cursor-pointer"
                            style={{ color: '#ea4335' }}>
                      🗑
                    </button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* Schema Ingest Modal */}
      {ingestConn && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4"
             style={{ background: 'rgba(0,0,0,0.6)', backdropFilter: 'blur(4px)' }}
             onClick={e => { if (e.target === e.currentTarget && !ingestRunning) setIngestConn(null) }}>
          <div className="w-full max-w-2xl rounded-2xl overflow-hidden shadow-2xl flex flex-col"
               style={{ background: 'var(--bg)', border: '1px solid var(--border)', maxHeight: '85vh' }}>
            {/* Header */}
            <div className="px-5 py-4 flex items-center gap-3 shrink-0"
                 style={{ borderBottom: '1px solid var(--border)', background: 'var(--surface)' }}>
              <span className="text-2xl">📜</span>
              <div className="flex-1">
                <div className="font-semibold" style={{ color: 'var(--text)' }}>
                  Şema Çıkarımı — {ingestConn.name}
                </div>
                <div className="text-xs" style={{ color: 'var(--mute)' }}>
                  Tüm CREATE script'leri RAG koleksiyonuna eklenir (veri içeriği dahil değildir)
                </div>
              </div>
              <button onClick={() => !ingestRunning && setIngestConn(null)}
                      disabled={ingestRunning}
                      className="w-8 h-8 rounded-full flex items-center justify-center cursor-pointer disabled:opacity-30"
                      style={{ color: 'var(--mute)' }}>×</button>
            </div>

            <div className="flex-1 overflow-y-auto p-5 space-y-4">
              {!ingestPreview && (
                <div className="text-sm text-center py-6" style={{ color: 'var(--mute)' }}>
                  Önizleme yükleniyor…
                </div>
              )}

              {/* Last ingest job status (sync pattern) */}
              {ingestPreview && ingestLastJob && (() => {
                const j = ingestLastJob
                const active = j.status === 'queued' || j.status === 'running'
                const finished = j.status === 'completed' || j.status === 'failed' || j.status === 'cancelled'
                if (active) {
                  return (
                    <div className="rounded-xl p-3 space-y-2"
                         style={{ background: 'rgba(138,180,248,0.08)', border: '1px solid rgba(138,180,248,0.3)' }}>
                      <div className="flex items-center justify-between text-xs">
                        <span style={{ color: 'var(--accent-hi)' }}>⏳ Şu an çalışıyor (Job #{j.id})</span>
                        <span className="font-mono" style={{ color: 'var(--text-2)' }}>
                          {j.progressCur.toLocaleString()} / {j.progressTot.toLocaleString()}
                        </span>
                      </div>
                      {j.message && <div className="text-xs" style={{ color: 'var(--mute)' }}>{j.message}</div>}
                      <button
                        onClick={() => {
                          const conn = ingestConn!
                          setIngestConn(null); setIngestPreview(null); setIngestLastJob(null)
                          setActiveJob({ id: j.id, title: `Şema Çıkarımı — ${conn.name}`, subtitle: `Job #${j.id} izleniyor` })
                        }}
                        className="w-full mt-1 py-1.5 rounded-md text-xs font-medium cursor-pointer"
                        style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
                        🔍 İlerlemeyi izle
                      </button>
                    </div>
                  )
                }
                if (finished) {
                  return (
                    <div className="rounded-xl p-3 space-y-1"
                         style={{ background: j.status === 'failed' ? 'rgba(234,67,53,0.08)' : 'var(--surface-2)',
                                  border: `1px solid ${j.status === 'failed' ? 'rgba(234,67,53,0.3)' : 'var(--border)'}` }}>
                      <div className="text-xs font-semibold" style={{ color: j.status === 'failed' ? '#ea4335' : 'var(--mute)' }}>
                        SON İŞLEM ({j.status === 'completed' ? '✓ başarılı' : j.status === 'failed' ? '✕ başarısız' : j.status})
                      </div>
                      <div className="text-[10px]" style={{ color: 'var(--mute)' }}>
                        {j.completedAt && new Date(j.completedAt).toLocaleString()}
                        {j.startedAt && j.completedAt && (() => {
                          const sec = Math.round((new Date(j.completedAt).getTime() - new Date(j.startedAt).getTime()) / 1000)
                          const m = Math.floor(sec / 60), s = sec % 60
                          return ` · süre: ${m > 0 ? `${m}d ${s}s` : `${s}s`}`
                        })()}
                      </div>
                      {j.status === 'failed' && j.error && (
                        <div className="text-xs font-mono mt-1" style={{ color: 'var(--mute)' }}>{j.error}</div>
                      )}
                    </div>
                  )
                }
                return null
              })()}

              {ingestPreview && (
                <>
                  <div className="grid grid-cols-2 gap-3">
                    {/* Kaynak DB */}
                    <div className="rounded-xl p-4" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
                      <div className="text-xs font-semibold mb-2" style={{ color: 'var(--mute)' }}>KAYNAK DB</div>
                      <div className="text-2xl font-bold" style={{ color: 'var(--accent-hi)' }}>{ingestPreview.total.toLocaleString()}</div>
                      <div className="text-[10px] mt-0.5" style={{ color: 'var(--mute)' }}>obje</div>
                      <div className="flex flex-wrap gap-1 mt-2">
                        {ingestPreview.byType.map(t => (
                          <span key={t.type} className="px-1.5 py-0.5 rounded text-[10px]"
                                style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>
                            {t.type}: <strong style={{ color: 'var(--text)' }}>{t.count}</strong>
                          </span>
                        ))}
                      </div>
                    </div>

                    {/* RAG'daki mevcut */}
                    <div className="rounded-xl p-4"
                         style={{ background: ingestStats && ingestStats.total > 0 ? 'rgba(52,168,83,0.08)' : 'var(--surface-2)',
                                  border: `1px solid ${ingestStats && ingestStats.total > 0 ? 'rgba(52,168,83,0.3)' : 'var(--border)'}` }}>
                      <div className="text-xs font-semibold mb-2" style={{ color: 'var(--mute)' }}>RAG'DAKİ MEVCUT</div>
                      {ingestStats && ingestStats.total > 0 ? (
                        <>
                          <div className="text-2xl font-bold" style={{ color: '#34a853' }}>{ingestStats.total.toLocaleString()}</div>
                          <div className="text-[10px] mt-0.5" style={{ color: 'var(--mute)' }}>
                            obje · {ingestStats.chunks.toLocaleString()} chunk
                          </div>
                          <div className="flex flex-wrap gap-1 mt-2">
                            {ingestStats.byType.map(t => (
                              <span key={t.type} className="px-1.5 py-0.5 rounded text-[10px]"
                                    style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>
                                {t.type}: <strong style={{ color: 'var(--text)' }}>{t.count}</strong>
                              </span>
                            ))}
                          </div>
                          {ingestStats.lastIngestedAt && (
                            <div className="text-[10px] mt-2" style={{ color: 'var(--mute)' }}>
                              Son: {new Date(ingestStats.lastIngestedAt).toLocaleString()}
                            </div>
                          )}
                        </>
                      ) : (
                        <>
                          <div className="text-2xl font-bold" style={{ color: 'var(--mute)' }}>0</div>
                          <div className="text-[10px] mt-0.5" style={{ color: 'var(--mute)' }}>
                            Henüz çıkarım yapılmamış
                          </div>
                        </>
                      )}
                    </div>
                  </div>

                  <label className="block">
                    <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Koleksiyon adı</div>
                    <input value={ingestCollection} onChange={e => setIngestCollection(e.target.value)}
                           className="w-full rounded-md px-3 py-2 text-sm outline-none"
                           style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
                  </label>

                  <div>
                    <div className="text-xs mb-2" style={{ color: 'var(--mute)' }}>Dahil edilecek obje tipleri</div>
                    <div className="flex flex-wrap gap-2">
                      {OBJ_TYPES.map(t => {
                        const checked = ingestTypes.includes(t)
                        return (
                          <label key={t} className="flex items-center gap-2 px-3 py-1.5 rounded-lg text-xs cursor-pointer"
                                 style={{ background: checked ? 'rgba(138,180,248,0.15)' : 'var(--surface-2)',
                                          border: `1px solid ${checked ? 'rgba(138,180,248,0.35)' : 'var(--border)'}`,
                                          color: checked ? 'var(--accent-hi)' : 'var(--text-2)' }}>
                            <input type="checkbox" checked={checked}
                                   onChange={e => setIngestTypes(prev =>
                                     e.target.checked ? [...prev, t] : prev.filter(x => x !== t))}
                                   className="cursor-pointer" />
                            {t}
                          </label>
                        )
                      })}
                    </div>
                  </div>
                  <div className="text-[11px] p-2 rounded" style={{ background: 'rgba(138,180,248,0.08)', color: 'var(--mute)' }}>
                    İşlem arka planda kuyruğa alınır. Bu pencereyi kapatabilirsin — ilerleme ayrı bir pencerede gösterilecek.
                  </div>
                </>
              )}
            </div>

            <div className="px-5 py-4 flex gap-2 shrink-0" style={{ borderTop: '1px solid var(--border)', background: 'var(--surface)' }}>
              {(() => {
                const j = ingestLastJob
                const active = !!j && (j.status === 'queued' || j.status === 'running')
                const hasExisting = ingestStats != null && ingestStats.total > 0
                const label = active
                  ? '⏳ Zaten çalışıyor'
                  : ingestRunning
                    ? 'Kuyruğa ekleniyor…'
                    : hasExisting ? '🚀 Yeni Çıkarım Başlat' : '🚀 Arka Planda Çalıştır'
                return (
                  <button onClick={onIngestRun}
                          disabled={ingestRunning || active || !ingestPreview || !ingestCollection.trim() || ingestTypes.length === 0}
                          title={active ? 'Zaten çalışan bir çıkarım var' : undefined}
                          className="flex-1 py-2 rounded-lg text-sm font-semibold cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed"
                          style={{ background: 'var(--accent)', color: '#0b1929' }}>
                    {label}
                  </button>
                )
              })()}
              <button onClick={() => setIngestConn(null)} disabled={ingestRunning}
                      className="px-4 py-2 rounded-lg text-sm cursor-pointer disabled:opacity-50"
                      style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
                Kapat
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Sync Start Dialog — shows last/current state + Start button */}
      {syncDialog && (() => {
        const j = syncDialog.lastJob
        const active = j && (j.status === 'queued' || j.status === 'running')
        const finished = j && (j.status === 'completed' || j.status === 'failed' || j.status === 'cancelled')
        const r = j?.result as any
        return (
          <div className="fixed inset-0 z-50 flex items-center justify-center p-4"
               style={{ background: 'rgba(0,0,0,0.6)', backdropFilter: 'blur(4px)' }}
               onClick={e => { if (e.target === e.currentTarget) setSyncDialog(null) }}>
            <div className="w-full max-w-xl rounded-2xl overflow-hidden shadow-2xl flex flex-col"
                 style={{ background: 'var(--bg)', border: '1px solid var(--border)', maxHeight: '85vh' }}>
              <div className="px-5 py-4 flex items-center gap-3 shrink-0"
                   style={{ borderBottom: '1px solid var(--border)', background: 'var(--surface)' }}>
                <span className="text-2xl">🔄</span>
                <div className="flex-1">
                  <div className="font-semibold" style={{ color: 'var(--text)' }}>
                    Senkronizasyon — {syncDialog.conn.name}
                  </div>
                  <div className="text-xs" style={{ color: 'var(--mute)' }}>
                    Değişen / yeni / silinen şema objeleri RAG'a aktarılır
                  </div>
                </div>
                <button onClick={() => setSyncDialog(null)}
                        className="w-8 h-8 rounded-full flex items-center justify-center cursor-pointer"
                        style={{ color: 'var(--mute)' }}>×</button>
              </div>

              <div className="flex-1 overflow-y-auto p-5 space-y-3">
                {syncDialog.loading && (
                  <div className="text-sm text-center py-6" style={{ color: 'var(--mute)' }}>
                    Son işlem durumu sorgulanıyor…
                  </div>
                )}

                {!syncDialog.loading && !j && (
                  <div className="rounded-xl p-4 text-sm text-center"
                       style={{ background: 'var(--surface-2)', border: '1px solid var(--border)', color: 'var(--mute)' }}>
                    Bu bağlantı için henüz senkron çalıştırılmamış.
                  </div>
                )}

                {!syncDialog.loading && active && j && (
                  <div className="rounded-xl p-4 space-y-2"
                       style={{ background: 'rgba(138,180,248,0.08)', border: '1px solid rgba(138,180,248,0.3)' }}>
                    <div className="flex items-center justify-between text-xs">
                      <span style={{ color: 'var(--accent-hi)' }}>
                        ⏳ Şu an çalışıyor (Job #{j.id})
                      </span>
                      <span className="font-mono" style={{ color: 'var(--text-2)' }}>
                        {j.progressCur.toLocaleString()} / {j.progressTot.toLocaleString()}
                      </span>
                    </div>
                    {j.message && <div className="text-xs" style={{ color: 'var(--mute)' }}>{j.message}</div>}
                    <button
                      onClick={() => { setActiveJob({ id: j.id,
                        title: `Senkronizasyon — ${syncDialog.conn.name}`,
                        subtitle: `Job #${j.id} izleniyor` }); setSyncDialog(null) }}
                      className="w-full mt-1 py-1.5 rounded-md text-xs font-medium cursor-pointer"
                      style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
                      🔍 İlerlemeyi izle
                    </button>
                  </div>
                )}

                {!syncDialog.loading && finished && j && (
                  <div className="rounded-xl p-4 space-y-2"
                       style={{ background: j.status === 'failed' ? 'rgba(234,67,53,0.08)' : 'var(--surface-2)',
                                border: `1px solid ${j.status === 'failed' ? 'rgba(234,67,53,0.3)' : 'var(--border)'}` }}>
                    <div className="text-xs font-semibold" style={{ color: j.status === 'failed' ? '#ea4335' : 'var(--mute)' }}>
                      SON İŞLEM ({j.status === 'completed' ? '✓ başarılı' : j.status === 'failed' ? '✕ başarısız' : j.status})
                    </div>
                    <div className="text-[10px]" style={{ color: 'var(--mute)' }}>
                      {j.completedAt && new Date(j.completedAt).toLocaleString()}
                      {j.startedAt && j.completedAt && (() => {
                        const sec = Math.round((new Date(j.completedAt).getTime() - new Date(j.startedAt).getTime()) / 1000)
                        const m = Math.floor(sec / 60), s = sec % 60
                        return ` · süre: ${m > 0 ? `${m}d ${s}s` : `${s}s`}`
                      })()}
                    </div>
                    {j.status === 'completed' && r && (
                      <div className="grid grid-cols-4 gap-2 mt-2 text-center text-xs">
                        <div><strong style={{ color: '#34a853' }}>{Array.isArray(r.added) ? r.added.length : 0}</strong>
                          <div className="text-[9px]" style={{ color: 'var(--mute)' }}>yeni</div></div>
                        <div><strong style={{ color: '#f59e0b' }}>{Array.isArray(r.updated) ? r.updated.length : 0}</strong>
                          <div className="text-[9px]" style={{ color: 'var(--mute)' }}>değişen</div></div>
                        <div><strong style={{ color: 'var(--text)' }}>{r.unchanged ?? 0}</strong>
                          <div className="text-[9px]" style={{ color: 'var(--mute)' }}>aynı</div></div>
                        <div><strong style={{ color: '#ea4335' }}>{Array.isArray(r.removed) ? r.removed.length : 0}</strong>
                          <div className="text-[9px]" style={{ color: 'var(--mute)' }}>silinen</div></div>
                      </div>
                    )}
                    {j.status === 'failed' && (
                      <div className="text-xs font-mono mt-1" style={{ color: 'var(--mute)' }}>{j.error}</div>
                    )}
                  </div>
                )}
              </div>

              <div className="px-5 py-4 flex gap-2 shrink-0" style={{ borderTop: '1px solid var(--border)', background: 'var(--surface)' }}>
                <button onClick={onSyncStart}
                        disabled={syncDialog.loading || !!active}
                        title={active ? 'Zaten çalışan bir senkron var' : 'Yeni senkronizasyon başlat'}
                        className="flex-1 py-2 rounded-lg text-sm font-semibold cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed"
                        style={{ background: 'var(--accent)', color: '#0b1929' }}>
                  {active ? '⏳ Zaten çalışıyor' : '🚀 Yeni Senkron Başlat'}
                </button>
                <button onClick={() => setSyncDialog(null)}
                        className="px-4 py-2 rounded-lg text-sm cursor-pointer"
                        style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
                  Kapat
                </button>
              </div>
            </div>
          </div>
        )
      })()}

      {/* Background job progress modal (shared) */}
      {activeJob && (
        <JobProgressModal
          jobId={activeJob.id}
          title={activeJob.title}
          subtitle={activeJob.subtitle}
          onClose={() => setActiveJob(null)}
        />
      )}

      {/* New Data Sync Dialog (table configs + groups + delta sync) */}
      {dataConn && (
        <SqlDataDialog
          conn={dataConn}
          onClose={() => setDataConn(null)}
          onJobStarted={(jobId, title, subtitle) => setActiveJob({ id: jobId, title, subtitle })}
        />
      )}
    </section>
  )
}

// =============================================================================
// Tab — Activity Log
// =============================================================================

const ACTION_LABELS: Record<string, { label: string; color: string }> = {
  'document.upload': { label: '📤 Doküman Yüklendi',  color: '#34a853' },
  'document.delete': { label: '🗑 Doküman Silindi',   color: '#ea4335' },
  'skill.upload':    { label: '📝 Skill Yüklendi',    color: '#4285f4' },
  'skill.delete':    { label: '🗑 Skill Silindi',     color: '#ea4335' },
  'template.create': { label: '✨ Şablon Oluşturuldu', color: '#34a853' },
  'template.update': { label: '✏️ Şablon Güncellendi', color: '#f59e0b' },
  'template.delete': { label: '🗑 Şablon Silindi',   color: '#ea4335' },
}

function ActivityTab() {
  const [data,    setData]    = useState<ActivityPage | null>(null)
  const [page,    setPage]    = useState(1)
  const [filter,  setFilter]  = useState('')
  const [loading, setLoading] = useState(true)
  const [error,   setError]   = useState<string | null>(null)
  const pageSize = 50

  const load = async (p = page, f = filter) => {
    setLoading(true); setError(null)
    try { setData(await getActivityLog(p, pageSize, f || undefined)) }
    catch (e: any) { setError(e.message) }
    finally { setLoading(false) }
  }

  useEffect(() => { load() }, [])

  const onFilter = (f: string) => {
    setFilter(f); setPage(1); load(1, f)
  }

  const totalPages = data ? Math.max(1, Math.ceil(data.total / pageSize)) : 1

  return (
    <section className="space-y-5">
      <div className="flex items-end gap-4 flex-wrap">
        <div>
          <h2 className="text-lg font-medium">Aktivite Günlüğü</h2>
          <p className="text-xs mt-1" style={{ color: 'var(--mute)' }}>
            Doküman, skill ve şablon işlemleri — kullanıcı + tarih/saat
          </p>
        </div>
        <div className="flex-1" />
        {/* Filter */}
        <select value={filter} onChange={e => onFilter(e.target.value)}
                className="px-3 py-2 rounded-md text-sm outline-none"
                style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}>
          <option value="">Tüm işlemler</option>
          <option value="document.upload">Doküman Yükleme</option>
          <option value="document.delete">Doküman Silme</option>
          <option value="skill.upload">Skill Yükleme</option>
          <option value="skill.delete">Skill Silme</option>
          <option value="template.create">Şablon Oluşturma</option>
          <option value="template.update">Şablon Güncelleme</option>
          <option value="template.delete">Şablon Silme</option>
        </select>
        <button onClick={() => load()} className="px-3 py-2 rounded-md text-sm cursor-pointer"
                style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text)' }}>
          ↺ Yenile
        </button>
      </div>

      {error && <div className="rounded-md px-3 py-2 text-xs" style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}>{error}</div>}

      <div className="rounded-xl overflow-hidden" style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
        <table className="w-full text-sm">
          <thead style={{ background: 'var(--surface-hi)', color: 'var(--mute)', fontSize: 11 }}>
            <tr>
              <th className="px-4 py-2 text-left font-medium">Tarih / Saat</th>
              <th className="px-4 py-2 text-left font-medium">Kullanıcı</th>
              <th className="px-4 py-2 text-left font-medium">İşlem</th>
              <th className="px-4 py-2 text-left font-medium">Hedef</th>
              <th className="px-4 py-2 text-left font-medium">Detay</th>
            </tr>
          </thead>
          <tbody>
            {loading && <tr><td colSpan={5} className="px-4 py-6 text-center text-xs" style={{ color: 'var(--mute)' }}>Yükleniyor…</td></tr>}
            {!loading && data?.items.length === 0 && <tr><td colSpan={5} className="px-4 py-6 text-center text-xs" style={{ color: 'var(--mute)' }}>Kayıt yok.</td></tr>}
            {!loading && data?.items.map(entry => {
              const al = ACTION_LABELS[entry.action]
              return (
                <tr key={entry.id} style={{ borderTop: '1px solid var(--border)' }}>
                  <td className="px-4 py-2 text-xs whitespace-nowrap" style={{ color: 'var(--mute)' }}>
                    {formatDate(entry.createdAt)}
                  </td>
                  <td className="px-4 py-2 text-xs font-medium" style={{ color: 'var(--text)' }}>
                    {entry.username}
                  </td>
                  <td className="px-4 py-2 text-xs">
                    <span style={{ color: al?.color ?? 'var(--text-2)' }}>
                      {al?.label ?? entry.action}
                    </span>
                  </td>
                  <td className="px-4 py-2 text-xs max-w-[200px] truncate" style={{ color: 'var(--text-2)' }}
                      title={entry.target}>
                    {entry.target}
                  </td>
                  <td className="px-4 py-2 text-xs" style={{ color: 'var(--mute)' }}>
                    {entry.details}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>

      {data && data.total > pageSize && (
        <div className="flex items-center justify-between text-xs" style={{ color: 'var(--mute)' }}>
          <div>Toplam {data.total} kayıt</div>
          <div className="flex gap-1">
            <button disabled={page <= 1} onClick={() => { setPage(p => p-1); load(page-1) }}
                    className="px-3 py-1.5 rounded-md cursor-pointer disabled:opacity-40"
                    style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>Önceki</button>
            <span className="px-3 py-1.5">Sayfa {page} / {totalPages}</span>
            <button disabled={page >= totalPages} onClick={() => { setPage(p => p+1); load(page+1) }}
                    className="px-3 py-1.5 rounded-md cursor-pointer disabled:opacity-40"
                    style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>Sonraki</button>
          </div>
        </div>
      )}
    </section>
  )
}

// =============================================================================
// Tab — Security (OWASP event log)
// =============================================================================

const SEVERITY_COLORS: Record<string, string> = {
  Debug:    '#9aa0a6',
  Info:     'var(--accent-hi)',
  Warn:     '#f9ab00',
  Error:    '#ea4335',
  Critical: '#c2185b',
}

const CATEGORY_LABELS: Record<string, string> = {
  Auth:     'Kimlik',
  Authz:    'Yetki',
  Session:  'Oturum',
  Input:    'Girdi',
  Config:   'Yapılandırma',
  Data:     'Veri',
  Security: 'Güvenlik',
  System:   'Sistem',
}

function SecurityTab() {
  const [page, setPage] = useState<EventLogPage | null>(null)
  const [summary, setSummary] = useState<EventLogSummary | null>(null)
  const [filters, setFilters] = useState<EventLogFilters>({ page: 1, pageSize: 50 })
  const [loading, setLoading] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<number | null>(null)

  const load = useCallback(async () => {
    setLoading(true); setErr(null)
    try {
      const [p, s] = await Promise.all([
        getEventLog(filters),
        getEventLogSummary().catch(() => null),
      ])
      setPage(p)
      if (s) setSummary(s)
    } catch (e: any) {
      setErr(e.message)
    } finally {
      setLoading(false)
    }
  }, [filters])

  useEffect(() => { load() }, [load])

  const updateFilter = (k: keyof EventLogFilters, v: string | undefined) => {
    setFilters(prev => ({ ...prev, [k]: v || undefined, page: 1 }))
  }
  const setPageNum = (n: number) => setFilters(prev => ({ ...prev, page: n }))

  const totalPages = page ? Math.max(1, Math.ceil(page.total / page.pageSize)) : 1

  return (
    <div className="space-y-4">
      <div className="flex items-start justify-between gap-3 flex-wrap">
        <div>
          <h2 className="text-base font-semibold" style={{ color: 'var(--text)' }}>
            🛡 Güvenlik Olayları
          </h2>
          <p className="text-xs mt-0.5" style={{ color: 'var(--mute)' }}>
            OWASP Logging Cheat Sheet uyumlu denetim kayıtları — kim · ne · ne zaman · nereden · neden
          </p>
        </div>
        <button onClick={load} disabled={loading}
                className="px-3 py-1.5 rounded-md text-xs cursor-pointer disabled:opacity-50"
                style={{ background: 'var(--surface-hi)', color: 'var(--text-2)', border: '1px solid var(--border)' }}>
          {loading ? '…' : '🔄 Yenile'}
        </button>
      </div>

      {/* 24h summary */}
      {summary && summary.rows.length > 0 && (
        <div className="rounded-xl p-3 text-xs"
             style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
          <div className="mb-2" style={{ color: 'var(--mute)' }}>
            Son 24 saat — kategori × severity dağılımı
          </div>
          <div className="flex flex-wrap gap-2">
            {summary.rows.map((r, i) => (
              <button key={i}
                      onClick={() => setFilters({ ...filters, category: r.category, severity: r.severity, page: 1 })}
                      className="px-2 py-1 rounded text-[11px] cursor-pointer"
                      style={{
                        background: 'var(--surface-2)',
                        border: `1px solid ${SEVERITY_COLORS[r.severity] ?? 'var(--border)'}`,
                        color: SEVERITY_COLORS[r.severity] ?? 'var(--text-2)',
                      }}
                      title="Bu filtre ile listele">
                <strong>{CATEGORY_LABELS[r.category] ?? r.category}</strong> · {r.severity} · <strong style={{ color: 'var(--text)' }}>{r.count}</strong>
              </button>
            ))}
          </div>
        </div>
      )}

      {/* Filters */}
      <div className="rounded-xl p-3 space-y-2 text-xs"
           style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
        <div className="grid grid-cols-2 md:grid-cols-4 gap-2">
          <select value={filters.category ?? ''} onChange={e => updateFilter('category', e.target.value)}
                  className="rounded px-2 py-1 outline-none cursor-pointer"
                  style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}>
            <option value="">Tüm kategoriler</option>
            {Object.entries(CATEGORY_LABELS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
          <select value={filters.severity ?? ''} onChange={e => updateFilter('severity', e.target.value)}
                  className="rounded px-2 py-1 outline-none cursor-pointer"
                  style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}>
            <option value="">Tüm önem dereceleri</option>
            {Object.keys(SEVERITY_COLORS).map(s => <option key={s} value={s}>{s}</option>)}
          </select>
          <select value={filters.result ?? ''} onChange={e => updateFilter('result', e.target.value)}
                  className="rounded px-2 py-1 outline-none cursor-pointer"
                  style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}>
            <option value="">Tüm sonuçlar</option>
            <option value="Success">Success</option>
            <option value="Failure">Failure</option>
            <option value="Denied">Denied</option>
            <option value="Error">Error</option>
          </select>
          <input placeholder="Olay tipi (örn: auth.login.fail)"
                 value={filters.eventType ?? ''} onChange={e => updateFilter('eventType', e.target.value)}
                 className="rounded px-2 py-1 outline-none"
                 style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
          <input placeholder="Kullanıcı"
                 value={filters.username ?? ''} onChange={e => updateFilter('username', e.target.value)}
                 className="rounded px-2 py-1 outline-none"
                 style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
          <input placeholder="Kaynak IP"
                 value={filters.sourceIp ?? ''} onChange={e => updateFilter('sourceIp', e.target.value)}
                 className="rounded px-2 py-1 outline-none"
                 style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
          <input placeholder="Serbest arama (action/resource/reason)"
                 value={filters.q ?? ''} onChange={e => updateFilter('q', e.target.value)}
                 className="md:col-span-2 rounded px-2 py-1 outline-none"
                 style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
        </div>
        {(filters.category || filters.severity || filters.eventType || filters.username || filters.sourceIp || filters.result || filters.q) && (
          <div>
            <button onClick={() => setFilters({ page: 1, pageSize: filters.pageSize })}
                    className="text-[11px] cursor-pointer hover:underline"
                    style={{ color: 'var(--mute)' }}>
              Filtreleri temizle
            </button>
          </div>
        )}
      </div>

      {err && (
        <div className="rounded px-3 py-2 text-xs"
             style={{ background: 'rgba(234,67,53,0.12)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}>
          {err}
        </div>
      )}

      {/* Table */}
      <div className="rounded-xl overflow-hidden"
           style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
        <table className="w-full text-xs">
          <thead style={{ background: 'var(--surface-hi)', color: 'var(--mute)' }}>
            <tr>
              <th className="px-2 py-2 text-left font-medium">Zaman</th>
              <th className="px-2 py-2 text-left font-medium">Kat.</th>
              <th className="px-2 py-2 text-left font-medium">Önem</th>
              <th className="px-2 py-2 text-left font-medium">Olay</th>
              <th className="px-2 py-2 text-left font-medium">Sonuç</th>
              <th className="px-2 py-2 text-left font-medium">Kullanıcı</th>
              <th className="px-2 py-2 text-left font-medium">IP</th>
              <th className="px-2 py-2 text-left font-medium">Kaynak</th>
            </tr>
          </thead>
          <tbody>
            {loading && (!page || page.items.length === 0) && (
              <tr><td colSpan={8} className="px-3 py-6 text-center" style={{ color: 'var(--mute)' }}>Yükleniyor…</td></tr>
            )}
            {!loading && page && page.items.length === 0 && (
              <tr><td colSpan={8} className="px-3 py-6 text-center" style={{ color: 'var(--mute)' }}>Bu filtre için kayıt yok.</td></tr>
            )}
            {page?.items.map(e => {
              const isExp = expanded === e.id
              const sevColor = SEVERITY_COLORS[e.severity] ?? 'var(--text-2)'
              return (
                <Fragment key={e.id}>
                  <tr onClick={() => setExpanded(isExp ? null : e.id)}
                      className="cursor-pointer"
                      style={{ borderTop: '1px solid var(--border)' }}>
                    <td className="px-2 py-1.5 font-mono text-[10px]" style={{ color: 'var(--mute)' }}>
                      {new Date(e.ts).toLocaleString()}
                    </td>
                    <td className="px-2 py-1.5">
                      <span className="px-1.5 py-0.5 rounded text-[9px]"
                            style={{ background: 'var(--surface-2)', color: 'var(--text-2)' }}>
                        {CATEGORY_LABELS[e.category] ?? e.category}
                      </span>
                    </td>
                    <td className="px-2 py-1.5 font-medium" style={{ color: sevColor }}>{e.severity}</td>
                    <td className="px-2 py-1.5 font-mono" style={{ color: 'var(--text)' }}>{e.eventType}</td>
                    <td className="px-2 py-1.5 font-medium"
                        style={{ color: e.result === 'Success' ? '#34a853' : e.result === 'Denied' ? '#f9ab00' : '#ea4335' }}>
                      {e.result}
                    </td>
                    <td className="px-2 py-1.5" style={{ color: 'var(--text-2)' }}>{e.username ?? '—'}</td>
                    <td className="px-2 py-1.5 font-mono" style={{ color: 'var(--mute)' }}>{e.sourceIp ?? '—'}</td>
                    <td className="px-2 py-1.5 truncate max-w-xs" style={{ color: 'var(--mute)' }} title={e.resource ?? ''}>
                      {e.resource ?? e.endpoint ?? '—'}
                    </td>
                  </tr>
                  {isExp && (
                    <tr style={{ background: 'var(--surface-2)' }}>
                      <td colSpan={8} className="px-3 py-3 text-[11px] space-y-1" style={{ color: 'var(--text-2)' }}>
                        <div><strong>Action:</strong> {e.action ?? '—'} · <strong>Endpoint:</strong> {e.endpoint ?? '—'}</div>
                        <div><strong>Reason:</strong> {e.reason ?? '—'}</div>
                        <div><strong>Request ID:</strong> <code style={{ color: 'var(--accent-hi)' }}>{e.requestId ?? '—'}</code> · <strong>Session:</strong> {e.sessionId ?? '—'}</div>
                        <div><strong>User-Agent:</strong> <span style={{ color: 'var(--mute)' }}>{e.userAgent ?? '—'}</span></div>
                        {e.details && (
                          <pre className="mt-1 p-2 rounded font-mono text-[10px] overflow-x-auto"
                               style={{ background: 'var(--bg)', color: 'var(--text)' }}>{e.details}</pre>
                        )}
                      </td>
                    </tr>
                  )}
                </Fragment>
              )
            })}
          </tbody>
        </table>
      </div>

      {/* Pagination */}
      {page && page.total > page.pageSize && (
        <div className="flex items-center justify-between text-xs" style={{ color: 'var(--mute)' }}>
          <div>Toplam: <span style={{ color: 'var(--text-2)' }}>{page.total.toLocaleString()}</span></div>
          <div className="flex items-center gap-2">
            <button disabled={(filters.page ?? 1) <= 1}
                    onClick={() => setPageNum(Math.max(1, (filters.page ?? 1) - 1))}
                    className="px-2 py-1 rounded cursor-pointer disabled:opacity-40"
                    style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>‹</button>
            <span>Sayfa {filters.page ?? 1} / {totalPages}</span>
            <button disabled={(filters.page ?? 1) >= totalPages}
                    onClick={() => setPageNum(Math.min(totalPages, (filters.page ?? 1) + 1))}
                    className="px-2 py-1 rounded cursor-pointer disabled:opacity-40"
                    style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>›</button>
          </div>
        </div>
      )}
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
