import { useEffect, useState } from 'react'
import { getActivityLog } from '../../../api/admin'
import type { ActivityPage } from '../../../api/admin'
import { DEFAULT_PAGE_SIZE, PageSizeSelector, formatDate } from './_shared'

const ACTION_LABELS: Record<string, { label: string; color: string }> = {
  'document.upload': { label: '📤 Doküman Yüklendi',  color: '#34a853' },
  'document.delete': { label: '🗑 Doküman Silindi',   color: '#ea4335' },
  'skill.upload':    { label: '📝 Skill Yüklendi',    color: '#4285f4' },
  'skill.delete':    { label: '🗑 Skill Silindi',     color: '#ea4335' },
  'template.create': { label: '✨ Şablon Oluşturuldu', color: '#34a853' },
  'template.update': { label: '✏️ Şablon Güncellendi', color: '#f59e0b' },
  'template.delete': { label: '🗑 Şablon Silindi',   color: '#ea4335' },
}

export default function ActivityTab() {
  const [data,    setData]    = useState<ActivityPage | null>(null)
  const [page,    setPage]    = useState(1)
  const [pageSize, setPageSize] = useState<number>(DEFAULT_PAGE_SIZE)
  const [filter,  setFilter]  = useState('')
  const [loading, setLoading] = useState(true)
  const [error,   setError]   = useState<string | null>(null)

  const load = async (p = page, f = filter, ps = pageSize) => {
    setLoading(true); setError(null)
    try { setData(await getActivityLog(p, ps, f || undefined)) }
    catch (e: any) { setError(e.message) }
    finally { setLoading(false) }
  }

  useEffect(() => { load() }, [])

  const onFilter = (f: string) => {
    setFilter(f); setPage(1); load(1, f, pageSize)
  }
  const onPageSize = (n: number) => {
    setPageSize(n); setPage(1); load(1, filter, n)
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
        <PageSizeSelector value={pageSize} onChange={onPageSize} compact />
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
