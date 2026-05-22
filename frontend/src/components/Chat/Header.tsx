import { useState, useRef, useEffect } from 'react'
import { useStore, t } from '../../store'
import { logout } from '../../api'
import type { Skill } from '../../api'
import SetLogo from '../SetLogo'
import { listSkillExamples } from '../../api/admin'

const MAX_SKILL_CHARS = 12000 // ~3000 tokens — keep context window headroom

async function fetchSkillPrompt(id: string): Promise<string> {
  const token = localStorage.getItem('setllm-token') ?? ''
  const r = await fetch(`/api/skills/${encodeURIComponent(id)}`, {
    headers: { Authorization: `Bearer ${token}` }
  })
  if (!r.ok) return ''
  const text = await r.text()
  if (text.length <= MAX_SKILL_CHARS) return text
  return text.slice(0, MAX_SKILL_CHARS) + '\n\n[...skill truncated to fit context window...]'
}

interface Props {
  skills:     Skill[]
  statusOk:   boolean | null
  generating: boolean
}

function SkillSelector({ skills }: { skills: Skill[] }) {
  const store = useStore()
  const conv  = store.currentConv()
  const [open, setOpen] = useState(false)
  const wrapRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const onClick = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', onClick)
    return () => document.removeEventListener('mousedown', onClick)
  }, [])

  const activeId   = conv?.settings.skillId ?? store.activeSkillId
  const activeName = conv?.settings.skillName ?? store.activeSkillName

  const setSkill = async (id: string | null, name: string | null, collection?: string | null) => {
    let systemPrompt = ''
    let skillExamples: { user: string; assistant: string }[] = []
    if (id) {
      [systemPrompt] = await Promise.all([
        fetchSkillPrompt(id).catch(() => ''),
      ])
      const rawExamples = await listSkillExamples(id).catch(() => [])
      skillExamples = rawExamples.map(e => ({ user: e.userMessage, assistant: e.assistantMessage }))
    }
    if (conv) store.updateConvSettings(conv.id, {
      skillId:         id,
      skillName:       name,
      skillCollection: collection ?? null,
      systemPrompt,
      skillExamples,
    })
    store.setSkill(id, name)
    setOpen(false)
  }

  return (
    <div ref={wrapRef} className="relative">
      <button
        onClick={() => setOpen(o => !o)}
        className={`pill ${activeId ? 'active' : ''}`}
      >
        <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round"
            d="M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m3.343-5.657l-.707-.707m2.828 9.9a5 5 0 117.072 0l-.548.547A3.374 3.374 0 0014 18.469V19a2 2 0 11-4 0v-.531c0-.895-.356-1.754-.988-2.386l-.548-.547z" />
        </svg>
        <span>{activeId ? activeName : t('skill')}</span>
        {activeId && conv?.settings.skillCollection && (
          <span className="text-[10px] opacity-60 ml-0.5">· {conv.settings.skillCollection}</span>
        )}
        {activeId && (conv?.settings.skillExamples?.length ?? 0) > 0 && (
          <span className="text-[9px] px-1 py-0.5 rounded-full ml-0.5"
                style={{ background: 'rgba(138,180,248,0.25)', color: 'var(--accent-hi)' }}
                title={`${conv?.settings.skillExamples?.length} few-shot örnek`}>
            {conv?.settings.skillExamples?.length}✦
          </span>
        )}
        {activeId && (
          <svg
            onClick={e => { e.stopPropagation(); setSkill(null, null) }}
            className="w-3 h-3 ml-1"
            fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={3}
          >
            <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
          </svg>
        )}
      </button>

      {open && (
        <div
          className="absolute right-0 top-full mt-2 w-72 rounded-xl shadow-2xl z-50 overflow-hidden"
          style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)' }}
        >
          <div className="px-3 py-2" style={{ borderBottom: '1px solid var(--border)' }}>
            <div className="text-[11px] uppercase tracking-wider font-semibold"
                 style={{ color: 'var(--mute)' }}>
              Modes
            </div>
          </div>
          <div className="max-h-64 overflow-y-auto scrollbar-thin">
            {skills.length === 0 && (
              <p className="text-xs text-center py-5 px-3" style={{ color: 'var(--mute-2)' }}>
                No skills found.
              </p>
            )}
            {skills.map(sk => {
              const active = activeId === sk.id
              return (
                <button
                  key={sk.id}
                  onClick={() => setSkill(sk.id, sk.name, sk.collection)}
                  className="w-full flex items-start gap-3 px-3 py-2.5 text-left transition cursor-pointer"
                  style={{
                    background: active ? 'rgba(138,180,248,0.15)' : 'transparent',
                    color: active ? 'var(--accent-hi)' : 'var(--text)',
                  }}
                >
                  <div className="w-7 h-7 rounded-lg flex items-center justify-center shrink-0 mt-0.5"
                       style={{ background: 'var(--surface-2)' }}>
                    <svg className="w-3.5 h-3.5" style={{ color: 'var(--accent)' }}
                         fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round"
                        d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                    </svg>
                  </div>
                  <div className="min-w-0 flex-1">
                    <div className="text-sm font-medium truncate">{sk.name}</div>
                    <div className="text-[10px] truncate mt-0.5" style={{ color: 'var(--mute-2)' }}>
                      {sk.description || sk.id}
                    </div>
                  </div>
                </button>
              )
            })}
          </div>
        </div>
      )}
    </div>
  )
}

export default function Header({ skills, statusOk }: Props) {
  const store = useStore()
  const conv  = store.currentConv()
  const settings = conv?.settings
  const [statusTooltip,  setStatusTooltip]  = useState(false)

  const statusClass = statusOk === true  ? 'ok'
                    : statusOk === false ? 'bad'
                    : ''
  // Detailed status for tooltip — prefer store.status (has "warming up" etc.)
  const statusDetail = store.status || (statusOk === true ? t('online') : statusOk === false ? t('offline') : t('checking'))

  const handleLogout = () => {
    logout()
    store.clearAuth()
  }

  const activeModel = settings?.model ?? store.activeModel

  return (
  <>
    <header
      className="h-14 flex items-center gap-2 px-3 shrink-0"
      style={{ background: 'var(--bg)' }}
    >
      {/* Hamburger */}
      <button
        onClick={store.toggleHistory}
        className="p-2 rounded-full transition cursor-pointer"
        style={{ color: 'var(--text-2)' }}
        title="Toggle sidebar"
        onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
        onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}
      >
        <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M4 6h16M4 12h16M4 18h16" />
        </svg>
      </button>

      {/* Logo + title */}
      <div className="flex items-center gap-2 ml-1">
        <SetLogo className="h-6 w-auto" />
        <span className="text-[15px] font-medium tracking-tight"
              style={{ color: 'var(--text)' }}>
          SET LLM
        </span>
      </div>

      {/* Agentic */}
      {settings?.agenticEnabled && (
        <span className="ml-2 text-[10px] font-semibold tracking-wider px-2 py-0.5 rounded-full"
              style={{ background: 'rgba(245,158,11,0.15)', color: '#f59e0b' }}>
          OTONOM
        </span>
      )}

      <div className="flex-1" />

      {/* Vertical divider */}
      <div className="h-5 mx-2" style={{ borderLeft: '1px solid var(--border)' }} />

      {/* User + status dot */}
      <div className="hidden md:flex items-center gap-1.5 relative"
           onMouseEnter={() => setStatusTooltip(true)}
           onMouseLeave={() => setStatusTooltip(false)}>
        {/* Status dot */}
        <span className={`status-dot ${statusClass}`} style={{ cursor: 'default' }} />
        <span className="text-[11px]" style={{ color: 'var(--mute)' }}>
          {store.auth.username}
        </span>
        {/* Tooltip — aşağıda göster (header'da yukarı yer yok) */}
        {statusTooltip && (
          <div className="absolute top-full left-0 mt-2 px-2.5 py-1.5 rounded-lg text-[11px] whitespace-nowrap z-50 pointer-events-none"
               style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text)', boxShadow: '0 4px 12px rgba(0,0,0,0.3)' }}>
            {/* Arrow pointing up */}
            <div className="absolute bottom-full left-3 w-0 h-0"
                 style={{ borderLeft: '4px solid transparent', borderRight: '4px solid transparent', borderBottom: '4px solid var(--border)' }} />
            {statusDetail}
          </div>
        )}
      </div>

      {/* Settings */}
      <button
        onClick={store.toggleSettings}
        className="p-2 rounded-full transition cursor-pointer"
        style={{ color: 'var(--text-2)' }}
        title={t('settings')}
        onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
        onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}
      >
        <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round"
            d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
          <path strokeLinecap="round" strokeLinejoin="round" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
        </svg>
      </button>

      {/* Logout */}
      <button
        onClick={handleLogout}
        className="p-2 rounded-full transition cursor-pointer"
        style={{ color: 'var(--text-2)' }}
        title={t('logout')}
        onMouseEnter={e => {
          (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'
          ;(e.currentTarget as HTMLElement).style.color = '#ea4335'
        }}
        onMouseLeave={e => {
          (e.currentTarget as HTMLElement).style.background = 'transparent'
          ;(e.currentTarget as HTMLElement).style.color = 'var(--text-2)'
        }}
      >
        <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round"
            d="M17 16l4-4m0 0l-4-4m4 4H7m6 4v1a3 3 0 01-3 3H6a3 3 0 01-3-3V7a3 3 0 013-3h4a3 3 0 013 3v1" />
        </svg>
      </button>
    </header>

  </>
  )
}
