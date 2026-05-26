import { useState, useEffect, FormEvent } from 'react'
import { login, getDomains, getModelCapabilities } from '../api'
import { useStore } from '../store'
import SetLogo from './SetLogo'

export default function LoginPage() {
  const { setAuth, setModelCapabilities } = useStore()
  const [domains,   setDomains]   = useState<string[]>([])
  const [domain,    setDomain]    = useState('')
  const [username,  setUsername]  = useState('')
  const [password,  setPassword]  = useState('')
  const [error,     setError]     = useState('')
  const [loading,   setLoading]   = useState(false)

  // If we got bounced here by the auth interceptor (?expired=1), inform user.
  const [expiredNotice, setExpiredNotice] = useState<string | null>(() => {
    try {
      const sp = new URLSearchParams(window.location.search)
      return sp.get('expired') === '1'
        ? 'Oturumunuz sona erdi. Lütfen tekrar giriş yapın.'
        : null
    } catch { return null }
  })

  useEffect(() => {
    getDomains().then(d => {
      setDomains(d)
      if (d.length) setDomain(d[0])
    }).catch(() => setDomains(['SETYAZILIM']))
  }, [])

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault()
    if (!username || !password) { setError('Username and password are required.'); return }
    setLoading(true); setError('')
    try {
      const r = await login(username, password, domain)
      // Parse isAdmin + groups from JWT payload
      let isAdmin = false
      let groups: string[] = []
      try {
        const payload = JSON.parse(atob(r.token.split('.')[1]))
        isAdmin = payload['isAdmin'] === 'true' || payload['isAdmin'] === true
        const g = payload['groups']
        if (typeof g === 'string' && g) groups = g.split(';').filter(Boolean)
      } catch { /* ignore parse error */ }
      setAuth({ token: r.token, username: r.username, domain: r.domain, isAdmin, groups })
      // Fetch model capabilities after login
      getModelCapabilities().then(setModelCapabilities).catch(() => { /* non-critical */ })
    } catch {
      setError('Invalid username or password.')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="min-h-dvh flex items-center justify-center p-6"
         style={{ background: 'linear-gradient(135deg, var(--color-bg) 0%, oklch(16% 0.04 290) 50%, oklch(20% 0.08 290) 100%)' }}>
      <div className="w-full max-w-md rounded-2xl p-10 shadow-[0_25px_60px_-15px_rgba(0,0,0,0.6)]"
           style={{ background: 'oklch(20% 0.025 275 / 0.85)', border: '1px solid var(--color-border)', backdropFilter: 'blur(24px)' }}>

        <SetLogo className="w-20 h-auto block mx-auto mb-4" />
        <h1 className="text-2xl font-bold text-center tracking-tight"
            style={{ color: 'var(--color-accent-hi)' }}>SET LLM</h1>
        <p className="text-[11px] text-center uppercase tracking-[.15em] font-semibold mt-1 mb-6"
           style={{ color: 'var(--color-mute)' }}>Active Directory Sign In</p>

        {expiredNotice && !error && (
          <div className="mb-3 px-3.5 py-2.5 rounded-lg text-sm text-center"
               style={{ background: 'rgba(251,191,36,0.10)', border: '1px solid rgba(251,191,36,0.30)', color: '#fbbf24' }}>
            ⏳ {expiredNotice}
          </div>
        )}

        {error && (
          <div className="mb-5 px-3.5 py-2.5 rounded-lg text-sm text-center text-red-400
                          bg-red-500/10 border border-red-500/25">
            {error}
          </div>
        )}

        <form onSubmit={handleSubmit} className="space-y-4"
              onChange={() => expiredNotice && setExpiredNotice(null)}>
          <div>
            <label className="block text-[11px] uppercase tracking-wider font-semibold mb-1.5"
                   style={{ color: 'var(--color-mute)' }}>Username</label>
            <input
              value={username}
              onChange={e => setUsername(e.target.value)}
              placeholder="e.g. johndoe"
              autoComplete="username"
              className="w-full rounded-lg px-3.5 py-2.5 text-sm outline-none transition"
              style={{ background: 'var(--color-bg)', border: '1px solid var(--color-border)',
                       color: 'var(--color-text)' }}
            />
          </div>

          <div>
            <label className="block text-[11px] uppercase tracking-wider font-semibold mb-1.5"
                   style={{ color: 'var(--color-mute)' }}>Password</label>
            <input
              type="password"
              value={password}
              onChange={e => setPassword(e.target.value)}
              placeholder="••••••••"
              autoComplete="current-password"
              className="w-full rounded-lg px-3.5 py-2.5 text-sm outline-none transition"
              style={{ background: 'var(--color-bg)', border: '1px solid var(--color-border)',
                       color: 'var(--color-text)' }}
            />
          </div>

          <div>
            <label className="block text-[11px] uppercase tracking-wider font-semibold mb-1.5"
                   style={{ color: 'var(--color-mute)' }}>Domain</label>
            <select
              value={domain}
              onChange={e => setDomain(e.target.value)}
              className="w-full rounded-lg px-3.5 py-2.5 text-sm outline-none transition"
              style={{ background: 'var(--color-bg)', border: '1px solid var(--color-border)',
                       color: 'var(--color-text)' }}
            >
              {domains.map(d => <option key={d} value={d}>{d}</option>)}
            </select>
          </div>

          <button
            type="submit"
            disabled={loading}
            className="w-full rounded-lg py-2.5 text-sm font-semibold text-white transition-colors
                       disabled:opacity-50 disabled:cursor-not-allowed"
            style={{ background: 'var(--color-accent)' }}
          >
            {loading ? 'Signing in...' : 'Sign In'}
          </button>
        </form>
      </div>
    </div>
  )
}
