import { useEffect, useState } from 'react'
import { useStore, t, DEFAULT_ENDPOINTS } from '../../../store'
import type { Endpoint } from '../../../store'

async function pingProxy(): Promise<boolean> {
  try {
    const r = await fetch('/health', { signal: AbortSignal.timeout(3000) })
    return r.ok
  } catch { return false }
}

export default function SettingsTab() {
  const store    = useStore()
  const conv     = store.currentConv()
  const settings = conv?.settings

  const [customModel,  setCustomModel]  = useState(settings?.model ?? '')
  const [keyDraft,     setKeyDraft]     = useState(store.apiKey)
  const [systemPrompt, setSystemPrompt] = useState(settings?.systemPrompt ?? '')
  const [connectBusy,  setConnectBusy]  = useState(false)

  useEffect(() => {
    setCustomModel(settings?.model ?? '')
    setSystemPrompt(settings?.systemPrompt ?? '')
  }, [conv?.id])

  const setPatch = (patch: Parameters<typeof store.updateConvSettings>[1]) => {
    if (!conv) return
    store.updateConvSettings(conv.id, patch)
  }

  const onConnectCustom = async () => {
    setConnectBusy(true)
    store.setStatus('connecting', null)
    try {
      store.setActiveEndpoint(customModel.trim() || null, null)
      setPatch({ model: customModel.trim() || null })
      store.setStatus('connected', true)
    } finally { setConnectBusy(false) }
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

  return (
    <section className="space-y-8 max-w-xl">
      <div>
        <h2 className="text-lg font-medium">Gelişmiş Ayarlar</h2>
        <p className="text-xs mt-1" style={{ color: 'var(--mute)' }}>
          Bağlantı ve sistem komutu ayarları. Değişiklikler aktif sohbete uygulanır.
        </p>
      </div>

      {/* ── Connection ─────────────────────────────────────────────────── */}
      <div className="rounded-xl p-5 space-y-4"
           style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
        <div className="text-xs font-semibold uppercase tracking-wider"
             style={{ color: 'var(--mute)' }}>
          Connection
        </div>

        <label className="block">
          <div className="text-xs mb-1.5" style={{ color: 'var(--mute)' }}>{t('modelName')}</div>
          <input
            value={customModel}
            onChange={e => setCustomModel(e.target.value)}
            placeholder="chat / code / reason / qwen3.6-27b"
            className="w-full rounded-md px-3 py-2 text-sm outline-none"
            style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
          />
        </label>

        <label className="block">
          <div className="text-xs mb-1.5" style={{ color: 'var(--mute)' }}>{t('apiKey')}</div>
          <div className="flex gap-2">
            <input
              value={keyDraft}
              type="password"
              onChange={e => setKeyDraft(e.target.value)}
              placeholder="sk-..."
              className="flex-1 rounded-md px-3 py-2 text-sm outline-none"
              style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
            />
            <button
              onClick={() => store.setApiKey(keyDraft)}
              className="px-3 py-2 rounded-md text-xs cursor-pointer"
              style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text)' }}
            >
              Kaydet
            </button>
          </div>
        </label>

        <div className="flex gap-2">
          <button
            onClick={onConnectCustom}
            disabled={connectBusy}
            className="flex-1 rounded-md py-2 text-sm font-medium cursor-pointer disabled:opacity-50"
            style={{ background: 'var(--accent)', color: '#0b1929' }}
          >
            {connectBusy ? t('connecting') : t('connect')}
          </button>
          <button
            onClick={onDisconnect}
            className="rounded-md px-4 py-2 text-xs cursor-pointer"
            style={{ border: '1px solid #ef4444', color: '#ef4444', background: 'transparent' }}
          >
            {t('disconnect')}
          </button>
        </div>

        <div className="flex flex-wrap gap-1.5">
          {(store.endpoints.length ? store.endpoints : DEFAULT_ENDPOINTS).map((ep, i) => {
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
      </div>

      {/* ── System Prompt ──────────────────────────────────────────────── */}
      <div className="rounded-xl p-5 space-y-3"
           style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
        <div className="text-xs font-semibold uppercase tracking-wider"
             style={{ color: 'var(--mute)' }}>
          {t('systemPrompt')}
        </div>
        <p className="text-xs" style={{ color: 'var(--mute)' }}>
          Aktif sohbet için sistem komutu. Skill seçilince skill prompt'u devralır.
        </p>
        <textarea
          value={systemPrompt}
          onChange={e => {
            setSystemPrompt(e.target.value)
            setPatch({ systemPrompt: e.target.value })
          }}
          rows={8}
          placeholder="You are a helpful assistant..."
          className="w-full rounded-md px-3 py-2 text-sm outline-none resize-y scrollbar-thin font-mono"
          style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
        />
        {!conv && (
          <p className="text-xs" style={{ color: '#f59e0b' }}>
            ⚠ Aktif sohbet yok — chat ekranında bir sohbet açın.
          </p>
        )}
      </div>
    </section>
  )
}
