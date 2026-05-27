import { useEffect, useState } from 'react'
import { getUsageUsers, getUsageModels, getUsageLogs, getRatingStats } from '../../../api/admin'
import type { UserSpend, ModelSpend, SpendLog, RatingStats } from '../../../api/admin'
import { DEFAULT_PAGE_SIZE, PageSizeSelector, formatDate } from './_shared'

export default function UsageTab() {
  const [users,  setUsers]  = useState<UserSpend[]>([])
  const [models, setModels] = useState<ModelSpend[]>([])
  const [logs,    setLogs]    = useState<SpendLog[]>([])
  const [ratings, setRatings] = useState<RatingStats | null>(null)
  const [logsLimit, setLogsLimit] = useState<number>(DEFAULT_PAGE_SIZE)
  const [loading,  setLoading]  = useState(true)
  const [error,    setError]    = useState<string | null>(null)

  const load = async (limit = logsLimit) => {
    setLoading(true); setError(null)
    try {
      const [u, m, l, r] = await Promise.all([
        getUsageUsers(), getUsageModels(), getUsageLogs(limit), getRatingStats().catch(() => null)
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
        <button onClick={() => load()} className="px-3 py-1.5 rounded-lg text-xs cursor-pointer"
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
            <div className="px-4 py-3 flex items-center justify-between gap-3"
                 style={{ borderBottom: '1px solid var(--border)' }}>
              <span className="text-xs font-semibold uppercase tracking-wider" style={{ color: 'var(--mute)' }}>
                Son {logsLimit} İstek
              </span>
              <PageSizeSelector
                value={logsLimit}
                onChange={n => { setLogsLimit(n); load(n) }}
                compact
              />
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
                  {logs.slice(0, logsLimit).map((l, i) => (
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
