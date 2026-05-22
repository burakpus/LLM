import type { ConvStats } from '../../store'

interface Props {
  stats: ConvStats | null
}

export default function StatsBar({ stats }: Props) {
  if (!stats) return null

  const ttftSec = stats.ttft != null ? (Math.round(stats.ttft) / 1000).toFixed(1) : null
  const elapsedSec = (stats.elapsed / 1000).toFixed(1)

  return (
    <div
      className="px-5 pt-1 pb-0 mx-auto w-full flex items-center gap-2"
      style={{
        maxWidth: '760px',
        fontSize: '11px',
        color: 'var(--mute-2)',
      }}
    >
      {ttftSec && <span>TTFT {ttftSec}s</span>}
      {ttftSec && stats.tokensPerSec != null && <span>•</span>}
      {stats.tokensPerSec != null && <span>{stats.tokensPerSec} tok/s</span>}
      <span>•</span>
      <span>{stats.tokens} tok</span>
      <span>•</span>
      <span>{elapsedSec}s</span>
      {/* Only show non-normal finish reasons — "stop" is expected, no need to display */}
      {stats.finishReason && stats.finishReason !== 'stop' && (
        <>
          <span>•</span>
          <span style={{ color: stats.finishReason === 'length' ? '#f59e0b' : 'var(--mute-2)' }}>
            {stats.finishReason === 'length' ? '⚠ limit' : stats.finishReason}
          </span>
        </>
      )}
    </div>
  )
}
