import { useEffect, useState } from 'react'
import { useStore } from '../../store'
import SetLogo from '../SetLogo'
import UploadTab from './tabs/UploadTab'
import DocumentsTab from './tabs/DocumentsTab'
import SkillsTab from './tabs/SkillsTab'
import TemplatesTab from './tabs/TemplatesTab'
import SqlConnectionsTab from './tabs/SqlConnectionsTab'
import JobsTab from './tabs/JobsTab'
import UsageTab from './tabs/UsageTab'
import ActivityTab from './tabs/ActivityTab'
import SecurityTab from './tabs/SecurityTab'
import BenchmarkTab from './tabs/BenchmarkTab'
import SettingsTab from './tabs/SettingsTab'

type Tab = 'upload' | 'documents' | 'skills' | 'templates' | 'sql' | 'jobs' | 'usage' | 'activity' | 'security' | 'benchmark' | 'settings'

// =============================================================================
// AdminPage — RAG admin panel (11 tabs, each in tabs/<Name>Tab.tsx)
// =============================================================================

export default function AdminPage() {
  const store = useStore()
  const { auth } = store
  const [tab,      setTab]      = useState<Tab>('upload')
  const [checking, setChecking] = useState(!auth.isAdmin)

  useEffect(() => {
    const stored = localStorage.getItem('setllm-theme')
    document.documentElement.setAttribute('data-theme', stored === 'light' ? 'light' : 'dark')
  }, [])

  // Re-check admin status from server on every /admin visit.
  // Fixes stale JWT tokens that were issued before AdminUsers config was added.
  useEffect(() => {
    if (auth.isAdmin) { setChecking(false); return }
    const tok = localStorage.getItem('setllm-token')
    if (!tok) { setChecking(false); return }
    fetch('/api/auth/me', { headers: { Authorization: `Bearer ${tok}` } })
      .then(r => r.ok ? r.json() : null)
      .then(data => {
        if (data?.isAdmin) {
          store.setAuth({ ...auth, isAdmin: true })
        }
      })
      .catch(() => {})
      .finally(() => setChecking(false))
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  if (checking) {
    return (
      <div className="min-h-dvh flex items-center justify-center"
           style={{ background: 'var(--bg)', color: 'var(--mute)' }}>
        <span className="text-sm">Yetki kontrol ediliyor…</span>
      </div>
    )
  }

  // Guard: non-admin users who navigate directly to /admin see a 403 page
  if (!auth.isAdmin) {
    return (
      <div className="min-h-dvh flex flex-col items-center justify-center gap-5"
           style={{ background: 'var(--bg)', color: 'var(--text)' }}>
        <svg className="w-16 h-16 opacity-25" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
          <path strokeLinecap="round" strokeLinejoin="round"
            d="M16.5 10.5V6.75a4.5 4.5 0 10-9 0v3.75m-.75 11.25h10.5a2.25 2.25 0 002.25-2.25v-6.75a2.25 2.25 0 00-2.25-2.25H6.75a2.25 2.25 0 00-2.25 2.25v6.75a2.25 2.25 0 002.25 2.25z"/>
        </svg>
        <div>
          <div className="text-2xl font-semibold text-center">Erişim Yetkiniz Yok</div>
          <p className="text-sm mt-2 text-center max-w-xs" style={{ color: 'var(--mute)' }}>
            Bu sayfayı görüntülemek için yönetici yetkisi gereklidir.
          </p>
        </div>
        <div className="flex flex-col items-center gap-2 mt-1">
          <p className="text-xs" style={{ color: 'var(--mute-2)' }}>
            Yetkiniz varsa çıkış yapıp tekrar giriş yapın.
          </p>
          <div className="flex gap-2">
            <a href="/" className="px-4 py-2 rounded-full text-sm cursor-pointer"
               style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
              ← Ana Sayfa
            </a>
            <a href="/login"
               onClick={() => { localStorage.removeItem('setllm-token'); localStorage.removeItem('setllm-user') }}
               className="px-4 py-2 rounded-full text-sm cursor-pointer"
               style={{ background: 'var(--accent)', color: '#0b1929' }}>
              Yeniden Giriş Yap
            </a>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="min-h-dvh flex flex-col" style={{ background: 'var(--bg)', color: 'var(--text)' }}>
      {/* Header */}
      <header
        className="h-14 flex items-center gap-3 px-4 shrink-0"
        style={{ background: 'var(--bg)', borderBottom: '1px solid var(--border)' }}
      >
        <a href="/" className="p-2 rounded-full cursor-pointer transition"
           style={{ color: 'var(--text-2)' }}
           title="Back to chat"
           onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
           onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}>
          <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M10 19l-7-7m0 0l7-7m-7 7h18" />
          </svg>
        </a>
        <SetLogo className="h-6 w-auto" />
        <span className="text-[15px] font-medium tracking-tight">SET LLM Admin</span>

        <div className="flex-1" />

        <nav className="flex items-center gap-1">
          {(['upload', 'documents', 'skills', 'templates', 'sql', 'jobs', 'usage', 'activity', 'security', 'benchmark', 'settings'] as Tab[]).map(tb => (
            <button
              key={tb}
              onClick={() => setTab(tb)}
              className="px-3 py-1.5 text-sm rounded-full transition cursor-pointer"
              style={{
                background: tab === tb ? 'var(--surface-hi)' : 'transparent',
                color:      tab === tb ? 'var(--accent-hi)' : 'var(--text-2)',
                border:     tab === tb ? '1px solid var(--border)' : '1px solid transparent',
              }}
            >
              {tb === 'upload' ? 'Upload' : tb === 'documents' ? 'Documents' : tb === 'skills' ? 'Skills' : tb === 'templates' ? 'Şablonlar' : tb === 'sql' ? 'SQL' : tb === 'jobs' ? 'İşler' : tb === 'usage' ? 'Kullanım' : tb === 'activity' ? 'Aktivite' : tb === 'security' ? '🛡 Güvenlik' : tb === 'benchmark' ? '🧪 Benchmark' : '⚙ Ayarlar'}
            </button>
          ))}
        </nav>

        {/* Logout — same icon/position as home page */}
        <button
          onClick={() => {
            localStorage.removeItem('setllm-token')
            localStorage.removeItem('setllm-user')
            window.location.href = '/login'
          }}
          className="ml-2 p-2 rounded-full transition cursor-pointer"
          style={{ color: 'var(--text-2)' }}
          title="Çıkış yap"
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

      {/* Body */}
      <main className="flex-1 overflow-auto p-6">
        <div className="max-w-6xl mx-auto">
          {tab === 'upload'    && <UploadTab />}
          {tab === 'documents' && <DocumentsTab />}
          {tab === 'skills'    && <SkillsTab />}
          {tab === 'templates' && <TemplatesTab />}
          {tab === 'sql'       && <SqlConnectionsTab />}
          {tab === 'jobs'      && <JobsTab />}
          {tab === 'usage'     && <UsageTab />}
          {tab === 'activity'  && <ActivityTab />}
          {tab === 'security'  && <SecurityTab />}
          {tab === 'benchmark' && <BenchmarkTab />}
          {tab === 'settings'  && <SettingsTab />}
        </div>
      </main>
    </div>
  )
}



