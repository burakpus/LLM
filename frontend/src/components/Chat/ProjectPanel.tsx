import { useState } from 'react'
import { useStore } from '../../store'
import { writeFile } from '../../api/project'
import DiffView from './DiffView'

// ── Language detection ────────────────────────────────────────────────────────
function langFromPath(path: string): string {
  const ext = path.split('.').pop()?.toLowerCase() ?? ''
  const map: Record<string, string> = {
    ts: 'TypeScript', tsx: 'TypeScript', js: 'JavaScript', jsx: 'JavaScript',
    py: 'Python', cs: 'C#', sql: 'SQL', json: 'JSON', yaml: 'YAML', yml: 'YAML',
    md: 'Markdown', html: 'HTML', css: 'CSS', sh: 'Shell', txt: 'Text',
  }
  return map[ext] ?? ext.toUpperCase()
}

export default function ProjectPanel() {
  const store = useStore()
  const { project } = store
  const [saving, setSaving] = useState(false)
  const [err,    setErr]    = useState<string | null>(null)

  if (!project.projectId) return null

  const activeContent = project.activeFilePath
    ? (project.files[project.activeFilePath] ?? '')
    : null
  const pending = project.pendingChange

  const onAccept = async () => {
    if (!pending || !project.projectId) return
    setSaving(true); setErr(null)
    try {
      await writeFile(project.projectId, pending.path, pending.newContent)
      store.acceptPendingChange()
    } catch (e: any) {
      setErr(e.message ?? String(e))
    } finally {
      setSaving(false)
    }
  }

  const onReject = () => store.setPendingChange(null)

  const fileList = Object.keys(project.files).sort()

  return (
    <div
      className="fixed top-14 right-0 bottom-0 flex flex-col"
      style={{
        width: '420px',
        background: 'var(--surface)',
        borderLeft: '1px solid var(--border)',
        zIndex: 20,
      }}
    >
      {/* Header */}
      <div
        className="flex items-center gap-2 px-3 py-2 shrink-0 text-xs"
        style={{ borderBottom: '1px solid var(--border)', color: 'var(--mute)' }}
      >
        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round"
            d="M3 7a2 2 0 012-2h4l2 2h8a2 2 0 012 2v9a2 2 0 01-2 2H5a2 2 0 01-2-2V7z"/>
        </svg>
        <span className="font-medium" style={{ color: 'var(--text)' }}>{project.projectId}</span>
        <div className="flex-1" />
        <button
          onClick={() => store.setProjectId(null)}
          className="px-2 py-0.5 rounded text-[10px] cursor-pointer transition"
          style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)' }}
          title="Projeyi kapat"
        >
          ✕
        </button>
      </div>

      {/* File tabs */}
      {fileList.length > 0 && (
        <div
          className="flex overflow-x-auto shrink-0 scrollbar-thin"
          style={{ borderBottom: '1px solid var(--border)' }}
        >
          {fileList.map(path => {
            const active = project.activeFilePath === path
            return (
              <button
                key={path}
                onClick={() => store.setActiveFile(path)}
                className="px-3 py-1.5 text-[11px] whitespace-nowrap shrink-0 cursor-pointer transition border-r"
                style={{
                  borderColor: 'var(--border)',
                  background:  active ? 'var(--surface-hi)' : 'transparent',
                  color:       active ? 'var(--accent-hi)'  : 'var(--mute)',
                  borderBottom: active ? '2px solid var(--accent)' : '2px solid transparent',
                }}
              >
                {path.split('/').pop()}
              </button>
            )
          })}
        </div>
      )}

      {/* Pending change banner */}
      {pending && (
        <div
          className="shrink-0 px-3 py-2 flex items-center gap-2"
          style={{ background: 'rgba(251,191,36,0.08)', borderBottom: '1px solid rgba(251,191,36,0.3)' }}
        >
          <span className="text-xs font-medium flex-1" style={{ color: '#fbbf24' }}>
            ✏️ Değişiklik: <span className="font-mono">{pending.path}</span>
          </span>
          <button
            onClick={onAccept}
            disabled={saving}
            className="px-3 py-1 rounded text-xs font-semibold cursor-pointer disabled:opacity-50"
            style={{ background: '#34a853', color: '#fff' }}
          >
            {saving ? '...' : 'Tamam'}
          </button>
          <button
            onClick={onReject}
            className="px-3 py-1 rounded text-xs cursor-pointer"
            style={{ background: 'transparent', border: '1px solid var(--border)', color: 'var(--mute)' }}
          >
            Reddet
          </button>
        </div>
      )}

      {err && (
        <div className="shrink-0 px-3 py-1.5 text-xs" style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335' }}>
          {err}
        </div>
      )}

      {/* Content area */}
      <div className="flex-1 overflow-auto">
        {pending ? (
          // Show diff view when there's a pending change
          <div className="p-3">
            <div className="text-[10px] mb-2 uppercase tracking-wider font-semibold" style={{ color: 'var(--mute)' }}>
              {langFromPath(pending.path)} — Diff
            </div>
            <DiffView lines={pending.diffLines} />
          </div>
        ) : project.activeFilePath && activeContent !== null ? (
          // Show current file content
          <div className="p-3">
            <div className="flex items-center justify-between mb-2">
              <span className="text-[10px] uppercase tracking-wider font-semibold" style={{ color: 'var(--mute)' }}>
                {langFromPath(project.activeFilePath)}
              </span>
              <span className="text-[10px]" style={{ color: 'var(--mute)' }}>
                {activeContent.split('\n').length} satır
              </span>
            </div>
            <pre className="text-xs font-mono leading-5 whitespace-pre overflow-x-auto select-text"
                 style={{ color: 'var(--text)' }}>
              {activeContent}
            </pre>
          </div>
        ) : (
          <div className="flex items-center justify-center h-full text-sm" style={{ color: 'var(--mute)' }}>
            {fileList.length === 0 ? 'Henüz dosya yok' : 'Dosya seçin'}
          </div>
        )}
      </div>
    </div>
  )
}
