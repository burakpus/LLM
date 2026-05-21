import { useEffect, useState, useRef, useCallback } from 'react'
import {
  uploadFiles, listDocuments, listCollections,
  deleteDocument, listSkills, getSkill,
} from '../../api/admin'
import type {
  UploadResult, DocumentsPage, CollectionRow, SkillRow,
} from '../../api/admin'
import SetLogo from '../SetLogo'

type Tab = 'upload' | 'documents' | 'skills'

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
          {(['upload', 'documents', 'skills'] as Tab[]).map(t => (
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
              {t === 'upload' ? 'Upload' : t === 'documents' ? 'Documents' : 'Skills'}
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
  const [skills, setSkills]   = useState<SkillRow[]>([])
  const [selected, setSelected] = useState<string | null>(null)
  const [content, setContent] = useState<string>('')
  const [loading, setLoading] = useState(false)
  const [error, setError]     = useState<string | null>(null)

  useEffect(() => {
    listSkills().then(setSkills).catch(e => setError(e.message ?? String(e)))
  }, [])

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

  return (
    <section className="space-y-5">
      <div>
        <h2 className="text-lg font-medium">Skills</h2>
        <p className="text-xs mt-1" style={{ color: 'var(--mute)' }}>
          System prompts loaded from the Skills directory. Click to view full content.
        </p>
      </div>

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
                  <button
                    onClick={() => openSkill(s.id)}
                    className="w-full text-left px-3 py-2.5 text-sm cursor-pointer transition flex items-center justify-between"
                    style={{
                      background: active ? 'rgba(138,180,248,0.15)' : 'transparent',
                      color:      active ? 'var(--accent-hi)' : 'var(--text)',
                      borderBottom: '1px solid var(--border)',
                    }}
                    onMouseEnter={e => { if (!active) (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)' }}
                    onMouseLeave={e => { if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent' }}
                  >
                    <span className="truncate">{s.id}</span>
                    <span className="text-[10px] shrink-0 ml-2" style={{ color: 'var(--mute)' }}>
                      {formatBytes(s.size)}
                    </span>
                  </button>
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
