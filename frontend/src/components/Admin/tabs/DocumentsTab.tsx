import { useCallback, useEffect, useState } from 'react'
import {
  deleteDocument, listCollections, listDocuments, updateCollectionSettings,
  listRagSynonyms, upsertRagSynonym, deleteRagSynonym, testRagSynonyms,
} from '../../../api/admin'
import type { CollectionRow, CollectionPriority, DocumentsPage, RagSynonym } from '../../../api/admin'
import { DEFAULT_PAGE_SIZE, DebouncedSearchInput, PageSizeSelector, formatDate } from './_shared'

const PRIORITY_COLOR: Record<CollectionPriority, string> = {
  high:   '#34a853',
  normal: 'var(--text-2)',
  low:    '#f9ab00',
  hidden: '#9aa0a6',
}
const PRIORITY_LABEL: Record<CollectionPriority, string> = {
  high:   '↑ High',
  normal: '— Normal',
  low:    '↓ Low',
  hidden: '✕ Hidden',
}

export default function DocumentsTab() {
  const [collections, setCollections] = useState<CollectionRow[]>([])
  const [filter, setFilter]           = useState<string>('')
  const [search, setSearch]           = useState<string>('')
  const [page, setPage]               = useState(1)
  const [pageSize, setPageSize]       = useState<number>(DEFAULT_PAGE_SIZE)
  const [data, setData]               = useState<DocumentsPage | null>(null)
  const [loading, setLoading]         = useState(false)
  const [error, setError]             = useState<string | null>(null)
  const [confirm, setConfirm]         = useState<{ collection: string; source: string } | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [cols, page1] = await Promise.all([
        listCollections(),
        listDocuments(filter || null, page, pageSize, search || undefined),
      ])
      setCollections(cols)
      setData(page1)
    } catch (e: any) {
      setError(e.message ?? String(e))
    } finally {
      setLoading(false)
    }
  }, [filter, page, pageSize, search])

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
          <div className="text-xs mb-1.5" style={{ color: 'var(--mute)' }}>Satır filtresi</div>
          <DebouncedSearchInput
            initial={search}
            onCommit={v => { setSearch(v); setPage(1) }}
            placeholder="source / title içinde ara…"
          />
        </label>

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

        <PageSizeSelector value={pageSize} onChange={n => { setPageSize(n); setPage(1) }} />

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

      {/* Collection settings — priority / data type / description per collection */}
      {collections.length > 0 && (
        <CollectionSettingsPanel
          collections={collections}
          onUpdated={updated => setCollections(cs => cs.map(c => c.collection === updated.collection ? { ...c, ...updated } : c))}
        />
      )}

      {/* RAG synonyms — Türkçe ↔ İngilizce eşanlamlı yönetimi */}
      <RagSynonymsPanel />

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

// ─── Collection Settings Panel ────────────────────────────────────────────────
// Per-collection priority + data-type label. Priority affects RAG retrieval
// ranking: high=2x, normal=1x, low=0.5x, hidden=excluded.

function CollectionSettingsPanel({
  collections, onUpdated,
}: {
  collections: CollectionRow[]
  onUpdated:   (c: CollectionRow) => void
}) {
  const [open, setOpen]       = useState(false)
  const [saving, setSaving]   = useState<string | null>(null)
  const [error, setError]     = useState<string | null>(null)

  const save = async (
    collection: string,
    patch: { priority?: CollectionPriority; dataType?: string; description?: string },
  ) => {
    setSaving(collection); setError(null)
    try {
      const updated = await updateCollectionSettings(collection, patch)
      onUpdated(updated)
    } catch (e: any) {
      setError(`${collection}: ${e.message ?? String(e)}`)
    } finally {
      setSaving(null)
    }
  }

  return (
    <div className="rounded-xl"
         style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
      <button
        onClick={() => setOpen(o => !o)}
        className="w-full px-4 py-2.5 flex items-center justify-between text-sm cursor-pointer"
        style={{ color: 'var(--text)' }}
      >
        <span className="font-medium">
          Collection Ayarları
          <span className="ml-2 text-xs" style={{ color: 'var(--mute)' }}>
            ({collections.length} collection · RAG retrieval önceliği)
          </span>
        </span>
        <span style={{ color: 'var(--mute)' }}>{open ? '▾' : '▸'}</span>
      </button>

      {open && (
        <div className="px-4 pb-4 space-y-2">
          {error && (
            <div className="rounded-md px-3 py-2 text-xs"
                 style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335' }}>
              {error}
            </div>
          )}
          <table className="w-full text-xs">
            <thead style={{ color: 'var(--mute)' }}>
              <tr>
                <th className="text-left px-2 py-1.5 font-medium">Collection</th>
                <th className="text-right px-2 py-1.5 font-medium">Sources</th>
                <th className="text-left px-2 py-1.5 font-medium" style={{ width: 130 }}>Öncelik</th>
                <th className="text-left px-2 py-1.5 font-medium" style={{ width: 180 }}>Veri Tipi</th>
                <th className="text-left px-2 py-1.5 font-medium">Açıklama</th>
              </tr>
            </thead>
            <tbody>
              {collections.map(c => {
                const pri = c.priority ?? 'normal'
                return (
                  <tr key={c.collection} style={{ borderTop: '1px solid var(--border)' }}>
                    <td className="px-2 py-2 font-medium" style={{ color: 'var(--text)' }}>
                      {c.collection}
                    </td>
                    <td className="px-2 py-2 text-right" style={{ color: 'var(--mute)' }}>
                      {c.sources.toLocaleString()}
                    </td>
                    <td className="px-2 py-2">
                      <select
                        value={pri}
                        disabled={saving === c.collection}
                        onChange={e => save(c.collection, { priority: e.target.value as CollectionPriority, dataType: c.dataType, description: c.description })}
                        className="px-2 py-1 rounded text-xs outline-none w-full"
                        style={{
                          background: 'var(--input-bg)',
                          border:     '1px solid var(--border)',
                          color:      PRIORITY_COLOR[pri],
                        }}
                      >
                        <option value="high">{PRIORITY_LABEL.high}</option>
                        <option value="normal">{PRIORITY_LABEL.normal}</option>
                        <option value="low">{PRIORITY_LABEL.low}</option>
                        <option value="hidden">{PRIORITY_LABEL.hidden}</option>
                      </select>
                    </td>
                    <td className="px-2 py-2">
                      <input
                        defaultValue={c.dataType ?? ''}
                        placeholder="schema, data-dictionary…"
                        disabled={saving === c.collection}
                        onBlur={e => {
                          if (e.target.value !== (c.dataType ?? ''))
                            save(c.collection, { priority: pri, dataType: e.target.value, description: c.description })
                        }}
                        className="px-2 py-1 rounded text-xs outline-none w-full"
                        style={{
                          background: 'var(--input-bg)',
                          border:     '1px solid var(--border)',
                          color:      'var(--text)',
                        }}
                      />
                    </td>
                    <td className="px-2 py-2">
                      <input
                        defaultValue={c.description ?? ''}
                        placeholder="kısa açıklama (opsiyonel)"
                        disabled={saving === c.collection}
                        onBlur={e => {
                          if (e.target.value !== (c.description ?? ''))
                            save(c.collection, { priority: pri, dataType: c.dataType, description: e.target.value })
                        }}
                        className="px-2 py-1 rounded text-xs outline-none w-full"
                        style={{
                          background: 'var(--input-bg)',
                          border:     '1px solid var(--border)',
                          color:      'var(--text)',
                        }}
                      />
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
          <p className="text-[10px] mt-2" style={{ color: 'var(--mute)' }}>
            <b>High</b> = score × 2.0 (öne çıkar) · <b>Normal</b> = × 1.0 · <b>Low</b> = × 0.5 (geri plana at) · <b>Hidden</b> = retrieval'da hiç dönmez. Değişiklik otomatik kaydedilir.
          </p>
        </div>
      )}
    </div>
  )
}

// =============================================================================
// RAG Synonyms — Türkçe ↔ İngilizce eşanlamlı yönetimi
// =============================================================================

function RagSynonymsPanel() {
  const [rows,    setRows]    = useState<RagSynonym[]>([])
  const [loading, setLoading] = useState(false)
  const [error,   setError]   = useState<string | null>(null)
  const [open,    setOpen]    = useState(false)
  const [newTerm, setNewTerm] = useState('')
  const [newSyns, setNewSyns] = useState('')
  const [confirm, setConfirm] = useState<string | null>(null)
  const [testQ,   setTestQ]   = useState('')
  const [testOut, setTestOut] = useState<{ expanded: string; added: string[] } | null>(null)
  const [editingTerm, setEditingTerm] = useState<string | null>(null)
  const [editingSyns, setEditingSyns] = useState<string>('')

  const refresh = useCallback(async () => {
    setLoading(true); setError(null)
    try { setRows(await listRagSynonyms()) }
    catch (e: any) { setError(e.message) }
    finally { setLoading(false) }
  }, [])

  useEffect(() => { if (open) refresh() }, [open, refresh])

  const onAdd = async () => {
    const term = newTerm.trim().toLowerCase()
    if (!term) return
    const syns = newSyns.split(',').map(s => s.trim()).filter(s => s.length > 0)
    if (syns.length === 0) { setError('En az bir eşanlamlı gerekli'); return }
    try {
      await upsertRagSynonym(term, syns)
      setNewTerm(''); setNewSyns('')
      await refresh()
    } catch (e: any) { setError(e.message) }
  }
  const onSaveEdit = async (term: string) => {
    const syns = editingSyns.split(',').map(s => s.trim()).filter(s => s.length > 0)
    if (syns.length === 0) { setError('En az bir eşanlamlı gerekli'); return }
    try {
      await upsertRagSynonym(term, syns)
      setEditingTerm(null); setEditingSyns('')
      await refresh()
    } catch (e: any) { setError(e.message) }
  }
  const onDelete = async (term: string) => {
    try {
      await deleteRagSynonym(term)
      setConfirm(null)
      await refresh()
    } catch (e: any) { setError(e.message) }
  }
  const onTest = async () => {
    if (!testQ.trim()) return
    try { setTestOut(await testRagSynonyms(testQ)) }
    catch (e: any) { setError(e.message) }
  }

  if (!open) {
    return (
      <button onClick={() => setOpen(true)}
              className="text-xs px-3 py-2 rounded-md cursor-pointer self-start transition"
              style={{ background: 'var(--surface)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
        🔤 RAG Eşanlamlıları (Türkçe ↔ İngilizce)
      </button>
    )
  }

  return (
    <div className="rounded-xl p-4 space-y-4" style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
      <div className="flex items-center justify-between">
        <div>
          <h3 className="text-sm font-semibold" style={{ color: 'var(--text)' }}>🔤 RAG Eşanlamlıları</h3>
          <p className="text-[11px] mt-0.5" style={{ color: 'var(--mute)' }}>
            Kullanıcı "vergi" yazınca embedding/FTS sorgusu otomatik "vat kdv tax" haline gelir. Sözlük DB'de tutulur, 60sn cache. Türkçe ek alabilen terimler için 4+ karakter uzunluğundakiler prefix match yapar (örn. "vergisi" → "vergi" eşleşir).
          </p>
        </div>
        <button onClick={() => setOpen(false)}
                className="text-xs px-2 py-1 rounded cursor-pointer"
                style={{ background: 'var(--surface-2)', color: 'var(--mute)' }}>
          Kapat
        </button>
      </div>

      {error && (
        <div className="rounded-md px-3 py-2 text-xs"
             style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}>
          {error}
        </div>
      )}

      {/* Test sorgu genişletme */}
      <div className="rounded-md p-3 space-y-2" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
        <div className="text-[11px] font-medium" style={{ color: 'var(--mute)' }}>Sorgu Genişletme Testi</div>
        <div className="flex gap-2">
          <input value={testQ} onChange={e => setTestQ(e.target.value)} placeholder="Sorgu yaz (örn: vergi kolonları)"
                 onKeyDown={e => { if (e.key === 'Enter') onTest() }}
                 className="flex-1 px-3 py-1.5 rounded-md text-sm outline-none"
                 style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
          <button onClick={onTest}
                  className="px-3 py-1.5 rounded-md text-sm cursor-pointer"
                  style={{ background: 'var(--accent)', color: '#0a0a0a' }}>
            Genişlet
          </button>
        </div>
        {testOut && (
          <div className="text-xs space-y-1 mt-2">
            <div><span style={{ color: 'var(--mute)' }}>Genişletilmiş:</span> <code style={{ color: 'var(--text)' }}>{testOut.expanded}</code></div>
            {testOut.added.length > 0 && (
              <div><span style={{ color: 'var(--mute)' }}>Eklenen:</span>{' '}
                {testOut.added.map(a => (
                  <span key={a} className="inline-block text-[10px] px-1.5 py-0.5 rounded mx-0.5"
                        style={{ background: 'var(--accent)', color: '#0a0a0a' }}>{a}</span>
                ))}
              </div>
            )}
          </div>
        )}
      </div>

      {/* Yeni terim ekle */}
      <div className="rounded-md p-3 space-y-2" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
        <div className="text-[11px] font-medium" style={{ color: 'var(--mute)' }}>Yeni Terim Ekle</div>
        <div className="flex gap-2 flex-wrap">
          <input value={newTerm} onChange={e => setNewTerm(e.target.value)} placeholder="terim (lowercase, örn: vergi)"
                 className="px-3 py-1.5 rounded-md text-sm outline-none w-40"
                 style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
          <input value={newSyns} onChange={e => setNewSyns(e.target.value)} placeholder="eşanlamlılar (virgülle ayır: vat, kdv, tax)"
                 onKeyDown={e => { if (e.key === 'Enter') onAdd() }}
                 className="flex-1 min-w-[20rem] px-3 py-1.5 rounded-md text-sm outline-none"
                 style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
          <button onClick={onAdd} disabled={!newTerm.trim() || !newSyns.trim()}
                  className="px-3 py-1.5 rounded-md text-sm cursor-pointer disabled:opacity-50"
                  style={{ background: 'var(--accent)', color: '#0a0a0a' }}>
            + Ekle
          </button>
        </div>
      </div>

      {/* Liste */}
      <div className="rounded-md overflow-hidden" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
        <div className="px-3 py-2 text-[11px] flex items-center justify-between" style={{ color: 'var(--mute)', borderBottom: '1px solid var(--border)' }}>
          <span>Toplam {rows.length} terim</span>
          {loading && <span>yükleniyor…</span>}
        </div>
        <table className="w-full text-xs">
          <thead style={{ color: 'var(--mute)' }}>
            <tr>
              <th className="px-3 py-2 text-left font-medium" style={{ width: '14rem' }}>Terim</th>
              <th className="px-3 py-2 text-left font-medium">Eşanlamlılar</th>
              <th className="px-3 py-2 text-right font-medium" style={{ width: '8rem' }}></th>
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 && !loading && (
              <tr><td colSpan={3} className="px-3 py-4 text-center" style={{ color: 'var(--mute)' }}>Henüz eşanlamlı yok</td></tr>
            )}
            {rows.map(r => {
              const isEdit    = editingTerm === r.term
              const isConfirm = confirm === r.term
              return (
                <tr key={r.term} style={{ borderTop: '1px solid var(--border)' }}>
                  <td className="px-3 py-1.5 font-mono" style={{ color: 'var(--text)' }}>{r.term}</td>
                  <td className="px-3 py-1.5">
                    {isEdit ? (
                      <input value={editingSyns} onChange={e => setEditingSyns(e.target.value)} autoFocus
                             onKeyDown={e => { if (e.key === 'Enter') onSaveEdit(r.term); if (e.key === 'Escape') setEditingTerm(null) }}
                             className="w-full px-2 py-1 rounded text-xs outline-none"
                             style={{ background: 'var(--input-bg)', border: '1px solid var(--accent)', color: 'var(--text)' }} />
                    ) : (
                      <span style={{ color: 'var(--text-2)' }}>{r.synonyms.join(', ')}</span>
                    )}
                  </td>
                  <td className="px-3 py-1.5 text-right">
                    {isEdit ? (
                      <div className="inline-flex gap-1">
                        <button onClick={() => onSaveEdit(r.term)}
                                className="px-2 py-0.5 rounded text-[11px] cursor-pointer"
                                style={{ background: '#34a853', color: '#fff' }}>Kaydet</button>
                        <button onClick={() => setEditingTerm(null)}
                                className="px-2 py-0.5 rounded text-[11px] cursor-pointer"
                                style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>İptal</button>
                      </div>
                    ) : isConfirm ? (
                      <div className="inline-flex gap-1">
                        <button onClick={() => onDelete(r.term)}
                                className="px-2 py-0.5 rounded text-[11px] cursor-pointer"
                                style={{ background: '#ea4335', color: '#fff' }}>Sil</button>
                        <button onClick={() => setConfirm(null)}
                                className="px-2 py-0.5 rounded text-[11px] cursor-pointer"
                                style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>İptal</button>
                      </div>
                    ) : (
                      <div className="inline-flex gap-1">
                        <button onClick={() => { setEditingTerm(r.term); setEditingSyns(r.synonyms.join(', ')) }}
                                className="px-2 py-0.5 rounded text-[11px] cursor-pointer"
                                style={{ color: 'var(--mute)' }}
                                onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
                                onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}>
                          ✏ Düzenle
                        </button>
                        <button onClick={() => setConfirm(r.term)}
                                className="px-2 py-0.5 rounded text-[11px] cursor-pointer"
                                style={{ color: '#ea4335' }}
                                onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'rgba(234,67,53,0.1)'}
                                onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}>
                          Sil
                        </button>
                      </div>
                    )}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
    </div>
  )
}
