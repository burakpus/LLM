import { useState, useEffect, useRef } from 'react'
import type { KeyboardEvent } from 'react'
import { useStore, t } from '../../store'

function ProjectButton() {
  const store = useStore()
  const [input, setInput] = useState('')
  const [open,  setOpen]  = useState(false)
  const proj = store.project

  if (proj.projectId) return (
    <div className="flex items-center gap-1.5 px-3 py-2 rounded-full text-xs"
         style={{ background: 'rgba(138,180,248,0.12)', border: '1px solid rgba(138,180,248,0.3)', color: 'var(--accent-hi)' }}>
      <svg className="w-3.5 h-3.5 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
        <path strokeLinecap="round" strokeLinejoin="round" d="M3 7a2 2 0 012-2h4l2 2h8a2 2 0 012 2v9a2 2 0 01-2 2H5a2 2 0 01-2-2V7z"/>
      </svg>
      <span className="flex-1 truncate font-medium">{proj.projectId}</span>
      <button onClick={() => store.setProjectId(null)} className="cursor-pointer" title="Kapat">✕</button>
    </div>
  )

  if (open) return (
    <form onSubmit={e => { e.preventDefault(); if (input.trim()) { store.setProjectId(input.trim()); setOpen(false); setInput('') } }}
          className="flex gap-1">
      <input autoFocus value={input} onChange={e => setInput(e.target.value)}
             placeholder="proje-adı" className="flex-1 px-2 py-1.5 rounded-lg text-xs outline-none"
             style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
      <button type="submit" className="px-2 py-1.5 rounded-lg text-xs cursor-pointer"
              style={{ background: 'var(--accent)', color: '#0b1929' }}>✓</button>
      <button type="button" onClick={() => setOpen(false)} className="px-2 py-1.5 rounded-lg text-xs cursor-pointer"
              style={{ background: 'var(--surface-hi)', color: 'var(--mute)' }}>✕</button>
    </form>
  )

  return (
    <button onClick={() => setOpen(true)}
            className="w-full flex items-center gap-3 px-3 py-2 rounded-full text-xs transition cursor-pointer"
            style={{ background: 'transparent', border: '1px dashed var(--border)', color: 'var(--mute)' }}>
      <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
        <path strokeLinecap="round" strokeLinejoin="round" d="M3 7a2 2 0 012-2h4l2 2h8a2 2 0 012 2v9a2 2 0 01-2 2H5a2 2 0 01-2-2V7z"/>
      </svg>
      <span>Yeni Proje</span>
    </button>
  )
}

export default function Sidebar() {
  const store = useStore()
  const [query,        setQuery]        = useState('')
  const [editingId,    setEditingId]    = useState<string | null>(null)
  const [editingValue, setEditingValue] = useState('')
  const [showArchive,  setShowArchive]  = useState(false)
  const editRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (editingId) editRef.current?.focus()
  }, [editingId])

  const lc = query.trim().toLowerCase()
  const match = (c: any) => {
    if (lc) {
      if (!c.title.toLowerCase().includes(lc) &&
          !c.messages?.some((m: any) => m.content?.toLowerCase?.()?.includes(lc))) return false
    }
    return true
  }

  const byDate = (a: any, b: any) => b.updatedAt - a.updatedAt
  const starred  = store.conversations.filter(c => c.starred && !c.archived && match(c)).sort(byDate)
  const regular  = store.conversations.filter(c => !c.starred && !c.archived && match(c)).sort(byDate)
  const archived = store.conversations.filter(c => c.archived && match(c)).sort(byDate)

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
    <>
      {/* Backdrop — click outside to close */}
      <div
        className="fixed inset-0 z-30 transition-opacity duration-300"
        style={{
          background: 'rgba(0,0,0,0.4)',
          opacity: store.historyOpen ? 1 : 0,
          pointerEvents: store.historyOpen ? 'auto' : 'none',
        }}
        onClick={store.toggleHistory}
      />

      {/* Sidebar — always fixed overlay, never pushes content */}
      <aside
        className="fixed top-0 left-0 h-full flex flex-col z-40"
        style={{
          width: '260px',
          background: 'var(--surface)',
          transform: store.historyOpen ? 'translateX(0)' : 'translateX(-100%)',
          transition: 'transform 0.28s cubic-bezier(0.4,0,0.2,1)',
          boxShadow: store.historyOpen ? '4px 0 24px rgba(0,0,0,0.3)' : 'none',
        }}
      >
      {/* Top controls */}
      <div className="p-3 space-y-2 shrink-0">
        <button
          onClick={() => store.newConversation()}
          className="w-full flex items-center gap-3 px-3 py-2.5 rounded-full text-sm font-medium transition cursor-pointer"
          style={{ background: 'var(--surface-hi)', color: 'var(--text)' }}
          title={t('newChat')}
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round"
              d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
          </svg>
          <span>{t('newChat')}</span>
        </button>
        {/* New project */}
        <ProjectButton />

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

      {/* Conversation list */}
      <div className="flex-1 overflow-y-auto scrollbar-thin px-2 pb-2">

        {/* Favorites */}
        {starred.length > 0 && (
          <>
            <SectionLabel icon="★" label="Favoriler" />
            {starred.map(c => <ConvRow key={c.id} conv={c} store={store}
              editingId={editingId} editingValue={editingValue} setEditingValue={setEditingValue}
              editRef={editRef} beginRename={beginRename} commitRename={commitRename}
              onEditKey={onEditKey} fmt={fmt} />)}
          </>
        )}

        {/* Regular */}
        {(starred.length > 0 || archived.length > 0) && regular.length > 0 && (
          <SectionLabel icon="" label="Sohbetler" />
        )}
        {regular.map(c => <ConvRow key={c.id} conv={c} store={store}
          editingId={editingId} editingValue={editingValue} setEditingValue={setEditingValue}
          editRef={editRef} beginRename={beginRename} commitRename={commitRename}
          onEditKey={onEditKey} fmt={fmt} />)}

        {/* Empty state */}
        {starred.length === 0 && regular.length === 0 && archived.length === 0 && (
          <p className="text-xs text-center py-8 px-3" style={{ color: 'var(--mute-2)' }}>
            {lc ? t('noMatch') : t('noConvs')}
          </p>
        )}

        {/* Archive toggle */}
        {archived.length > 0 && (
          <div className="mt-2">
            <button
              onClick={() => setShowArchive(v => !v)}
              className="w-full flex items-center gap-2 px-3 py-1.5 rounded-lg text-xs cursor-pointer transition"
              style={{ color: 'var(--mute)', background: showArchive ? 'var(--surface-hi)' : 'transparent' }}
            >
              <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round"
                  d="M5 8h14M5 8a2 2 0 110-4h14a2 2 0 110 4M5 8v10a2 2 0 002 2h10a2 2 0 002-2V8m-9 4h4"/>
              </svg>
              Arşiv ({archived.length})
              <span className="ml-auto">{showArchive ? '▲' : '▼'}</span>
            </button>
            {showArchive && archived.map(c => (
              <ConvRow key={c.id} conv={c} store={store}
                editingId={editingId} editingValue={editingValue} setEditingValue={setEditingValue}
                editRef={editRef} beginRename={beginRename} commitRename={commitRename}
                onEditKey={onEditKey} fmt={fmt} />
            ))}
          </div>
        )}
      </div>

    </aside>
    </>
  )
}

// ── Section label ─────────────────────────────────────────────────────────────
function SectionLabel({ icon, label }: { icon: string; label: string }) {
  return (
    <div className="px-3 pt-3 pb-1 flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-wider"
         style={{ color: 'var(--mute)' }}>
      {icon && <span>{icon}</span>}
      {label}
    </div>
  )
}

// ── Single conversation row ───────────────────────────────────────────────────
function ConvRow({ conv, store, editingId, editingValue, setEditingValue, editRef,
  beginRename, commitRename, onEditKey, fmt }: any) {
  const active = conv.id === store.currentId
  return (
    <div
      onClick={() => store.loadConversation(conv.id)}
      className="group rounded-lg px-3 py-2 cursor-pointer transition flex items-center gap-2 mb-0.5"
      style={{ background: active ? 'var(--surface-hi)' : 'transparent' }}
      onMouseEnter={e => { if (!active) (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)' }}
      onMouseLeave={e => { if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent' }}
    >
      {/* Generating dot */}
      {conv.generating && (
        <span className="w-1.5 h-1.5 rounded-full shrink-0"
              style={{ background: '#34a853', animation: 'pulse 1.5s infinite' }} />
      )}

      <div className="min-w-0 flex-1">
        {/* Project badge */}
        {conv.settings?.projectId && (
          <div className="flex items-center gap-1 mb-0.5">
            <svg className="w-2.5 h-2.5 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}
                 style={{ color: 'var(--accent)' }}>
              <path strokeLinecap="round" strokeLinejoin="round"
                d="M3 7a2 2 0 012-2h4l2 2h8a2 2 0 012 2v9a2 2 0 01-2 2H5a2 2 0 01-2-2V7z"/>
            </svg>
            <span className="text-[9px] truncate" style={{ color: 'var(--accent)' }}>
              {conv.settings.projectId}
            </span>
          </div>
        )}

        {editingId === conv.id ? (
          <input ref={editRef} value={editingValue}
                 onChange={e => setEditingValue(e.target.value)}
                 onBlur={commitRename} onKeyDown={onEditKey}
                 onClick={e => e.stopPropagation()}
                 className="w-full rounded px-1 py-0.5 text-sm outline-none"
                 style={{ background: 'var(--bg)', border: '1px solid var(--accent)', color: 'var(--text)' }} />
        ) : (
          <div className="text-sm truncate select-none" style={{ color: 'var(--text)' }}
               onDoubleClick={e => { e.stopPropagation(); beginRename(conv.id, conv.title) }}
               title={conv.title || 'Untitled'}>
            {conv.title || 'Untitled'}
          </div>
        )}

        <div className="text-[10px] mt-0.5 hidden group-hover:flex gap-2"
             style={{ color: 'var(--mute-2)' }}>
          <span>{fmt(conv.updatedAt)}</span>
          {conv.totalTokens > 0 && (
            <span>{conv.totalTokens > 1000
              ? `${(conv.totalTokens/1000).toFixed(1)}k` : conv.totalTokens} tok</span>
          )}
        </div>
      </div>

      {/* Action buttons (hover) */}
      <div className="opacity-0 group-hover:opacity-100 flex items-center gap-0.5 shrink-0 transition">
        {/* Star */}
        <button onClick={e => { e.stopPropagation(); store.starConversation(conv.id, !conv.starred) }}
                className="p-1 rounded-full transition cursor-pointer"
                style={{ color: conv.starred ? '#fbbf24' : 'var(--mute)' }}
                title={conv.starred ? 'Favoriden çıkar' : 'Favorile'}>
          <svg className="w-3.5 h-3.5" viewBox="0 0 24 24"
               fill={conv.starred ? 'currentColor' : 'none'} stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round"
              d="M11.049 2.927c.3-.921 1.603-.921 1.902 0l1.519 4.674a1 1 0 00.95.69h4.915c.969 0 1.371 1.24.588 1.81l-3.976 2.888a1 1 0 00-.363 1.118l1.518 4.674c.3.922-.755 1.688-1.538 1.118l-3.976-2.888a1 1 0 00-1.176 0l-3.976 2.888c-.783.57-1.838-.197-1.538-1.118l1.518-4.674a1 1 0 00-.363-1.118l-3.976-2.888c-.784-.57-.38-1.81.588-1.81h4.914a1 1 0 00.951-.69l1.519-4.674z"/>
          </svg>
        </button>
        {/* Archive */}
        <button onClick={e => { e.stopPropagation(); store.archiveConversation(conv.id, !conv.archived) }}
                className="p-1 rounded-full transition cursor-pointer"
                style={{ color: 'var(--mute)' }}
                title={conv.archived ? 'Arşivden çıkar' : 'Arşivle'}>
          <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round"
              d={conv.archived
                ? "M5 8h14M5 8a2 2 0 110-4h14a2 2 0 110 4M5 8v10a2 2 0 002 2h10a2 2 0 002-2V8M10 12h4"
                : "M5 8h14M5 8a2 2 0 110-4h14a2 2 0 110 4M5 8v10a2 2 0 002 2h10a2 2 0 002-2V8m-9 4h4"}/>
          </svg>
        </button>
        {/* Delete */}
        <button onClick={e => { e.stopPropagation(); if (confirm('Sohbeti sil?')) store.deleteConversation(conv.id) }}
                className="p-1 rounded-full transition cursor-pointer"
                style={{ color: 'var(--mute)' }}
                title="Sil">
          <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round"
              d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6M1 7h22M9 7V4a1 1 0 011-1h4a1 1 0 011 1v3"/>
          </svg>
        </button>
      </div>
    </div>
  )
}
