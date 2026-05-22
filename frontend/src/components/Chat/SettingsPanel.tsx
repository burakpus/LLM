import { useState, useEffect } from 'react'
import { useStore, t, defaultSettings } from '../../store'
import HelpModal from './HelpModal'

// ── Consistent toggle row ─────────────────────────────────────────────────────
function ToggleRow({
  label, checked, onChange,
}: { label: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <label className="flex items-center justify-between cursor-pointer py-2.5"
           style={{ borderBottom: '1px solid var(--border)' }}>
      <span className="text-sm" style={{ color: 'var(--text)' }}>{label}</span>
      <input type="checkbox" checked={checked} onChange={e => onChange(e.target.checked)}
             className="cursor-pointer" />
    </label>
  )
}

// ── Section label ─────────────────────────────────────────────────────────────
function SectionLabel({ children }: { children: string }) {
  return (
    <div className="text-[10px] uppercase tracking-wider font-semibold pt-4 pb-1"
         style={{ color: 'var(--mute)' }}>
      {children}
    </div>
  )
}

export default function SettingsPanel() {
  const store    = useStore()
  const conv     = store.currentConv()
  const settings = conv?.settings ?? defaultSettings

  const [customToolsText, setCustomToolsText] = useState(() =>
    JSON.stringify(settings.customTools ?? [], null, 2)
  )
  const [helpOpen, setHelpOpen] = useState(false)

  useEffect(() => {
    setCustomToolsText(JSON.stringify(settings.customTools ?? [], null, 2))
  }, [conv?.id])

  if (!conv) return null

  const setPatch = (patch: Partial<typeof settings>) =>
    store.updateConvSettings(conv.id, patch)

  const onSaveCustomTools = () => {
    try {
      const parsed = JSON.parse(customToolsText || '[]')
      if (!Array.isArray(parsed)) throw new Error('expected an array')
      setPatch({ customTools: parsed })
    } catch (e: unknown) {
      alert('Invalid JSON: ' + (e as Error).message)
    }
  }

  const onClearChat = () => {
    if (confirm('Bu sohbetin tüm mesajları silinecek. Devam edilsin mi?')) {
      store.clearConversation(conv.id)
    }
  }

  return (
    <>
      {store.settingsOpen && (
        <div onClick={store.toggleSettings}
             className="fixed inset-0 top-14 bg-black/55 backdrop-blur-sm z-30" />
      )}

      <aside
        className="fixed top-14 right-0 bottom-0 w-80 max-w-[95vw] overflow-y-auto scrollbar-thin shadow-2xl transition-transform duration-300 z-40"
        style={{
          background: 'var(--surface)',
          borderLeft: '1px solid var(--border)',
          transform: store.settingsOpen ? 'translateX(0)' : 'translateX(100%)',
        }}
      >
        <div className="px-4 pb-6">

          {/* ── Header ────────────────────────────────────────────── */}
          <div className="sticky top-0 flex items-center justify-between py-4"
               style={{ background: 'var(--surface)', borderBottom: '1px solid var(--border)' }}>
            <span className="text-[11px] uppercase tracking-wider font-semibold"
                  style={{ color: 'var(--mute)' }}>
              {t('configuration')}
            </span>
            <button onClick={store.toggleSettings}
                    className="w-7 h-7 flex items-center justify-center rounded-full cursor-pointer text-lg leading-none transition"
                    style={{ color: 'var(--mute)' }}
                    onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
                    onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}>
              ×
            </button>
          </div>

          {/* ── Bilgi ─────────────────────────────────────────────── */}
          <SectionLabel>Hesap</SectionLabel>

          <div className="rounded-xl overflow-hidden text-[11px]"
               style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
            <div className="px-3 py-2 flex items-center justify-between"
                 style={{ borderBottom: '1px solid var(--border)' }}>
              <span style={{ color: 'var(--mute)' }}>Kullanıcı</span>
              <strong style={{ color: 'var(--text)' }}>{store.auth.username}</strong>
            </div>
            <div className="px-3 py-2 flex items-center justify-between"
                 style={{ borderBottom: '1px solid var(--border)' }}>
              <span style={{ color: 'var(--mute)' }}>Domain</span>
              <strong style={{ color: 'var(--text)' }}>{store.auth.domain}</strong>
            </div>
            {store.auth.groups && store.auth.groups.length > 0 && (
              <div className="px-3 py-2 flex items-center justify-between gap-2"
                   style={{ borderBottom: '1px solid var(--border)' }}>
                <span style={{ color: 'var(--mute)' }}>Gruplar</span>
                <div className="flex gap-1 flex-wrap justify-end">
                  {store.auth.groups.map(g => (
                    <span key={g} className="px-1.5 py-0.5 rounded text-[10px] font-medium"
                          style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>
                      {g}
                    </span>
                  ))}
                </div>
              </div>
            )}
            <div className="px-3 py-2 flex items-center justify-between">
              <span style={{ color: 'var(--mute)' }}>
                Sohbetler: <strong style={{ color: 'var(--text)' }}>{store.conversations.length}</strong>
              </span>
              <button
                onClick={onClearChat}
                title="Sohbeti temizle"
                className="p-1.5 rounded-md cursor-pointer transition"
                style={{ color: '#ef4444' }}
                onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'rgba(239,68,68,0.1)'}
                onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}
              >
                <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round"
                    d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6M1 7h22M9 7V4a1 1 0 011-1h4a1 1 0 011 1v3" />
                </svg>
              </button>
            </div>
          </div>

          {/* ── Uygulama ──────────────────────────────────────────── */}
          <SectionLabel>Uygulama</SectionLabel>

          <div className="rounded-xl overflow-hidden"
               style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
            {[
              {
                icon: <span className="text-sm font-bold" style={{ color: 'var(--mute)' }}>?</span>,
                label: 'Yardım',
                onClick: () => setHelpOpen(true),
              },
              {
                icon: <span className="text-base">{store.darkMode ? '☀️' : '🌙'}</span>,
                label: store.darkMode ? 'Açık Tema' : 'Koyu Tema',
                onClick: store.toggleTheme,
              },
              {
                icon: (
                  <span className="text-[10px] font-bold px-1.5 py-0.5 rounded"
                        style={{ border: '1px solid var(--border)', color: 'var(--mute)' }}>
                    {store.lang === 'tr' ? 'EN' : 'TR'}
                  </span>
                ),
                label: store.lang === 'tr' ? 'Switch to English' : 'Türkçeye Geç',
                onClick: store.toggleLang,
              },
            ].map((item, i, arr) => (
              <button
                key={item.label}
                onClick={item.onClick}
                className="w-full flex items-center gap-3 px-3 py-2.5 text-sm cursor-pointer transition text-left"
                style={{
                  color: 'var(--text-2)',
                  borderBottom: i < arr.length - 1 || store.auth.isAdmin ? '1px solid var(--border)' : 'none',
                }}
                onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
                onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}
              >
                <span className="w-5 flex items-center justify-center shrink-0">{item.icon}</span>
                {item.label}
              </button>
            ))}

            {store.auth.isAdmin && (
              <a
                href="/admin"
                className="w-full flex items-center gap-3 px-3 py-2.5 text-sm cursor-pointer transition"
                style={{ color: 'var(--text-2)', textDecoration: 'none' }}
                onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
                onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}
              >
                <span className="w-5 flex items-center justify-center shrink-0">
                  <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                    <path strokeLinecap="round" strokeLinejoin="round"
                      d="M12 6V4m0 2a2 2 0 100 4m0-4a2 2 0 110 4m-6 8a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4m6 6v10m6-2a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4" />
                  </svg>
                </span>
                Admin Paneli
              </a>
            )}
          </div>

          {/* ── Parametreler (gelişmiş, en altta) ─────────────────── */}
          <SectionLabel>{t('parameters')}</SectionLabel>

          <div className="space-y-3">
            <div>
              <div className="flex items-center justify-between mb-1">
                <span className="text-[11px]" style={{ color: 'var(--mute)' }}>{t('maxTokens')}</span>
                <strong className="text-[11px]" style={{ color: 'var(--text)' }}>{settings.maxTokens}</strong>
              </div>
              <div className="flex gap-2">
                <input
                  type="number" min={1} max={1048576}
                  value={settings.maxTokens}
                  onChange={e => setPatch({ maxTokens: Math.max(1, parseInt(e.target.value || '0', 10)) })}
                  className="flex-1 rounded-md px-3 py-1.5 text-sm outline-none"
                  style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
                />
                <button onClick={() => setPatch({ maxTokens: 32768 })}
                        className="rounded-md px-2.5 py-1.5 text-xs cursor-pointer"
                        style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text)' }}>
                  MAX
                </button>
              </div>
            </div>

            <div>
              <div className="flex items-center justify-between mb-1">
                <span className="text-[11px]" style={{ color: 'var(--mute)' }}>{t('temperature')}</span>
                <strong className="text-[11px]" style={{ color: 'var(--text)' }}>{settings.temperature.toFixed(2)}</strong>
              </div>
              <input
                type="range" min={0} max={2} step={0.05}
                value={settings.temperature}
                onChange={e => setPatch({ temperature: parseFloat(e.target.value) })}
                className="w-full"
              />
            </div>

            <ToggleRow label={t('stream')} checked={settings.stream}
                       onChange={v => setPatch({ stream: v })} />
          </div>

          {/* ── Mod (gelişmiş, en altta) ──────────────────────────── */}
          <SectionLabel>Mod</SectionLabel>

          <ToggleRow label={t('agentic')} checked={settings.agenticEnabled}
                     onChange={v => setPatch({ agenticEnabled: v })} />

          {settings.agenticEnabled && (
            <div className="mt-2 space-y-2 pl-1">
              <div className="flex items-center justify-between">
                <span className="text-[11px]" style={{ color: 'var(--mute)' }}>
                  {t('maxLoops')}: <strong style={{ color: 'var(--text)' }}>{settings.maxAgentLoops}</strong>
                </span>
                <input
                  type="number" min={1} max={50}
                  value={settings.maxAgentLoops}
                  onChange={e => setPatch({ maxAgentLoops: Math.max(1, parseInt(e.target.value || '1', 10)) })}
                  className="w-20 rounded-md px-2 py-1 text-xs outline-none text-right"
                  style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
                />
              </div>
              <div>
                <div className="text-[11px] mb-1" style={{ color: 'var(--mute)' }}>{t('customTools')} (JSON)</div>
                <textarea
                  value={customToolsText}
                  onChange={e => setCustomToolsText(e.target.value)}
                  onBlur={onSaveCustomTools}
                  rows={4}
                  className="w-full rounded-md px-3 py-2 text-xs outline-none font-mono scrollbar-thin"
                  style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
                />
              </div>
            </div>
          )}

        </div>
      </aside>

      {helpOpen && <HelpModal onClose={() => setHelpOpen(false)} isAdmin={!!store.auth.isAdmin} />}
    </>
  )
}
