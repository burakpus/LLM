import { useEffect, useState } from 'react'
import { useStore } from './store'
import LoginPage from './components/LoginPage'
import ChatPage  from './components/Chat/ChatPage'
import AdminPage from './components/Admin/AdminPage'
import { me }   from './api'

export default function App() {
  const { auth, setAuth, clearAuth } = useStore()
  const [checking, setChecking]      = useState(true)
  const [path, setPath]              = useState<string>(window.location.pathname)

  useEffect(() => {
    // Restore auth from localStorage on mount
    const stored = localStorage.getItem('setllm-user')
    const token  = localStorage.getItem('setllm-token')
    if (stored && token) {
      const u = JSON.parse(stored)
      setAuth({ token, ...u })
      // Verify token is still valid
      me().then(() => setChecking(false))
         .catch(() => { clearAuth(); setChecking(false) })
    } else {
      clearAuth()
      setChecking(false)
    }
  }, [])

  // Listen for back/forward navigation
  useEffect(() => {
    const onPop = () => setPath(window.location.pathname)
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [])

  if (checking) {
    return (
      <div className="h-dvh flex items-center justify-center"
           style={{ background: 'var(--color-bg)' }}>
        <div className="w-8 h-8 border-2 border-[var(--color-accent)] border-t-transparent
                        rounded-full animate-spin" />
      </div>
    )
  }

  if (!auth.token) return <LoginPage />

  const isAdmin = path === '/admin' || path.startsWith('/admin/')
  return isAdmin ? <AdminPage /> : <ChatPage />
}
