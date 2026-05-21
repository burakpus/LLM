import { useState, useEffect } from 'react'
import { useStore, t, defaultSettings } from '../../store'
import type { Endpoint } from '../../store'

// Health check via .NET proxy (no direct vLLM access from browser)
async function pingProxy(): Promise<boolean> {
  try {
    const r = await fetch('/health', { signal: AbortSignal.timeout(3000) })
    return r.ok
  } catch {
    return false
  }
}

export default function SettingsPanel() {
  const store = useStore()
  const conv  = store.currentConv()
  const settings = conv?.settings ?? defaultSettings

  const [customModel, setCustomModel] = useState(settings.model   ?? '')
  const [keyDraft,    setKeyDraft]    = useState(store.apiKey)
  const [connectBusy, setConnectBusy] = useState(false)
  const [customToolsText, setCustomToolsText] = useState(() =>
    JSON.stringify(settings.customTools ?? [], null, 2)
  )

  useEffect(() => {
    setCustomModel(settings.model ?? '')
    setCustomToolsText(JSON.stringify(settings.customTools ?? [], null, 2))
  }, [conv?.id])

  if (!conv) return null

  // ── handlers ────────────────────────────────────────────────────────────
  const setPatch = (patch: Partial<typeof settings>) =>
    store.updateConvSettings(conv.id, patch)

  const onConnectCustom = async () => {
    setConnectBusy(true)
    store.setStatus('connecting', null)
    try {
      store.setActiveEndpoint(customModel.trim() || null, null)
      setPatch({ model: customModel.trim() || null })
      store.setStatus('connected', true)
    } finally {
      setConnectBusy(false)
    }
  }

  const onConnectEndpoint = async (ep: Endpoint, idx: number) => {
    setConnectBusy(true)
    store.setStatus('connecting', null)
    const ok = await pingProxy()
    store.setActiveEndpoint(ep.model, idx)
    setPatch({ model: ep.model })
    store.setStatus(ok ? 'connected' : 'unreachable', ok)
    setConnectBusy(false)
  }

  const onDisconnect = () => {
    store.setActiveEndpoint(null, null)
    setPatch({ model: null })
    store.setStatus('disconnected', false)
  }

  const onSaveApiKey = () => {
    store.setApiKey(keyDraft)
  }

  const onSaveCustomTools = () => {
    try {
      const parsed = JSON.parse(customToolsText || '[]')
      if (!Array.isArray(parsed)) throw new Error('expected an array')
      setPatch({ customTools: parsed })
    } catch (e: unknown) {
      alert('Invalid JSON: ' + (e as Error).message)
    }
  }

  // ── render ──────────────────────────────────────────────────────────────
  return (
    <>
      {store.settingsOpen && (
        <div
          onClick={store.toggleSettings}
          className="fixed inset-0 top-14 bg-black/55 backdrop-blur-sm z-30"
        />
      )}

      <aside
        className="fixed top-14 right-0 bottom-0 w-96 max-w-[95vw] overflow-y-auto scrollbar-thin shadow-2xl transition-transform duration-300 z-40"
        style={{
          background: 'var(--surface)',
          borderLeft: '1px solid var(--border)',
          transform: store.settingsOpen ? 'translateX(0)' : 'translateX(100%)',
        }}
      >
        <div className="p-5 space-y-5">
          {/* Header */}
          <div className="flex items-center justify-between pb-3"
               style={{ borderBottom: '1px solid var(--border)' }}>
            <span className="text-xs uppercase tracking-wider font-semibold"
                  style={{ color: 'var(--mute)' }}>
              {t('configuration')}
            </span>
            <button
              onClick={store.toggleSettings}
              className="text-xl font-bold cursor-pointer transition leading-none"
              style={{ color: 'var(--mute)' }}
            >
              ×
            </button>
          </div>

          {/* ── Connection ──────────────────────────────────────────── */}
          <section>
            <div className="text-[11px] uppercase tracking-wider mb-2 font-semibold"
                 style={{ color: 'var(--mute)' }}>
              Connection
            </div>

            <label className="block text-[11px] mb-1" style={{ color: 'var(--mute)' }}>
              {t('modelName')}
            </label>
            <input
              value={customModel}
              onChange={e => setCustomModel(e.target.value)}
              placeholder="qwen3.6-27b"
              className="w-full mb-2 rounded-md px-3 py-1.5 text-sm outline-none"
              style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
            />

            <label className="block text-[11px] mb-1" style={{ color: 'var(--mute)' }}>
              {t('apiKey')}
            </label>
            <div className="flex gap-2 mb-2">
              <input
                value={keyDraft}
                type="password"
                onChange={e => setKeyDraft(e.target.value)}
                placeholder="sk-..."
                className="flex-1 rounded-md px-3 py-1.5 text-sm outline-none"
                style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
              />
              <button onClick={onSaveApiKey}
                      className="rounded-md px-2.5 py-1.5 text-xs cursor-pointer"
                      style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text)' }}>
                Save
              </button>
            </div>

            <div className="flex gap-2">
              <button
                onClick={onConnectCustom}
                disabled={connectBusy}
                className="flex-1 rounded-md py-1.5 text-sm font-medium text-white cursor-pointer disabled:opacity-50"
                style={{ background: 'var(--accent)' }}
              >
                {connectBusy ? t('connecting') : t('connect')}
              </button>
              <button onClick={onDisconnect}
                      className="rounded-md px-3 py-1.5 text-xs cursor-pointer"
                      style={{ background: 'transparent', border: '1px solid #ef4444', color: '#ef4444' }}>
                {t('disconnect')}
              </button>
            </div>

            <div className="mt-3 flex flex-wrap gap-1.5">
              {store.endpoints.map((ep, i) => {
                const active = store.activeEpIdx === i
                return (
                  <button key={ep.name + i}
                          onClick={() => onConnectEndpoint(ep, i)}
                          className={`pill ${active ? 'active' : ''}`}>
                    <span className={`status-dot ${active && store.statusOk ? 'ok' : ''}`} />
                    {ep.name}
                  </button>
                )
              })}
            </div>
          </section>

          {/* ── Parameters ──────────────────────────────────────────── */}
          <section className="pt-3" style={{ borderTop: '1px solid var(--border)' }}>
            <div className="text-[11px] uppercase tracking-wider mb-2 font-semibold"
                 style={{ color: 'var(--mute)' }}>
              {t('parameters')}
            </div>

            <label className="block text-[11px] mb-1" style={{ color: 'var(--mute)' }}>
              {t('maxTokens')}: <strong style={{ color: 'var(--text)' }}>{settings.maxTokens}</strong>
            </label>
            <div className="flex gap-2 mb-3">
              <input
                type="number"
                min={1} max={1048576}
                value={settings.maxTokens}
                onChange={e => setPatch({ maxTokens: Math.max(1, parseInt(e.target.value || '0', 10)) })}
                className="flex-1 rounded-md px-3 py-1.5 text-sm outline-none"
                style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
              />
              <button onClick={() => setPatch({ maxTokens: 32768 })}
                      className="rounded-md px-2.5 py-1 text-xs cursor-pointer"
                      style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text)' }}>
                MAX
              </button>
            </div>

            <label className="block text-[11px] mb-1" style={{ color: 'var(--mute)' }}>
              {t('temperature')}: <strong style={{ color: 'var(--text)' }}>{settings.temperature.toFixed(2)}</strong>
            </label>
            <input
              type="range" min={0} max={2} step={0.05}
              value={settings.temperature}
              onChange={e => setPatch({ temperature: parseFloat(e.target.value) })}
              className="w-full mb-3"
            />

            <label className="flex items-center gap-2 text-sm cursor-pointer">
              <input
                type="checkbox"
                checked={settings.stream}
                onChange={e => setPatch({ stream: e.target.checked })}
              />
              <span style={{ color: 'var(--text)' }}>{t('stream')}</span>
            </label>
          </section>

          {/* ── System Prompt ────────────────────────────────────────── */}
          <section className="pt-3" style={{ borderTop: '1px solid var(--border)' }}>
            <div className="text-[11px] uppercase tracking-wider mb-2 font-semibold"
                 style={{ color: 'var(--mute)' }}>
              {t('systemPrompt')}
            </div>
            <textarea
              value={settings.systemPrompt}
              onChange={e => setPatch({ systemPrompt: e.target.value })}
              rows={5}
              placeholder="You are a helpful assistant..."
              className="w-full rounded-md px-3 py-2 text-sm outline-none resize-y scrollbar-thin"
              style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
            />
          </section>

          {/* ── Agentic ─────────────────────────────────────────────── */}
          <section className="pt-3" style={{ borderTop: '1px solid var(--border)' }}>
            <label className="flex items-center justify-between cursor-pointer">
              <span className="text-sm font-medium" style={{ color: 'var(--text)' }}>
                {t('agentic')}
              </span>
              <input
                type="checkbox"
                checked={settings.agenticEnabled}
                onChange={e => setPatch({ agenticEnabled: e.target.checked })}
              />
            </label>

            {settings.agenticEnabled && (
              <div className="mt-2 space-y-2">
                <label className="block text-[11px]" style={{ color: 'var(--mute)' }}>
                  {t('maxLoops')}: <strong style={{ color: 'var(--text)' }}>{settings.maxAgentLoops}</strong>
                </label>
                <input
                  type="number" min={1} max={50}
                  value={settings.maxAgentLoops}
                  onChange={e => setPatch({ maxAgentLoops: Math.max(1, parseInt(e.target.value || '1', 10)) })}
                  className="w-full rounded-md px-3 py-1.5 text-sm outline-none"
                  style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
                />
                <label className="block text-[11px]" style={{ color: 'var(--mute)' }}>
                  {t('customTools')} (JSON array)
                </label>
                <textarea
                  value={customToolsText}
                  onChange={e => setCustomToolsText(e.target.value)}
                  onBlur={onSaveCustomTools}
                  rows={5}
                  className="w-full rounded-md px-3 py-2 text-xs outline-none font-mono scrollbar-thin"
                  style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
                />
              </div>
            )}
          </section>

          {/* ── Auto-complete ───────────────────────────────────────── */}
          <section className="pt-3" style={{ borderTop: '1px solid var(--border)' }}>
            <label className="flex items-center justify-between cursor-pointer">
              <span className="text-sm font-medium" style={{ color: 'var(--text)' }}>
                {t('autoComplete')}
              </span>
              <input
                type="checkbox"
                checked={settings.autoComplete}
                onChange={e => setPatch({ autoComplete: e.target.checked })}
              />
            </label>
          </section>

          {/* ── Clear chat ──────────────────────────────────────────── */}
          <section className="pt-3" style={{ borderTop: '1px solid var(--border)' }}>
            <button
              onClick={() => {
                if (confirm('Clear all messages in this conversation?')) {
                  store.clearConversation(conv.id)
                }
              }}
              className="w-full rounded-md py-1.5 text-sm cursor-pointer"
              style={{ background: 'transparent', border: '1px solid #ef4444', color: '#ef4444' }}
            >
              {t('clearChat')}
            </button>
          </section>

          {/* ── Info ─────────────────────────────────────────────────── */}
          <section className="pt-3 text-[11px] space-y-0.5" style={{ borderTop: '1px solid var(--border)', color: 'var(--mute)' }}>
            <div>User: <strong style={{ color: 'var(--text)' }}>{store.auth.username}</strong></div>
            <div>Domain: <strong style={{ color: 'var(--text)' }}>{store.auth.domain}</strong></div>
            <div>Conversations: {store.conversations.length}</div>
          </section>
        </div>
      </aside>
    </>
  )
}
