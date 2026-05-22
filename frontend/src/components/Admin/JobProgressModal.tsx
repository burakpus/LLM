import { useEffect, useRef, useState } from 'react'
import { getJob } from '../../api/admin'
import type { JobInfo } from '../../api/admin'

interface Props {
  jobId:    number
  title:    string
  subtitle?: string
  onClose:  () => void
  // Optional renderer for the result payload (when status=completed)
  renderResult?: (result: any) => React.ReactNode
}

export default function JobProgressModal({ jobId, title, subtitle, onClose, renderResult }: Props) {
  const [job, setJob] = useState<JobInfo | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const tick = useRef<number | null>(null)
  const notified = useRef(false)
  const startSnap = useRef<{ time: number; cur: number } | null>(null)

  // Request browser notification permission on mount
  useEffect(() => {
    if ('Notification' in window && Notification.permission === 'default') {
      Notification.requestPermission()
    }
  }, [])

  useEffect(() => {
    let cancelled = false
    const poll = async () => {
      try {
        const j = await getJob(jobId)
        if (cancelled) return
        setJob(j)
        // Snapshot first running progress for ETA
        if (j.status === 'running' && !startSnap.current && j.progressCur > 0)
          startSnap.current = { time: Date.now(), cur: j.progressCur }

        if (j.status === 'completed' || j.status === 'failed' || j.status === 'cancelled') {
          if (tick.current) { window.clearInterval(tick.current); tick.current = null }
          // Browser notification
          if (!notified.current && 'Notification' in window && Notification.permission === 'granted') {
            const ok = j.status === 'completed'
            new Notification(`${ok ? '✓' : '✕'} ${title}`, {
              body: ok ? 'İşlem başarıyla tamamlandı' : `Başarısız: ${j.error?.slice(0, 100) ?? ''}`,
              icon: '/favicon.ico',
            })
            notified.current = true
          }
        }
      } catch (e: any) {
        if (!cancelled) setErr(e.message)
      }
    }
    poll()
    tick.current = window.setInterval(poll, 1500)
    return () => {
      cancelled = true
      if (tick.current) window.clearInterval(tick.current)
    }
  }, [jobId, title])

  // Modal is always closable — job continues in background regardless
  const finished = job && (job.status === 'completed' || job.status === 'failed' || job.status === 'cancelled')
  const pct      = job && job.progressTot > 0 ? Math.round((job.progressCur / job.progressTot) * 100) : 0

  // ETA estimation
  let eta: string | null = null
  if (job?.status === 'running' && startSnap.current && job.progressCur > startSnap.current.cur && job.progressTot > 0) {
    const elapsedMs = Date.now() - startSnap.current.time
    const delta     = job.progressCur - startSnap.current.cur
    const rate      = delta / (elapsedMs / 1000)            // items/sec
    const remaining = job.progressTot - job.progressCur
    const remSec    = Math.round(remaining / rate)
    if (remSec > 0 && remSec < 36000) {
      const m = Math.floor(remSec / 60), s = remSec % 60
      eta = m > 0 ? `~${m}d ${s}s` : `~${s}s`
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4"
         style={{ background: 'rgba(0,0,0,0.6)', backdropFilter: 'blur(4px)' }}
         onClick={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="w-full max-w-2xl rounded-2xl overflow-hidden shadow-2xl flex flex-col"
           style={{ background: 'var(--bg)', border: '1px solid var(--border)', maxHeight: '85vh' }}>

        {/* Header */}
        <div className="px-5 py-4 flex items-center gap-3 shrink-0"
             style={{ borderBottom: '1px solid var(--border)', background: 'var(--surface)' }}>
          <span className="text-2xl">
            {job?.status === 'completed' ? '✓' : job?.status === 'failed' ? '✕' : '⏳'}
          </span>
          <div className="flex-1">
            <div className="font-semibold" style={{ color: 'var(--text)' }}>{title}</div>
            <div className="text-xs" style={{ color: 'var(--mute)' }}>
              {subtitle ?? `Job #${jobId}`}
              {job?.status && <span className="ml-2" style={{ color: 'var(--accent-hi)' }}>· {job.status}</span>}
            </div>
          </div>
          <button onClick={onClose}
                  title="Kapat (iş arka planda devam eder)"
                  className="w-8 h-8 rounded-full flex items-center justify-center cursor-pointer"
                  style={{ color: 'var(--mute)' }}>×</button>
        </div>

        {/* Body */}
        <div className="flex-1 overflow-y-auto p-5 space-y-4">
          {err && (
            <div className="rounded-md px-3 py-2 text-xs"
                 style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}>
              {err}
            </div>
          )}

          {!job && !err && (
            <div className="text-sm text-center py-6" style={{ color: 'var(--mute)' }}>
              Yükleniyor…
            </div>
          )}

          {job && (
            <>
              {/* Progress bar */}
              {(job.status === 'queued' || job.status === 'running') && (
                <div className="space-y-2">
                  <div className="flex items-center justify-between text-xs">
                    <span style={{ color: 'var(--mute)' }}>
                      {job.status === 'queued' ? 'Kuyrukta bekliyor…' : (job.message || 'İşlem sürüyor…')}
                    </span>
                    <span className="font-mono" style={{ color: 'var(--text-2)' }}>
                      {job.progressCur.toLocaleString()} / {job.progressTot.toLocaleString()}
                      {job.progressTot > 0 && ` (${pct}%)`}
                      {eta && <span className="ml-2" style={{ color: 'var(--accent-hi)' }}>kalan {eta}</span>}
                    </span>
                  </div>
                  <div className="h-2 rounded-full overflow-hidden" style={{ background: 'var(--surface-hi)' }}>
                    <div className="h-full transition-all duration-300"
                         style={{ width: `${pct}%`, background: 'var(--accent)' }} />
                  </div>
                </div>
              )}

              {/* Failed */}
              {job.status === 'failed' && (
                <div className="rounded-xl p-4"
                     style={{ background: 'rgba(234,67,53,0.08)', border: '1px solid rgba(234,67,53,0.3)' }}>
                  <div className="text-sm font-semibold mb-1" style={{ color: '#ea4335' }}>İşlem başarısız</div>
                  <div className="text-xs font-mono" style={{ color: 'var(--text-2)' }}>{job.error}</div>
                </div>
              )}

              {/* Completed — custom result rendering or default */}
              {job.status === 'completed' && (
                renderResult
                  ? renderResult(job.result)
                  : (
                    <div className="rounded-xl p-4"
                         style={{ background: 'rgba(52,168,83,0.08)', border: '1px solid rgba(52,168,83,0.3)' }}>
                      <div className="text-sm font-semibold mb-2" style={{ color: '#34a853' }}>Tamamlandı</div>
                      <pre className="text-xs whitespace-pre-wrap font-mono" style={{ color: 'var(--text-2)' }}>
                        {JSON.stringify(job.result, null, 2)}
                      </pre>
                    </div>
                  )
              )}

              {/* Meta */}
              <div className="text-[10px] grid grid-cols-3 gap-2" style={{ color: 'var(--mute)' }}>
                <div>Başlatan: <strong style={{ color: 'var(--text-2)' }}>{job.createdBy}</strong></div>
                <div>Tip: <strong className="font-mono" style={{ color: 'var(--text-2)' }}>{job.type}</strong></div>
                <div>Başlangıç: <strong style={{ color: 'var(--text-2)' }}>{job.startedAt ? new Date(job.startedAt).toLocaleTimeString() : '-'}</strong></div>
              </div>
            </>
          )}
        </div>

        {/* Footer */}
        <div className="px-5 py-4 flex justify-end shrink-0"
             style={{ borderTop: '1px solid var(--border)', background: 'var(--surface)' }}>
          <button onClick={onClose}
                  className="px-4 py-2 rounded-lg text-sm cursor-pointer"
                  style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
            {finished ? 'Kapat' : 'Arka Planda Bırak'}
          </button>
        </div>
      </div>
    </div>
  )
}
