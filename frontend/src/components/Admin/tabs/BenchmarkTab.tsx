import { useCallback, useEffect, useMemo, useState } from 'react'
import { runBenchmark, listBenchmarks } from '../../../api/admin'
import type { BenchmarkResult } from '../../../api/admin'

const BENCH_MODELS = ['chat', 'code', 'reason'] as const
const BENCH_PRESETS = [1, 5, 10, 25, 50, 100] as const
const DEFAULT_PROMPT = 'Yapay zeka ve makine öğrenmesi arasındaki farkı 3 cümle ile özetle.'

export default function BenchmarkTab() {
  const [model,       setModel]       = useState<string>('chat')
  const [n,           setN]           = useState<number>(10)
  const [prompt,      setPrompt]      = useState<string>(DEFAULT_PROMPT)
  const [maxTokens,   setMaxTokens]   = useState<number>(150)
  const [temperature, setTemperature] = useState<number>(0.4)
  const [label,       setLabel]       = useState<string>('')
  const [running,     setRunning]     = useState<boolean>(false)
  const [latest,      setLatest]      = useState<BenchmarkResult | null>(null)
  const [history,     setHistory]     = useState<BenchmarkResult[]>([])
  const [error,       setError]       = useState<string | null>(null)

  const reloadHistory = useCallback(async () => {
    try { setHistory(await listBenchmarks(undefined, 30)) }
    catch (e: any) { setError(e.message) }
  }, [])

  useEffect(() => { reloadHistory() }, [reloadHistory])

  const onRun = async () => {
    if (!prompt.trim()) { setError('prompt zorunlu'); return }
    setRunning(true); setError(null); setLatest(null)
    try {
      const res = await runBenchmark({
        model, concurrency: n, prompt, maxTokens, temperature,
        label: label.trim() || undefined,
      })
      setLatest(res)
      await reloadHistory()
    } catch (e: any) { setError(e.message) }
    finally { setRunning(false) }
  }

  // Comparison: previous result with same model+concurrency
  const previous = useMemo(() => {
    if (!latest) return null
    return history.find(h => h.id !== latest.id && h.model === latest.model && h.concurrency === latest.concurrency) ?? null
  }, [latest, history])

  const fmt = (n: number, p = 1) => n.toFixed(p)

  return (
    <div className="space-y-4">
      <div className="flex items-start justify-between flex-wrap gap-2">
        <div>
          <h2 className="text-base font-semibold" style={{ color: 'var(--text)' }}>
            🧪 LLM Benchmark
          </h2>
          <p className="text-xs mt-0.5" style={{ color: 'var(--mute)' }}>
            N paralel istek atar → TTFT p50/p95, tok/s (per-stream + aggregate), wall süresi ölçer. Model değişikliğinden önce/sonra karşılaştırma yapmak için ideal.
          </p>
        </div>
      </div>

      {/* Config form */}
      <div className="rounded-xl p-4 space-y-3"
           style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
        <div className="grid grid-cols-1 md:grid-cols-4 gap-3">
          <label className="block">
            <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Model</div>
            <select value={model} onChange={e => setModel(e.target.value)}
                    className="w-full rounded-md px-2 py-1.5 text-sm cursor-pointer outline-none"
                    style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}>
              {BENCH_MODELS.map(m => <option key={m} value={m}>{m}</option>)}
            </select>
          </label>
          <label className="block">
            <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Eş zamanlı (N) — 1..200</div>
            <div className="flex items-center gap-2">
              <input type="number" min={1} max={200} value={n}
                     onChange={e => setN(Math.max(1, Math.min(200, parseInt(e.target.value) || 1)))}
                     className="w-24 rounded-md px-2 py-1.5 text-sm outline-none"
                     style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
              <div className="flex gap-1">
                {BENCH_PRESETS.map(p => (
                  <button key={p} onClick={() => setN(p)} type="button"
                          className="px-1.5 py-0.5 rounded text-[10px] cursor-pointer"
                          style={{
                            background: n === p ? 'var(--accent)' : 'var(--surface-2)',
                            color:      n === p ? '#0b1929' : 'var(--text-2)',
                          }}>{p}</button>
                ))}
              </div>
            </div>
          </label>
          <label className="block">
            <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Max Tokens</div>
            <input type="number" min={1} max={2000} value={maxTokens}
                   onChange={e => setMaxTokens(parseInt(e.target.value) || 150)}
                   className="w-full rounded-md px-2 py-1.5 text-sm outline-none"
                   style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
          </label>
          <label className="block">
            <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Temperature</div>
            <input type="number" min={0} max={2} step={0.1} value={temperature}
                   onChange={e => setTemperature(parseFloat(e.target.value) || 0)}
                   className="w-full rounded-md px-2 py-1.5 text-sm outline-none"
                   style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
          </label>
        </div>

        <label className="block">
          <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Prompt</div>
          <textarea value={prompt} onChange={e => setPrompt(e.target.value)} rows={2}
                    className="w-full rounded-md px-3 py-2 text-sm outline-none resize-none"
                    style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
        </label>

        <label className="block">
          <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Etiket (opsiyonel) — örn: &quot;Gemma 4 → Gemma 4.1 öncesi&quot;</div>
          <input value={label} onChange={e => setLabel(e.target.value)}
                 placeholder="Before/after model swap, kontrolün adı, vb."
                 className="w-full rounded-md px-2 py-1.5 text-sm outline-none"
                 style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
        </label>

        <div className="flex items-center gap-3">
          <button onClick={onRun} disabled={running}
                  className="px-4 py-2 rounded-lg text-sm font-semibold cursor-pointer disabled:opacity-50"
                  style={{ background: 'var(--accent)', color: '#0b1929' }}>
            {running ? `⏱ Çalışıyor… (${n} paralel istek)` : `🚀 Çalıştır`}
          </button>
          {running && (
            <span className="text-xs" style={{ color: 'var(--mute)' }}>
              N={n} istek aynı anda gönderildi — bitince sonuçlar aşağıda görünecek (genelde 5-30 sn).
            </span>
          )}
        </div>
      </div>

      {error && (
        <div className="rounded px-3 py-2 text-xs"
             style={{ background: 'rgba(234,67,53,0.12)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}>
          {error}
        </div>
      )}

      {/* Latest result */}
      {latest && (
        <div className="rounded-xl p-4 space-y-3"
             style={{ background: 'var(--surface)', border: '1px solid rgba(138,180,248,0.4)' }}>
          <div className="flex items-center justify-between flex-wrap gap-2">
            <div>
              <div className="font-semibold text-sm" style={{ color: 'var(--text)' }}>
                ✓ Son ölçüm — <span style={{ color: 'var(--accent-hi)' }}>{latest.model}</span> · N={latest.concurrency}
                {latest.label && <span className="ml-2 text-xs" style={{ color: 'var(--mute)' }}>· {latest.label}</span>}
              </div>
              <div className="text-[11px]" style={{ color: 'var(--mute)' }}>
                {new Date(latest.ts).toLocaleString()} · {latest.createdBy}
              </div>
            </div>
            <div className="text-[11px]" style={{ color: latest.failed > 0 ? '#ea4335' : '#34a853' }}>
              {latest.success}/{latest.success + latest.failed} başarılı
            </div>
          </div>

          <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
            <Metric label="Wall" value={`${fmt(latest.wallSeconds)}s`} sub="Toplam süre" />
            <Metric label="Aggregate" value={`${fmt(latest.tpsAggregate)} tok/s`}
                    sub={`${latest.totalTokens} token toplam`} accent />
            <Metric label="TTFT p50" value={`${fmt(latest.ttftP50Ms, 0)}ms`}
                    sub={`p95 ${fmt(latest.ttftP95Ms, 0)}ms`} />
            <Metric label="Per-stream tok/s" value={`${fmt(latest.tpsPerStreamP50)}`}
                    sub={`p95 ${fmt(latest.tpsPerStreamP95)}`} />
          </div>

          {/* Diff vs previous */}
          {previous && (
            <div className="text-[11px] pt-2"
                 style={{ borderTop: '1px solid var(--border)', color: 'var(--mute)' }}>
              <span>vs önceki ({new Date(previous.ts).toLocaleString()}): </span>
              <DiffChip label="aggregate" cur={latest.tpsAggregate}    prev={previous.tpsAggregate}    suffix="tok/s" higherBetter />
              <DiffChip label="ttft p50"  cur={latest.ttftP50Ms}       prev={previous.ttftP50Ms}       suffix="ms" higherBetter={false} />
              <DiffChip label="ttft p95"  cur={latest.ttftP95Ms}       prev={previous.ttftP95Ms}       suffix="ms" higherBetter={false} />
              <DiffChip label="per-stream p50" cur={latest.tpsPerStreamP50} prev={previous.tpsPerStreamP50} suffix="" higherBetter />
            </div>
          )}
        </div>
      )}

      {/* History */}
      <div className="rounded-xl overflow-hidden"
           style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
        <div className="px-3 py-2 text-xs font-semibold"
             style={{ background: 'var(--surface-hi)', color: 'var(--mute)', borderBottom: '1px solid var(--border)' }}>
          Geçmiş ({history.length})
        </div>
        <table className="w-full text-xs">
          <thead style={{ color: 'var(--mute)' }}>
            <tr style={{ borderBottom: '1px solid var(--border)' }}>
              <th className="px-3 py-2 text-left font-medium">Zaman</th>
              <th className="px-3 py-2 text-left font-medium">Model</th>
              <th className="px-3 py-2 text-right font-medium">N</th>
              <th className="px-3 py-2 text-right font-medium">Wall</th>
              <th className="px-3 py-2 text-right font-medium">TTFT p50/p95</th>
              <th className="px-3 py-2 text-right font-medium">tok/s stream</th>
              <th className="px-3 py-2 text-right font-medium">tok/s agg</th>
              <th className="px-3 py-2 text-right font-medium">Başarı</th>
              <th className="px-3 py-2 text-left font-medium">Etiket</th>
            </tr>
          </thead>
          <tbody>
            {history.length === 0 && (
              <tr><td colSpan={9} className="px-3 py-6 text-center" style={{ color: 'var(--mute)' }}>
                Henüz çalıştırılmamış. Yukarıdaki form ile başla.
              </td></tr>
            )}
            {history.map(h => (
              <tr key={h.id} style={{ borderBottom: '1px solid var(--border)' }}>
                <td className="px-3 py-1.5 font-mono text-[10px]" style={{ color: 'var(--mute)' }}>
                  {new Date(h.ts).toLocaleString()}
                </td>
                <td className="px-3 py-1.5" style={{ color: 'var(--accent-hi)' }}>{h.model}</td>
                <td className="px-3 py-1.5 text-right font-mono" style={{ color: 'var(--text)' }}>{h.concurrency}</td>
                <td className="px-3 py-1.5 text-right font-mono" style={{ color: 'var(--mute)' }}>{fmt(h.wallSeconds)}s</td>
                <td className="px-3 py-1.5 text-right font-mono" style={{ color: 'var(--mute)' }}>
                  {fmt(h.ttftP50Ms, 0)} / {fmt(h.ttftP95Ms, 0)} ms
                </td>
                <td className="px-3 py-1.5 text-right font-mono" style={{ color: 'var(--mute)' }}>
                  {fmt(h.tpsPerStreamP50)} / {fmt(h.tpsPerStreamP95)}
                </td>
                <td className="px-3 py-1.5 text-right font-mono font-semibold" style={{ color: '#34a853' }}>{fmt(h.tpsAggregate)}</td>
                <td className="px-3 py-1.5 text-right" style={{ color: h.failed > 0 ? '#ea4335' : 'var(--mute)' }}>
                  {h.success}/{h.success + h.failed}
                </td>
                <td className="px-3 py-1.5 text-xs" style={{ color: 'var(--mute)' }}>
                  {h.label ?? '—'}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function Metric({ label, value, sub, accent }: { label: string; value: string; sub?: string; accent?: boolean }) {
  return (
    <div className="rounded-lg p-3" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
      <div className="text-[10px] uppercase tracking-wider" style={{ color: 'var(--mute)' }}>{label}</div>
      <div className="text-xl font-bold mt-0.5" style={{ color: accent ? 'var(--accent-hi)' : 'var(--text)' }}>{value}</div>
      {sub && <div className="text-[10px] mt-0.5" style={{ color: 'var(--mute)' }}>{sub}</div>}
    </div>
  )
}

function DiffChip({ label, cur, prev, suffix, higherBetter }:
  { label: string; cur: number; prev: number; suffix: string; higherBetter: boolean }) {
  if (prev <= 0) return null
  const diff = cur - prev
  const pct  = (diff / prev) * 100
  const better = higherBetter ? diff >= 0 : diff <= 0
  const color = Math.abs(pct) < 5 ? 'var(--mute)' : better ? '#34a853' : '#ea4335'
  const arrow = diff > 0 ? '↑' : diff < 0 ? '↓' : '·'
  return (
    <span className="ml-2 px-1.5 py-0.5 rounded font-mono text-[10px]"
          style={{ background: 'var(--surface-2)', color, border: `1px solid ${color}40` }}>
      {label} {arrow} {pct.toFixed(1)}%{suffix && ` (${cur.toFixed(0)}${suffix})`}
    </span>
  )
}
