import type { ConvStats } from '../../store'

interface Props {
  stats:  ConvStats | null
  model?: string | null
}

export default function StatsBar({ stats, model }: Props) {
  if (!stats && !model) return null

  const ttftSec    = stats?.ttft != null ? (Math.round(stats.ttft) / 1000).toFixed(1) : null
  const elapsedSec = stats ? (stats.elapsed / 1000).toFixed(1) : null

  return (
    <div
      className="px-5 pt-1 pb-0 mx-auto w-full flex items-center gap-2"
      style={{
        maxWidth: '760px',
        fontSize: '11px',
        color: 'var(--mute-2)',
      }}
    >
      {stats && <>
        {ttftSec && <span>TTFT {ttftSec}s</span>}
        {ttftSec && stats.tokensPerSec != null && <span>•</span>}
        {stats.tokensPerSec != null && <span>{stats.tokensPerSec} tok/s</span>}
        <span>•</span>
        <span>{stats.tokens} tok</span>
        <span>•</span>
        <span>{elapsedSec ?? '0.0'}s</span>
        {stats.finishReason && stats.finishReason !== 'stop' && (
          <>
            <span>•</span>
            <span style={{ color: stats.finishReason === 'length' ? '#f59e0b' : 'var(--mute-2)' }}>
              {stats.finishReason === 'length' ? '⚠ limit' : stats.finishReason}
            </span>
          </>
        )}
      </>}
      {model && (
        <>
          {stats && <span>•</span>}
          <span className="font-mono">{model}</span>
        </>
      )}
    </div>
  )
}
