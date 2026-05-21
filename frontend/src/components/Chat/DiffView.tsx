import type { DiffLine } from '../../store'

// ── LCS-based line diff ───────────────────────────────────────────────────────

export function computeDiff(original: string, updated: string): DiffLine[] {
  const a = original === '' ? [] : original.split('\n')
  const b = updated   === '' ? [] : updated.split('\n')
  const m = a.length, n = b.length

  // Build LCS length table
  const dp: number[][] = Array.from({ length: m + 1 }, () => new Array(n + 1).fill(0))
  for (let i = 1; i <= m; i++)
    for (let j = 1; j <= n; j++)
      dp[i][j] = a[i-1] === b[j-1] ? dp[i-1][j-1] + 1 : Math.max(dp[i-1][j], dp[i][j-1])

  // Backtrack to produce diff lines
  const lines: DiffLine[] = []
  let i = m, j = n, newLine = n
  while (i > 0 || j > 0) {
    if (i > 0 && j > 0 && a[i-1] === b[j-1]) {
      lines.unshift({ type: 'unchanged', lineNumber: j, text: b[j-1] })
      i--; j--; newLine = j
    } else if (j > 0 && (i === 0 || dp[i][j-1] >= dp[i-1][j])) {
      lines.unshift({ type: 'added', lineNumber: j, text: b[j-1] })
      j--
    } else {
      lines.unshift({ type: 'removed', lineNumber: i, text: a[i-1] })
      i--
    }
  }
  return lines
}

// ── Render ────────────────────────────────────────────────────────────────────

interface Props {
  lines: DiffLine[]
}

export default function DiffView({ lines }: Props) {
  // Show only changed lines + 3 lines context around them
  const CONTEXT = 3
  const changed = new Set(lines.map((l, i) => l.type !== 'unchanged' ? i : -1).filter(i => i >= 0))
  const visible = new Set<number>()
  changed.forEach(idx => {
    for (let k = Math.max(0, idx - CONTEXT); k <= Math.min(lines.length - 1, idx + CONTEXT); k++)
      visible.add(k)
  })

  let prevIdx = -2
  return (
    <pre className="text-xs font-mono leading-5 overflow-x-auto whitespace-pre select-text"
         style={{ color: 'var(--text)' }}>
      {lines.map((line, idx) => {
        if (!visible.has(idx)) return null
        const sep = idx > prevIdx + 1
        prevIdx = idx
        return (
          <div key={idx}>
            {sep && idx > 0 && (
              <div className="py-0.5 px-2 text-[10px]" style={{ color: 'var(--mute)', background: 'var(--surface-2)' }}>
                ···
              </div>
            )}
            <div
              className="flex min-w-0"
              style={{
                background: line.type === 'added'
                  ? 'rgba(52,168,83,0.18)'
                  : line.type === 'removed'
                    ? 'rgba(234,67,53,0.18)'
                    : 'transparent',
              }}
            >
              <span
                className="select-none w-6 text-right shrink-0 pr-1"
                style={{ color: line.type === 'added' ? '#34a853' : line.type === 'removed' ? '#ea4335' : 'var(--mute)' }}
              >
                {line.type === 'added' ? '+' : line.type === 'removed' ? '−' : ' '}
              </span>
              <span className="pl-1 break-all">{line.text}</span>
            </div>
          </div>
        )
      })}
    </pre>
  )
}
