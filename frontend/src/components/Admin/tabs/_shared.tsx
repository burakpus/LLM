// Shared helpers + tiny components used by multiple Admin tabs.
// Keep this file minimal — only put helpers here when 2+ tabs actually use them.

import { useEffect, useState } from 'react'

export function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / 1024 / 1024).toFixed(2)} MB`
}

export function formatDate(iso: string): string {
  try {
    const d = new Date(iso)
    return d.toLocaleString()
  } catch {
    return iso
  }
}

/** Default page-size choices for admin list views. */
export const PAGE_SIZE_OPTIONS = [25, 50, 100, 200, 500, 1000] as const
export const DEFAULT_PAGE_SIZE = 50

/**
 * Reusable "Top XXX" page-size selector. Shows label + dropdown matching admin styles.
 * Set `compact` to drop the label.
 */
export function PageSizeSelector({
  value, onChange, options = PAGE_SIZE_OPTIONS, compact = false, label = 'Sayfa boyutu',
}: {
  value:    number
  onChange: (n: number) => void
  options?: readonly number[]
  compact?: boolean
  label?:   string
}) {
  return (
    <label className="block">
      {!compact && (
        <div className="text-xs mb-1.5" style={{ color: 'var(--mute)' }}>{label}</div>
      )}
      <select
        value={value}
        onChange={e => onChange(parseInt(e.target.value, 10))}
        className="px-3 py-2 rounded-md text-sm outline-none cursor-pointer"
        style={{
          background: 'var(--input-bg)',
          border:     '1px solid var(--border)',
          color:      'var(--text)',
        }}
      >
        {options.map(n => <option key={n} value={n}>Top {n}</option>)}
      </select>
    </label>
  )
}

/**
 * Debounced text input — fires `onCommit(value)` after the user stops typing
 * for `delayMs` (default 350ms). Use for search boxes.
 */
export function DebouncedSearchInput({
  initial = '', onCommit, placeholder = 'Ara…', delayMs = 350, minLength = 0,
}: {
  initial?:     string
  onCommit:     (v: string) => void
  placeholder?: string
  delayMs?:     number
  minLength?:   number   // commit only when length >= minLength OR empty
}) {
  const [v, setV] = useState(initial)
  useEffect(() => {
    const t = setTimeout(() => {
      if (v.length === 0 || v.length >= minLength) onCommit(v)
    }, delayMs)
    return () => clearTimeout(t)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [v])
  return (
    <input
      value={v}
      onChange={e => setV(e.target.value)}
      placeholder={placeholder}
      className="px-3 py-2 rounded-md text-sm outline-none"
      style={{
        background: 'var(--input-bg)',
        border:     '1px solid var(--border)',
        color:      'var(--text)',
        minWidth:   '14rem',
      }}
    />
  )
}
