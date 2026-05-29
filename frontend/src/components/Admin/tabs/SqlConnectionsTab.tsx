import { useEffect, useState } from 'react'
import {
  listSqlConnections, createSqlConnection, updateSqlConnection,
  deleteSqlConnection, testSqlConnection, testSqlCredentials,
  listSqlObjects,
  getLatestJobForConnection,
  getSqlIngestedStats,
  generateSqlSkill,
} from '../../../api/admin'
import type {
  SqlConnection, SqlConnectionUpsert, SqlDbType,
  SqlObjectSummary,
  JobInfo, SqlIngestedStats,
} from '../../../api/admin'
import JobProgressModal from '../JobProgressModal'
import SqlDataDialog from '../SqlDataDialog'

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

export default function SqlConnectionsTab() {
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
  // Generate-skill busy state (per connection id) — UI feedback only
  const [generatingSkill, setGeneratingSkill] = useState<number | null>(null)

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

  // Generate SQL skill .md from the connection schema (LLM-authored, ~30s).
  // İzole özellik — SQL RAG'a hiç dokunmaz. Üretilen skill kullanıcı sohbette
  // istediğinde seçer; tıpkı mevcut cfs-db-model.md gibi sistem prompt'una eklenir.
  const onGenerateSkill = async (c: SqlConnection) => {
    const slug = c.name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')
    if (!window.confirm(
      `"${c.name}" şemasından bir SQL skill .md üretilsin mi?\n\n` +
      `LiteLLM üzerinden 6 bölümlü doküman yazılır (~30 saniye).\n` +
      `Mevcut "${slug || 'sql'}-db-model.md" üzerine yazılır.\n\n` +
      `Not: Bu işlem RAG yapısını etkilemez; yalnızca Skills/ klasörüne dosya yazar.`
    )) return
    setGeneratingSkill(c.id); setError(null); setMsg(null)
    try {
      const r = await generateSqlSkill(c.id)
      setMsg(`✓ ${r.skillFile} — ${r.tables} tablo · ${r.chars.toLocaleString('tr-TR')} karakter`)
    } catch (e: any) {
      setError(`Skill üretimi başarısız: ${e.message}`)
    } finally {
      setGeneratingSkill(null)
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
                    <button onClick={() => onGenerateSkill(c)}
                            disabled={generatingSkill === c.id}
                            className="px-2 py-1 rounded text-xs cursor-pointer disabled:opacity-50"
                            style={{ background: 'rgba(168,85,247,0.15)', color: '#a855f7', border: '1px solid rgba(168,85,247,0.3)' }}
                            title="Şemadan LLM ile SQL skill .md üret (~30 sn). RAG'ı etkilemez.">
                      {generatingSkill === c.id ? '⏳ …' : '🧠 Skill'}
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
