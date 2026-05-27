import { Fragment, useCallback, useEffect, useState } from 'react'
import { getEventLog, getEventLogSummary } from '../../../api/admin'
import type { EventLogPage, EventLogFilters, EventLogSummary } from '../../../api/admin'
import { DEFAULT_PAGE_SIZE, PageSizeSelector } from './_shared'

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

export default function SecurityTab() {
  const [page, setPage] = useState<EventLogPage | null>(null)
  const [summary, setSummary] = useState<EventLogSummary | null>(null)
  const [filters, setFilters] = useState<EventLogFilters>({ page: 1, pageSize: DEFAULT_PAGE_SIZE })
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
        <div className="flex items-center gap-2">
          <PageSizeSelector
            value={filters.pageSize ?? DEFAULT_PAGE_SIZE}
            onChange={n => setFilters(prev => ({ ...prev, pageSize: n, page: 1 }))}
            compact
          />
          <button onClick={load} disabled={loading}
                  className="px-3 py-1.5 rounded-md text-xs cursor-pointer disabled:opacity-50"
                  style={{ background: 'var(--surface-hi)', color: 'var(--text-2)', border: '1px solid var(--border)' }}>
            {loading ? '…' : '🔄 Yenile'}
          </button>
        </div>
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
