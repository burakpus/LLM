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
      {stats.finishReason && (
        <>
          <span>•</span>
          <span>{stats.finishReason}</span>
        </>
      )}
    </div>
  )
}
