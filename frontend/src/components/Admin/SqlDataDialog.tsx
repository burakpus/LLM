import { useEffect, useState } from 'react'
import {
  listSqlTables, listTableGroups, createTableGroup, deleteTableGroup,
  listTableConfigs, upsertTableConfig, deleteTableConfig, syncSqlData,
  getLatestJobForConnection, bulkAssignTableGroup,
} from '../../api/admin'
import type {
  SqlConnection, SqlTable, SqlTableGroup, SqlTableConfig, JobInfo,
} from '../../api/admin'

interface Props {
  conn: SqlConnection
  onClose: () => void
  onJobStarted: (jobId: number, title: string, subtitle?: string) => void
}

export default function SqlDataDialog({ conn, onClose, onJobStarted }: Props) {
  const [tables,  setTables]  = useState<SqlTable[] | null>(null)
  const [groups,  setGroups]  = useState<SqlTableGroup[]>([])
  const [configs, setConfigs] = useState<SqlTableConfig[]>([])
  const [latestJob, setLatestJob] = useState<JobInfo | null>(null)
  const [loading, setLoading] = useState(true)
  const [err,     setErr]     = useState<string | null>(null)
  const [filter,  setFilter]  = useState('')

  // Per-table config editor
  const [editing, setEditing] = useState<{ schema: string; table: string; cfg?: SqlTableConfig } | null>(null)

  // Group manager modal
  const [showGroupMgr, setShowGroupMgr] = useState(false)
  const [newGroupName, setNewGroupName] = useState('')

  // Bulk selection state — set of table_config_ids
  const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set())
  const [bulkBusy, setBulkBusy] = useState(false)

  const reload = async () => {
    setLoading(true); setErr(null)
    try {
      const [tbls, grps, cfgs, latest] = await Promise.all([
        listSqlTables(conn.id),
        listTableGroups(conn.id),
        listTableConfigs(conn.id),
        getLatestJobForConnection(conn.id, 'sql.sync-data').catch(() => null),
      ])
      setTables(tbls); setGroups(grps); setConfigs(cfgs); setLatestJob(latest)
    } catch (e: any) { setErr(e.message) }
    finally { setLoading(false) }
  }

  // Surgical: refetch just the table configs (lightweight) — used after edits
  const reloadConfigs = async () => {
    try {
      const cfgs = await listTableConfigs(conn.id)
      setConfigs(cfgs)
    } catch (e: any) { setErr(e.message) }
  }
  useEffect(() => { reload() }, [conn.id])

  const tableKey = (s: string, t: string) => `${s}|${t}`
  const cfgByKey = new Map(configs.map(c => [tableKey(c.schema, c.table), c]))

  const filtered = (tables ?? []).filter(t =>
    !filter || `${t.schema}.${t.name}`.toLowerCase().includes(filter.toLowerCase()))

  // Group tables: by groupId from configs, then "Atanmamış" for the rest
  const groupBuckets: { id: number | null; name: string; items: SqlTable[] }[] = []
  for (const g of groups) groupBuckets.push({ id: g.id, name: g.name, items: [] })
  groupBuckets.push({ id: null, name: 'Atanmamış', items: [] })
  for (const tbl of filtered) {
    const cfg = cfgByKey.get(tableKey(tbl.schema, tbl.name))
    const target = cfg?.groupId ?? null
    const bucket = groupBuckets.find(b => b.id === target) ?? groupBuckets[groupBuckets.length - 1]
    bucket.items.push(tbl)
  }

  const activeJob = latestJob && (latestJob.status === 'queued' || latestJob.status === 'running') ? latestJob : null

  const onSync = async () => {
    try {
      const { jobId } = await syncSqlData(conn.id)
      onJobStarted(jobId, `Veri Senkronu — ${conn.name}`, 'Yapılandırılmış tablolardan delta veri çekiliyor')
      onClose()
    } catch (e: any) { setErr(e.message) }
  }

  const onAddGroup = async () => {
    if (!newGroupName.trim()) return
    try {
      const { id } = await createTableGroup(conn.id, newGroupName.trim(), groups.length)
      // Surgical append
      setGroups(prev => [...prev, { id, name: newGroupName.trim(), sortOrder: prev.length }])
      setNewGroupName('')
    } catch (e: any) { setErr(e.message) }
  }
  const onDelGroup = async (gid: number) => {
    if (!confirm('Bu grubu sil? İçindeki tablolar "Atanmamış"a düşer.')) return
    try {
      await deleteTableGroup(conn.id, gid)
      // Surgical: remove group locally + null out group_id on configs that had it
      setGroups(prev => prev.filter(g => g.id !== gid))
      setConfigs(prev => prev.map(c => c.groupId === gid ? { ...c, groupId: null } : c))
    } catch (e: any) { setErr(e.message) }
  }

  // Bulk assign selected tables (must already have configs) to a group
  const onBulkAssign = async (groupId: number | null) => {
    const ids = Array.from(selectedIds)
    if (ids.length === 0) return
    setBulkBusy(true); setErr(null)
    try {
      await bulkAssignTableGroup(conn.id, ids, groupId)
      // Surgical update of affected configs
      setConfigs(prev => prev.map(c => ids.includes(c.id) ? { ...c, groupId } : c))
      setSelectedIds(new Set())
    } catch (e: any) { setErr(e.message) }
    finally { setBulkBusy(false) }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4"
         style={{ background: 'rgba(0,0,0,0.6)', backdropFilter: 'blur(4px)' }}
         onClick={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="w-full max-w-4xl rounded-2xl overflow-hidden shadow-2xl flex flex-col"
           style={{ background: 'var(--bg)', border: '1px solid var(--border)', maxHeight: '90vh' }}>

        {/* Header */}
        <div className="px-5 py-4 flex items-center gap-3 shrink-0"
             style={{ borderBottom: '1px solid var(--border)', background: 'var(--surface)' }}>
          <span className="text-2xl">💾</span>
          <div className="flex-1">
            <div className="font-semibold" style={{ color: 'var(--text)' }}>
              Veri Senkronu — {conn.name}
            </div>
            <div className="text-xs" style={{ color: 'var(--mute)' }}>
              PK + created/updated kolonları ile satır-bazlı delta — sadece değişenler ingest edilir
            </div>
          </div>
          <button onClick={() => setShowGroupMgr(true)}
                  className="px-3 py-1.5 rounded-lg text-xs cursor-pointer"
                  style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
            📁 Gruplar
          </button>
          <button onClick={onClose}
                  className="w-8 h-8 rounded-full flex items-center justify-center cursor-pointer"
                  style={{ color: 'var(--mute)' }}>×</button>
        </div>

        <div className="flex-1 overflow-y-auto p-5 space-y-4">
          {err && (
            <div className="rounded-md px-3 py-2 text-xs"
                 style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}>
              {err}
            </div>
          )}

          {/* Active job card */}
          {activeJob && (
            <div className="rounded-xl p-4"
                 style={{ background: 'rgba(138,180,248,0.08)', border: '1px solid rgba(138,180,248,0.3)' }}>
              <div className="flex items-center justify-between text-xs">
                <span style={{ color: 'var(--accent-hi)' }}>⏳ Şu an çalışıyor — Job #{activeJob.id}</span>
                <span className="font-mono" style={{ color: 'var(--text-2)' }}>
                  {activeJob.progressCur.toLocaleString()} / {activeJob.progressTot.toLocaleString()}
                </span>
              </div>
              <div className="text-xs mt-1" style={{ color: 'var(--mute)' }}>{activeJob.message}</div>
              <button onClick={() => onJobStarted(activeJob.id, `Veri Senkronu — ${conn.name}`, `Job #${activeJob.id}`)}
                      className="w-full mt-2 py-1.5 rounded text-xs font-medium cursor-pointer"
                      style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
                🔍 İlerlemeyi izle
              </button>
            </div>
          )}

          {/* Latest finished job */}
          {latestJob && !activeJob && (
            <div className="rounded-xl p-3 text-xs"
                 style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
              <div className="flex items-center justify-between">
                <span style={{ color: latestJob.status === 'completed' ? '#34a853' : '#ea4335' }}>
                  {latestJob.status === 'completed' ? '✓ Son sync başarılı' : `✕ Son sync başarısız`}
                </span>
                <span style={{ color: 'var(--mute)' }}>
                  {latestJob.completedAt && new Date(latestJob.completedAt).toLocaleString()}
                </span>
              </div>
              {latestJob.status === 'completed' && latestJob.result && (
                <div className="mt-2 grid grid-cols-5 gap-2 text-center">
                  <div><strong style={{ color: '#34a853' }}>{(latestJob.result as any).added ?? 0}</strong>
                    <div className="text-[9px]" style={{ color: 'var(--mute)' }}>yeni</div></div>
                  <div><strong style={{ color: '#f59e0b' }}>{(latestJob.result as any).updated ?? 0}</strong>
                    <div className="text-[9px]" style={{ color: 'var(--mute)' }}>değişen</div></div>
                  <div><strong style={{ color: 'var(--text)' }}>{(latestJob.result as any).unchanged ?? 0}</strong>
                    <div className="text-[9px]" style={{ color: 'var(--mute)' }}>aynı</div></div>
                  <div><strong style={{ color: 'var(--accent-hi)' }}>{(latestJob.result as any).chunks ?? 0}</strong>
                    <div className="text-[9px]" style={{ color: 'var(--mute)' }}>chunk</div></div>
                  <div><strong style={{ color: 'var(--text)' }}>{(latestJob.result as any).rows ?? 0}</strong>
                    <div className="text-[9px]" style={{ color: 'var(--mute)' }}>satır</div></div>
                </div>
              )}
            </div>
          )}

          {loading && <div className="text-center py-8 text-sm" style={{ color: 'var(--mute)' }}>Yükleniyor…</div>}

          {!loading && tables && (
            <>
              <div className="flex items-center gap-2">
                <input value={filter} onChange={e => setFilter(e.target.value)}
                       placeholder="Tablo ara…"
                       className="flex-1 rounded-md px-3 py-2 text-sm outline-none"
                       style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
                <span className="text-xs" style={{ color: 'var(--mute)' }}>
                  <strong style={{ color: 'var(--accent-hi)' }}>{configs.length}</strong> / {tables.length} yapılandırılmış
                </span>
              </div>

              {/* Bulk action bar (when something selected) */}
              {selectedIds.size > 0 && (
                <div className="rounded-xl p-2 flex items-center gap-2 text-xs"
                     style={{ background: 'rgba(138,180,248,0.10)', border: '1px solid rgba(138,180,248,0.3)' }}>
                  <span style={{ color: 'var(--accent-hi)' }}>{selectedIds.size} tablo seçili</span>
                  <span style={{ color: 'var(--mute)' }}>→ gruba ata:</span>
                  <select disabled={bulkBusy} defaultValue=""
                          onChange={e => {
                            const v = e.target.value
                            if (v === '') return
                            onBulkAssign(v === 'null' ? null : parseInt(v, 10))
                            e.target.value = ''
                          }}
                          className="rounded-md px-2 py-1 text-xs outline-none cursor-pointer"
                          style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}>
                    <option value="">— Grup seç —</option>
                    <option value="null">— Atanmamış —</option>
                    {groups.map(g => <option key={g.id} value={g.id}>{g.name}</option>)}
                  </select>
                  <button onClick={() => setSelectedIds(new Set())}
                          className="ml-auto px-2 py-1 rounded cursor-pointer"
                          style={{ color: 'var(--mute)' }}>
                    Seçimi temizle
                  </button>
                </div>
              )}

              {/* Groups & tables */}
              {groupBuckets.filter(b => b.items.length > 0).map(b => {
                const groupCfgIds = b.items
                  .map(t => cfgByKey.get(tableKey(t.schema, t.name))?.id)
                  .filter((x): x is number => x != null)
                const allSelected = groupCfgIds.length > 0 && groupCfgIds.every(id => selectedIds.has(id))
                const toggleGroup = () => {
                  setSelectedIds(prev => {
                    const next = new Set(prev)
                    if (allSelected) groupCfgIds.forEach(id => next.delete(id))
                    else groupCfgIds.forEach(id => next.add(id))
                    return next
                  })
                }
                return (
                <div key={`g-${b.id ?? 'na'}`} className="rounded-xl overflow-hidden"
                     style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
                  <div className="px-3 py-2 text-xs font-semibold flex items-center gap-2"
                       style={{ background: 'var(--surface-hi)', color: 'var(--mute)', borderBottom: '1px solid var(--border)' }}>
                    {groupCfgIds.length > 0 && (
                      <input type="checkbox" checked={allSelected} onChange={toggleGroup}
                             className="cursor-pointer"
                             title="Bu gruptaki yapılandırılmış tabloları seç/bırak" />
                    )}
                    <span className="flex-1">📁 {b.name} ({b.items.length})</span>
                  </div>
                  {b.items.map(t => {
                    const cfg = cfgByKey.get(tableKey(t.schema, t.name))
                    const piiCount = t.columns.filter(c => c.isPII).length
                    const checked = cfg ? selectedIds.has(cfg.id) : false
                    const toggle = () => {
                      if (!cfg) return
                      setSelectedIds(prev => {
                        const next = new Set(prev)
                        if (next.has(cfg.id)) next.delete(cfg.id); else next.add(cfg.id)
                        return next
                      })
                    }
                    return (
                      <div key={tableKey(t.schema, t.name)} className="px-3 py-2 flex items-center gap-3 text-xs"
                           style={{ borderBottom: '1px solid var(--border)' }}>
                        {cfg ? (
                          <input type="checkbox" checked={checked} onChange={toggle}
                                 className="cursor-pointer" title="Toplu işlem için seç" />
                        ) : (
                          <span className="w-4 inline-block" />
                        )}
                        <div className="flex-1 min-w-0">
                          <div className="text-sm font-medium truncate" style={{ color: 'var(--text)' }}>
                            {t.schema}.{t.name}
                            {cfg?.lastSyncStatus === 'failed' && (
                              <span className="ml-2 px-1.5 py-0.5 rounded text-[10px]"
                                    title={cfg.lastSyncError}
                                    style={{ background: 'rgba(234,67,53,0.15)', color: '#ea4335' }}>
                                ✕ son sync hata
                              </span>
                            )}
                            {cfg?.lastSyncStatus === 'ok' && (cfg.lastSyncAdded + cfg.lastSyncUpdated) > 0 && (
                              <span className="ml-2 px-1.5 py-0.5 rounded text-[10px]"
                                    title="Son sync sonucu"
                                    style={{ background: 'rgba(52,168,83,0.15)', color: '#34a853' }}>
                                +{cfg.lastSyncAdded} / ↻{cfg.lastSyncUpdated}
                              </span>
                            )}
                          </div>
                          <div className="text-[10px]" style={{ color: 'var(--mute)' }}>
                            {t.columns.length} kolon · ~{t.estimatedRows.toLocaleString()} satır
                            {piiCount > 0 && <span style={{ color: '#ea4335' }}> · {piiCount} PII</span>}
                            {cfg && (
                              <>
                                {' · '}
                                <span style={{ color: 'var(--accent-hi)' }}>PK: {cfg.pkCol}</span>
                                {cfg.updatedCol && <span style={{ color: '#34a853' }}> · ↻ {cfg.updatedCol}</span>}
                                {cfg.includedColumns.length > 0 && (
                                  <span style={{ color: 'var(--mute)' }}> · {cfg.includedColumns.length} kolon seçili</span>
                                )}
                                {cfg.lastSyncedAt && <span> · son sync: {new Date(cfg.lastSyncedAt).toLocaleString()}</span>}
                              </>
                            )}
                          </div>
                        </div>
                        <button onClick={() => setEditing({ schema: t.schema, table: t.name, cfg })}
                                className="px-2 py-1 rounded text-xs cursor-pointer"
                                style={{
                                  background: cfg ? 'rgba(52,168,83,0.15)' : 'var(--surface-hi)',
                                  color: cfg ? '#34a853' : 'var(--text-2)',
                                  border: `1px solid ${cfg ? 'rgba(52,168,83,0.3)' : 'var(--border)'}`,
                                }}>
                          {cfg ? '✓ Yapılandırıldı' : '⚙ Yapılandır'}
                        </button>
                      </div>
                    )
                  })}
                </div>
                )
              })}
            </>
          )}
        </div>

        {/* Footer */}
        <div className="px-5 py-4 flex gap-2 shrink-0" style={{ borderTop: '1px solid var(--border)', background: 'var(--surface)' }}>
          <button onClick={onSync}
                  disabled={!!activeJob || configs.length === 0}
                  title={activeJob ? 'Zaten çalışan sync var' : configs.length === 0 ? 'En az bir tablo yapılandırılmalı' : 'Tüm yapılandırılmış tabloları senkronize et'}
                  className="flex-1 py-2 rounded-lg text-sm font-semibold cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed"
                  style={{ background: '#f59e0b', color: '#0b1929' }}>
            {activeJob ? '⏳ Zaten çalışıyor' : `🔄 ${configs.length} Tabloyu Senkronize Et (delta)`}
          </button>
          <button onClick={onClose}
                  className="px-4 py-2 rounded-lg text-sm cursor-pointer"
                  style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
            Kapat
          </button>
        </div>
      </div>

      {/* Per-table config editor */}
      {editing && (
        <TableConfigEditor
          conn={conn}
          schema={editing.schema}
          table={editing.table}
          existing={editing.cfg}
          tableInfo={tables?.find(t => t.schema === editing.schema && t.name === editing.table) ?? null}
          groups={groups}
          onClose={(saved) => {
            setEditing(null)
            if (saved) reloadConfigs()  // surgical — only refresh configs, not the whole dialog
          }}
        />
      )}

      {/* Group manager modal */}
      {showGroupMgr && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4"
             style={{ background: 'rgba(0,0,0,0.6)' }}
             onClick={e => { if (e.target === e.currentTarget) setShowGroupMgr(false) }}>
          <div className="w-full max-w-md rounded-2xl overflow-hidden shadow-2xl"
               style={{ background: 'var(--bg)', border: '1px solid var(--border)' }}>
            <div className="px-5 py-4 flex items-center gap-3"
                 style={{ borderBottom: '1px solid var(--border)', background: 'var(--surface)' }}>
              <span className="text-xl">📁</span>
              <div className="flex-1 font-semibold" style={{ color: 'var(--text)' }}>Tablo Grupları</div>
              <button onClick={() => setShowGroupMgr(false)}
                      className="w-8 h-8 rounded-full cursor-pointer text-lg" style={{ color: 'var(--mute)' }}>×</button>
            </div>
            <div className="p-4 space-y-2 max-h-96 overflow-y-auto">
              {groups.map(g => (
                <div key={g.id} className="flex items-center gap-2 p-2 rounded"
                     style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
                  <span className="flex-1 text-sm" style={{ color: 'var(--text)' }}>{g.name}</span>
                  <button onClick={() => onDelGroup(g.id)}
                          className="px-2 py-1 rounded text-xs cursor-pointer"
                          style={{ color: '#ea4335' }}>🗑</button>
                </div>
              ))}
              {groups.length === 0 && (
                <div className="text-center text-xs py-4" style={{ color: 'var(--mute)' }}>
                  Henüz grup yok. Aşağıdan ekleyin.
                </div>
              )}
              <div className="flex gap-2 pt-2" style={{ borderTop: '1px solid var(--border)' }}>
                <input value={newGroupName} onChange={e => setNewGroupName(e.target.value)}
                       onKeyDown={e => { if (e.key === 'Enter') onAddGroup() }}
                       placeholder="Yeni grup adı (örn: Krediler)"
                       className="flex-1 rounded-md px-3 py-1.5 text-sm outline-none"
                       style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
                <button onClick={onAddGroup}
                        className="px-3 py-1.5 rounded-md text-xs font-medium cursor-pointer"
                        style={{ background: 'var(--accent)', color: '#0b1929' }}>+ Ekle</button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

// ─────────────────────────────────────────────────────────────────────────────
// Per-table config editor
// ─────────────────────────────────────────────────────────────────────────────
function TableConfigEditor({ conn, schema, table, existing, tableInfo, groups, onClose }: {
  conn:      SqlConnection
  schema:    string
  table:     string
  existing?: SqlTableConfig
  tableInfo: SqlTable | null
  groups:    SqlTableGroup[]
  onClose:   (saved: boolean) => void
}) {
  const [pkCol,        setPkCol]        = useState(existing?.pkCol ?? '')
  const [createdCol,   setCreatedCol]   = useState(existing?.createdCol ?? '')
  const [updatedCol,   setUpdatedCol]   = useState(existing?.updatedCol ?? '')
  const [rowLimit,     setRowLimit]     = useState(existing?.rowLimit ?? 1000)
  const [whereClause,  setWhereClause]  = useState(existing?.whereClause ?? '')
  const [groupId,      setGroupId]      = useState<number | null>(existing?.groupId ?? null)
  const [collection,   setCollection]   = useState(existing?.collection ?? `sql-data-${conn.name.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`)
  // Column selection: empty array = all columns. Otherwise restrict to listed columns.
  const [includedColumns, setIncludedColumns] = useState<string[]>(existing?.includedColumns ?? [])
  const [showColumnPicker, setShowColumnPicker] = useState(false)
  const [saving,       setSaving]       = useState(false)
  const [err,          setErr]          = useState<string | null>(null)

  // Suggest defaults based on column names if not set
  useEffect(() => {
    if (!tableInfo) return
    if (!pkCol) {
      const lowerName = tableInfo.name.toLowerCase()
      const pk = tableInfo.columns.find(c => {
        const n = c.name.toLowerCase()
        return n === 'id' || n === lowerName + '_id' || n === lowerName + 'id'
      })
      if (pk) setPkCol(pk.name)
    }
    if (!createdCol) {
      const c = tableInfo.columns.find(c => /created.?at|create_date|kayit_tarihi|insert_date/i.test(c.name))
      if (c) setCreatedCol(c.name)
    }
    if (!updatedCol) {
      const c = tableInfo.columns.find(c => /updated.?at|update_date|son_guncelleme|modified_date|son_islem_tarihi/i.test(c.name))
      if (c) setUpdatedCol(c.name)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tableInfo])

  const onSave = async () => {
    if (!pkCol.trim()) { setErr('PK kolonu gerekli'); return }
    setSaving(true); setErr(null)
    try {
      await upsertTableConfig(conn.id, {
        schema, table,
        pkCol: pkCol.trim(),
        createdCol: createdCol.trim(),
        updatedCol: updatedCol.trim(),
        rowLimit, whereClause: whereClause.trim(),
        includedColumns,
        groupId, collection: collection.trim(),
      })
      onClose(true)
    } catch (e: any) { setErr(e.message) }
    finally { setSaving(false) }
  }

  const onDelete = async () => {
    if (!existing) return
    if (!confirm("Bu tablo yapılandırması silinsin? RAG'daki ingest edilmiş satırlar kalır ama tracking silinir.")) return
    try {
      await deleteTableConfig(conn.id, existing.id)
      onClose(true)
    } catch (e: any) { setErr(e.message) }
  }

  return (
    <div className="fixed inset-0 z-[60] flex items-center justify-center p-4"
         style={{ background: 'rgba(0,0,0,0.7)' }}
         onClick={e => { if (e.target === e.currentTarget) onClose(false) }}>
      <div className="w-full max-w-2xl rounded-2xl overflow-hidden shadow-2xl flex flex-col"
           style={{ background: 'var(--bg)', border: '1px solid var(--border)', maxHeight: '90vh' }}>
        <div className="px-5 py-4 flex items-center gap-3 shrink-0"
             style={{ borderBottom: '1px solid var(--border)', background: 'var(--surface)' }}>
          <span className="text-xl">⚙</span>
          <div className="flex-1">
            <div className="font-semibold" style={{ color: 'var(--text)' }}>{schema}.{table}</div>
            <div className="text-xs" style={{ color: 'var(--mute)' }}>Delta sync için PK + tarih kolonları</div>
          </div>
          <button onClick={() => onClose(false)} className="w-8 h-8 rounded-full cursor-pointer text-lg" style={{ color: 'var(--mute)' }}>×</button>
        </div>

        <div className="flex-1 overflow-y-auto p-5 space-y-3">
          {err && (
            <div className="rounded-md px-3 py-2 text-xs" style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335' }}>{err}</div>
          )}

          {tableInfo && (
            <div className="text-[10px]" style={{ color: 'var(--mute)' }}>
              Mevcut kolonlar: {tableInfo.columns.map(c => c.name).join(', ')}
            </div>
          )}

          <ColumnSelect label="PK Kolonu *" value={pkCol} onChange={setPkCol} columns={tableInfo?.columns ?? []} hint="Tek kolon veya 'col1,col2' (composite)" />
          <ColumnSelect label="Created At" value={createdCol} onChange={setCreatedCol} columns={tableInfo?.columns ?? []} hint="Yeni kayıt tespiti (opsiyonel)" />
          <ColumnSelect label="Updated At" value={updatedCol} onChange={setUpdatedCol} columns={tableInfo?.columns ?? []} hint="Değişiklik tespiti — delta için gerekli" />

          <div className="grid grid-cols-2 gap-3">
            <label className="block">
              <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Tablo başına satır limiti</div>
              <input type="number" value={rowLimit} onChange={e => setRowLimit(parseInt(e.target.value) || 1000)}
                     min={1} max={100000}
                     className="w-full rounded-md px-3 py-2 text-sm outline-none"
                     style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
            </label>
            <label className="block">
              <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Grup</div>
              <select value={groupId ?? ''} onChange={e => setGroupId(e.target.value ? parseInt(e.target.value) : null)}
                      className="w-full rounded-md px-3 py-2 text-sm outline-none"
                      style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}>
                <option value="">— Atanmamış —</option>
                {groups.map(g => <option key={g.id} value={g.id}>{g.name}</option>)}
              </select>
            </label>
          </div>

          <label className="block">
            <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>WHERE filtresi (opsiyonel)</div>
            <input value={whereClause} onChange={e => setWhereClause(e.target.value)}
                   placeholder="aktif = 1 AND silinmis = 0"
                   className="w-full rounded-md px-3 py-2 text-sm outline-none font-mono"
                   style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
          </label>

          {/* Column selection */}
          <div className="rounded-md p-2"
               style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
            <div className="flex items-center justify-between text-xs">
              <div style={{ color: 'var(--mute)' }}>
                Dahil edilecek kolonlar:
                {' '}
                <strong style={{ color: includedColumns.length === 0 ? 'var(--accent-hi)' : 'var(--text)' }}>
                  {includedColumns.length === 0 ? 'TÜMÜ' : `${includedColumns.length} kolon seçili`}
                </strong>
              </div>
              <button onClick={() => setShowColumnPicker(s => !s)}
                      className="px-2 py-1 rounded text-xs cursor-pointer"
                      style={{ background: 'var(--surface-hi)', color: 'var(--text-2)', border: '1px solid var(--border)' }}>
                {showColumnPicker ? 'Gizle' : '⚙ Kolon seç'}
              </button>
            </div>
            {showColumnPicker && tableInfo && (
              <>
                <div className="flex gap-2 mt-2 text-[11px]">
                  <button onClick={() => setIncludedColumns([])}
                          className="px-2 py-0.5 rounded cursor-pointer"
                          style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>
                    Tümünü seç (varsayılan)
                  </button>
                  <button onClick={() => setIncludedColumns(tableInfo.columns.filter(c => !c.isPII).map(c => c.name))}
                          className="px-2 py-0.5 rounded cursor-pointer"
                          style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>
                    Sadece PII olmayanlar
                  </button>
                  <button onClick={() => setIncludedColumns(tableInfo.columns.map(c => c.name))}
                          className="px-2 py-0.5 rounded cursor-pointer"
                          style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>
                    Hepsini işaretle
                  </button>
                </div>
                <div className="mt-2 max-h-40 overflow-y-auto grid grid-cols-2 gap-1">
                  {tableInfo.columns.map(c => {
                    const selected = includedColumns.length === 0 || includedColumns.includes(c.name)
                    const allMode  = includedColumns.length === 0
                    return (
                      <label key={c.name} className="flex items-center gap-2 text-xs px-2 py-1 rounded cursor-pointer"
                             style={{ background: selected && !allMode ? 'rgba(138,180,248,0.10)' : 'transparent' }}
                             title={c.dataType}>
                        <input type="checkbox" checked={selected}
                               onChange={e => {
                                 if (allMode) {
                                   // Switching from "all" to explicit list — start with everything checked except this one
                                   const all = tableInfo.columns.map(x => x.name)
                                   setIncludedColumns(e.target.checked ? all : all.filter(x => x !== c.name))
                                 } else {
                                   setIncludedColumns(prev => e.target.checked
                                     ? Array.from(new Set([...prev, c.name]))
                                     : prev.filter(x => x !== c.name))
                                 }
                               }} />
                        <span style={{ color: 'var(--text)' }}>{c.name}</span>
                        {c.isPII && <span style={{ color: '#ea4335' }} title="PII">⚠</span>}
                      </label>
                    )
                  })}
                </div>
                <div className="text-[10px] mt-1" style={{ color: 'var(--mute)' }}>
                  Hepsi seçiliyken liste boş tutulur → backend tüm kolonları (PII maskeli) çeker. PK ve updated kolonları her zaman dahil edilir.
                </div>
              </>
            )}
          </div>

          <label className="block">
            <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>RAG Koleksiyonu</div>
            <input value={collection} onChange={e => setCollection(e.target.value)}
                   className="w-full rounded-md px-3 py-2 text-sm outline-none"
                   style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
          </label>

          {existing?.lastSyncedAt && (
            <div className="text-[11px] p-2 rounded" style={{ background: 'var(--surface-2)', color: 'var(--mute)' }}>
              Son sync: {new Date(existing.lastSyncedAt).toLocaleString()}
              {existing.lastMaxUpdatedAt && <> · son update_at: {new Date(existing.lastMaxUpdatedAt).toLocaleString()}</>}
            </div>
          )}
        </div>

        <div className="px-5 py-4 flex gap-2 shrink-0" style={{ borderTop: '1px solid var(--border)', background: 'var(--surface)' }}>
          <button onClick={onSave} disabled={saving || !pkCol.trim()}
                  className="flex-1 py-2 rounded-lg text-sm font-semibold cursor-pointer disabled:opacity-50"
                  style={{ background: 'var(--accent)', color: '#0b1929' }}>
            {saving ? 'Kaydediliyor…' : existing ? 'Güncelle' : 'Kaydet'}
          </button>
          {existing && (
            <button onClick={onDelete}
                    className="px-3 py-2 rounded-lg text-sm cursor-pointer"
                    style={{ background: 'transparent', border: '1px solid #ef4444', color: '#ef4444' }}>
              🗑
            </button>
          )}
          <button onClick={() => onClose(false)}
                  className="px-4 py-2 rounded-lg text-sm cursor-pointer"
                  style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
            İptal
          </button>
        </div>
      </div>
    </div>
  )
}

function ColumnSelect({ label, value, onChange, columns, hint }: {
  label: string; value: string; onChange: (v: string) => void
  columns: { name: string; dataType: string }[]; hint?: string
}) {
  return (
    <label className="block">
      <div className="text-xs mb-1 flex items-center justify-between" style={{ color: 'var(--mute)' }}>
        <span>{label}</span>
        {hint && <span className="text-[10px]" style={{ color: 'var(--mute-2)' }}>{hint}</span>}
      </div>
      <div className="flex gap-2">
        <input value={value} onChange={e => onChange(e.target.value)}
               className="flex-1 rounded-md px-3 py-2 text-sm outline-none font-mono"
               style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
        <select value="" onChange={e => { if (e.target.value) onChange(e.target.value) }}
                className="rounded-md px-2 py-2 text-xs outline-none"
                style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}>
          <option value="">↓ kolon seç</option>
          {columns.map(c => <option key={c.name} value={c.name}>{c.name} ({c.dataType})</option>)}
        </select>
      </div>
    </label>
  )
}
