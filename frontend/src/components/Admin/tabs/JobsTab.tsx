import { useCallback, useEffect, useState } from 'react'
import { listAdminJobs, cancelJob, retryJob } from '../../../api/admin'
import type { JobsPage } from '../../../api/admin'
import JobProgressModal from '../JobProgressModal'

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

export default function JobsTab() {
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
