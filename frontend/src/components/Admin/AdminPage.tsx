import { useEffect, useState, useRef, useCallback } from 'react'
import {
  uploadFiles, listDocuments, listCollections,
  deleteDocument, listSkills, getSkill, uploadSkills, deleteSkill,
  getUsageUsers, getUsageModels, getUsageLogs,
} from '../../api/admin'
import type {
  UploadResult, DocumentsPage, CollectionRow, SkillRow,
  UserSpend, ModelSpend, SpendLog,
} from '../../api/admin'
import SetLogo from '../SetLogo'

type Tab = 'upload' | 'documents' | 'skills' | 'usage'

// =============================================================================
// AdminPage — RAG admin panel (3 tabs: upload / documents / skills)
// =============================================================================

export default function AdminPage() {
  const [tab, setTab] = useState<Tab>('upload')

  useEffect(() => {
    // ensure dark theme matches the rest of the app
    const stored = localStorage.getItem('setllm-theme')
    document.documentElement.setAttribute('data-theme', stored === 'light' ? 'light' : 'dark')
  }, [])

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
          {(['upload', 'documents', 'skills', 'usage'] as Tab[]).map(t => (
            <button
              key={t}
              onClick={() => setTab(t)}
              className="px-3 py-1.5 text-sm rounded-full transition cursor-pointer"
              style={{
                background: tab === t ? 'var(--surface-hi)' : 'transparent',
                color:      tab === t ? 'var(--accent-hi)' : 'var(--text-2)',
                border:     tab === t ? '1px solid var(--border)' : '1px solid transparent',
              }}
            >
              {t === 'upload' ? 'Upload' : t === 'documents' ? 'Documents' : t === 'skills' ? 'Skills' : 'Kullanım'}
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
          {tab === 'usage'     && <UsageTab />}
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
  const fileRef = useRef<HTMLInputElement>(null)

  const reload = () => listSkills().then(setSkills).catch(e => setError(e.message ?? String(e)))

  useEffect(() => { reload() }, [])

  const openSkill = async (id: string) => {
    setSelected(id)
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
               style={{ color: 'var(--text-2)' }}>
            {content || (selected ? '' : 'No skill selected.')}
          </pre>
        </div>
      </div>
    </section>
  )
}

// =============================================================================
// Tab 4 — Usage
// =============================================================================

function UsageTab() {
  const [users,  setUsers]  = useState<UserSpend[]>([])
  const [models, setModels] = useState<ModelSpend[]>([])
  const [logs,   setLogs]   = useState<SpendLog[]>([])
  const [loading, setLoading] = useState(true)
  const [error,   setError]   = useState<string | null>(null)

  const load = async () => {
    setLoading(true); setError(null)
    try {
      const [u, m, l] = await Promise.all([
        getUsageUsers(), getUsageModels(), getUsageLogs(50)
      ])
      setUsers(u); setModels(m); setLogs(l)
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
