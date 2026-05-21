import { useStore, t } from '../../store'

export default function SettingsDrawer() {
  const store = useStore()

  return (
    <>
      {/* Backdrop */}
      {store.settingsOpen && (
        <div
          onClick={store.toggleSettings}
          className="fixed inset-0 top-14 bg-black/55 backdrop-blur-sm z-30"
        />
      )}

      {/* Panel */}
      <aside
        className="fixed top-14 right-0 bottom-0 w-80 max-w-[90vw] overflow-y-auto scrollbar-thin shadow-xl transition-transform duration-300 z-40"
        style={{
          background: 'var(--color-surface)',
          borderLeft: '1px solid var(--color-border)',
          transform: store.settingsOpen ? 'translateX(0)' : 'translateX(100%)',
        }}
      >
        <div className="p-4 space-y-4">
          <div className="flex items-center justify-between pb-2.5"
               style={{ borderBottom: '1px solid var(--color-border)' }}>
            <span className="text-[11px] uppercase tracking-wider font-semibold" style={{ color: 'var(--color-mute)' }}>
              {t('configuration')}
            </span>
            <button onClick={store.toggleSettings}
                    className="text-xl font-bold cursor-pointer hover:text-red-500 transition"
                    style={{ color: 'var(--color-mute)' }}>×</button>
          </div>

          {/* Theme */}
          <div>
            <label className="block text-[11px] uppercase tracking-wider mb-1.5" style={{ color: 'var(--color-mute)' }}>
              Theme
            </label>
            <button onClick={store.toggleTheme}
                    className="w-full border rounded-md px-3 py-2 text-sm transition cursor-pointer"
                    style={{ background: 'var(--color-bg)', border: '1px solid var(--color-border)', color: 'var(--color-text)' }}>
              {store.darkMode ? '☀️ Switch to Light' : '🌙 Switch to Dark'}
            </button>
          </div>

          {/* Language */}
          <div>
            <label className="block text-[11px] uppercase tracking-wider mb-1.5" style={{ color: 'var(--color-mute)' }}>
              Language
            </label>
            <button onClick={store.toggleLang}
                    className="w-full border rounded-md px-3 py-2 text-sm transition cursor-pointer"
                    style={{ background: 'var(--color-bg)', border: '1px solid var(--color-border)', color: 'var(--color-text)' }}>
              {store.lang === 'tr' ? '🇬🇧 Switch to English' : '🇹🇷 Türkçeye Geç'}
            </button>
          </div>

          {/* Info */}
          <div className="pt-2" style={{ borderTop: '1px solid var(--color-border)' }}>
            <div className="text-[11px] space-y-1" style={{ color: 'var(--color-mute)' }}>
              <div>Logged in as <strong style={{ color: 'var(--color-text)' }}>{store.auth.username}</strong></div>
              <div>Domain: <strong style={{ color: 'var(--color-text)' }}>{store.auth.domain}</strong></div>
              <div>Conversations: {store.conversations.length}</div>
            </div>
          </div>
        </div>
      </aside>
    </>
  )
}
