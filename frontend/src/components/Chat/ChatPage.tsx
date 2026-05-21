import { useEffect, useState } from 'react'
import { useStore } from '../../store'
import { health, listSkills } from '../../api'
import type { Skill } from '../../api'
import Header        from './Header'
import Sidebar       from './Sidebar'
import MessageList   from './MessageList'
import InputBar      from './InputBar'
import SettingsPanel from './SettingsPanel'
import StatsBar      from './StatsBar'
import ProjectPanel  from './ProjectPanel'
import { useGeneration } from '../../hooks/useGeneration'

export default function ChatPage() {
  const store    = useStore()
  const [skills,      setSkills]      = useState<Skill[]>([])
  const [statusOk,    setStatusOk]    = useState<boolean | null>(null)
  const [fileContext, setFileContext]  = useState<string>('')   // injected from project panel tab click
  const { send, regenerate, continueResponse, stop } = useGeneration()

  // ── Ensure at least one conversation ───────────────────────────────────────
  useEffect(() => {
    if (!store.currentId || !store.conversations.find(c => c.id === store.currentId)) {
      store.newConversation()
    }
    // run once
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // ── Apply theme on mount ──────────────────────────────────────────────────
  useEffect(() => {
    document.documentElement.setAttribute('data-theme', store.darkMode ? 'dark' : 'light')
  }, [store.darkMode])

  // ── Load skills ───────────────────────────────────────────────────────────
  useEffect(() => {
    listSkills().then(setSkills).catch(() => setSkills([]))
  }, [])

  // ── Health check ──────────────────────────────────────────────────────────
  useEffect(() => {
    const check = () => health().then(setStatusOk).catch(() => setStatusOk(false))
    check()
    const id = setInterval(check, 30_000)
    return () => clearInterval(id)
  }, [])

  // ── Auto-connect: ping .NET proxy health, select first endpoint model ────────
  useEffect(() => {
    const eps = store.endpoints
    if (eps.length === 0) return

    let cancelled = false
    ;(async () => {
      try {
        const r = await fetch('/health', { signal: AbortSignal.timeout(4000) })
        if (cancelled) return
        if (r.ok) {
          const ep = eps[0]
          store.setActiveEndpoint(ep.model, 0)
          store.setStatus('connected', true)
          if (store.currentId)
            store.updateConvSettings(store.currentId, { model: ep.model })
          return
        }
      } catch { /* fall through */ }
      if (!cancelled) store.setStatus('not connected', false)
    })()

    return () => { cancelled = true }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const conv       = store.currentConv()
  const generating = conv?.generating ?? false

  return (
    <div className="h-dvh flex flex-col overflow-hidden"
         style={{ background: 'var(--bg)', color: 'var(--text)' }}>

      <Header
        skills={skills}
        statusOk={statusOk}
        generating={generating}
      />

      <div className="flex-1 flex overflow-hidden relative">
        <Sidebar />

        <main className="flex-1 flex flex-col min-w-0 transition-[margin-right] duration-300"
              style={{ marginRight: store.project.projectId ? '420px' : 0 }}>
          <MessageList
            messages={conv?.messages ?? []}
            generating={generating}
            onRegenerate={regenerate}
            onContinue={continueResponse}
          />
          <StatsBar stats={conv?.stats ?? null} />
          <InputBar
            onSend={send}
            onStop={stop}
            onRegenerate={regenerate}
            generating={generating}
            fileContext={fileContext}
            onFileContextConsumed={() => setFileContext('')}
          />
        </main>

        <SettingsPanel />
        <ProjectPanel onFileContext={setFileContext} />
      </div>
    </div>
  )
}
