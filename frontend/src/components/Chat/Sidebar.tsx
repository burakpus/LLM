import { useState, useEffect, useRef } from 'react'
import type { KeyboardEvent } from 'react'
import { useStore, t } from '../../store'

export default function Sidebar() {
  const store = useStore()
  const [query, setQuery] = useState('')
  const [editingId, setEditingId] = useState<string | null>(null)
  const [editingValue, setEditingValue] = useState('')
  const editRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (editingId) editRef.current?.focus()
  }, [editingId])

  const lc = query.trim().toLowerCase()
  const sorted = [...store.conversations]
    .sort((a, b) => b.updatedAt - a.updatedAt)
    .filter(c => {
      if (!lc) return true
      if (c.title.toLowerCase().includes(lc)) return true
      return c.messages.some(m => m.content.toLowerCase().includes(lc))
    })

  const fmt = (ts: number) => {
    const d = new Date(ts)
    const now = new Date()
    if (d.toDateString() === now.toDateString()) {
      return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    }
    if (d.getFullYear() === now.getFullYear()) {
      return d.toLocaleDateString([], { month: 'short', day: 'numeric' })
    }
    return d.toLocaleDateString([], { year: 'numeric', month: 'short' })
  }

  const beginRename = (id: string, current: string) => {
    setEditingId(id)
    setEditingValue(current)
  }

  const commitRename = () => {
    if (editingId) {
      const v = editingValue.trim()
      if (v) store.renameConversation(editingId, v)
    }
    setEditingId(null)
  }

  const onEditKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') { e.preventDefault(); commitRename() }
    if (e.key === 'Escape') { e.preventDefault(); setEditingId(null) }
  }

  return (
    <aside
      className="transition-[width] duration-300 overflow-hidden flex flex-col shrink-0"
      style={{
        width: store.historyOpen ? '260px' : '0',
        background: 'var(--surface)',
      }}
    >
      {/* Top controls */}
      <div className="p-3 space-y-2 shrink-0">
        <button
          onClick={() => store.newConversation()}
          className="w-full flex items-center gap-3 px-3 py-2.5 rounded-full text-sm font-medium transition cursor-pointer"
          style={{
            background: 'var(--surface-hi)',
            color: 'var(--text)',
          }}
          title={t('newChat')}
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round"
              d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
          </svg>
          <span>{t('newChat')}</span>
        </button>

        <div className="relative">
          <svg className="w-3.5 h-3.5 absolute left-3 top-1/2 -translate-y-1/2"
               style={{ color: 'var(--mute-2)' }}
               fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round"
              d="M21 21l-4.35-4.35M17 11A6 6 0 115 11a6 6 0 0112 0z"/>
          </svg>
          <input
            value={query}
            onChange={e => setQuery(e.target.value)}
            placeholder={t('search')}
            className="w-full rounded-full pl-9 pr-3 py-1.5 text-xs outline-none transition"
            style={{ background: 'var(--bg)', border: '1px solid transparent', color: 'var(--text)' }}
          />
        </div>
      </div>

      {/* Recents label */}
      <div className="px-4 pt-2 pb-1 text-[11px] font-medium tracking-wide shrink-0"
           style={{ color: 'var(--mute)' }}>
        Recent
      </div>

      {/* List */}
      <div className="flex-1 overflow-y-auto scrollbar-thin px-2 pb-2">
        {store.conversations.length === 0 && (
          <p className="text-xs text-center py-8 px-3" style={{ color: 'var(--mute-2)' }}>
            {t('noConvs')}
          </p>
        )}
        {store.conversations.length > 0 && sorted.length === 0 && (
          <p className="text-xs text-center py-4" style={{ color: 'var(--mute-2)' }}>
            {t('noMatch')}
          </p>
        )}

        {sorted.map(conv => {
          const active = conv.id === store.currentId
          return (
            <div
              key={conv.id}
              onClick={() => store.loadConversation(conv.id)}
              className="group rounded-full px-3 py-2 cursor-pointer transition flex items-center gap-2"
              style={{
                background: active ? 'var(--surface-hi)' : 'transparent',
              }}
              onMouseEnter={e => {
                if (!active) (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'
              }}
              onMouseLeave={e => {
                if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent'
              }}
            >
              {conv.generating && (
                <span className="w-1.5 h-1.5 rounded-full shrink-0"
                      style={{ background: '#34a853', animation: 'pulse 1.5s infinite' }} />
              )}

              <div className="min-w-0 flex-1">
                {editingId === conv.id ? (
                  <input
                    ref={editRef}
                    value={editingValue}
                    onChange={e => setEditingValue(e.target.value)}
                    onBlur={commitRename}
                    onKeyDown={onEditKey}
                    onClick={e => e.stopPropagation()}
                    className="w-full rounded px-1.5 py-0.5 text-sm outline-none"
                    style={{ background: 'var(--bg)', border: '1px solid var(--accent)', color: 'var(--text)' }}
                  />
                ) : (
                  <div
                    className="text-sm truncate select-none"
                    style={{ color: 'var(--text)' }}
                    onDoubleClick={e => { e.stopPropagation(); beginRename(conv.id, conv.title) }}
                    title={conv.title || 'Untitled'}
                  >
                    {conv.title || 'Untitled'}
                  </div>
                )}
                <div className="text-[10px] mt-0.5 hidden group-hover:flex gap-2 items-center"
                     style={{ color: 'var(--mute-2)' }}>
                  <span>{fmt(conv.updatedAt)}</span>
                  {conv.totalTokens > 0 && (
                    <span>{conv.totalTokens > 1000
                      ? `${(conv.totalTokens/1000).toFixed(1)}k`
                      : conv.totalTokens} tok</span>
                  )}
                </div>
              </div>

              <button
                onClick={e => {
                  e.stopPropagation()
                  if (confirm('Delete this conversation?')) {
                    store.deleteConversation(conv.id)
                  }
                }}
                className="opacity-0 group-hover:opacity-100 p-1 transition shrink-0 rounded-full"
                style={{ color: 'var(--mute)' }}
                title="Delete"
              >
                <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round"
                    d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6M1 7h22M9 7V4a1 1 0 011-1h4a1 1 0 011 1v3"/>
                </svg>
              </button>
            </div>
          )
        })}
      </div>

      <div className="px-4 py-3 text-[10px] shrink-0"
           style={{ color: 'var(--mute-2)' }}>
        {store.conversations.length} {t('conversations')}
      </div>
    </aside>
  )
}
