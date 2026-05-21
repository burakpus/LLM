import { useState, useEffect, useRef } from 'react'
import { useStore } from '../../store'
import { writeFile, listFiles } from '../../api/project'
import { computeDiff } from './DiffView'
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

// ── Props for chat context callback ──────────────────────────────────────────
interface Props {
  onFileContext?: (text: string) => void  // called when user clicks a tab → inject into chat
}

export default function ProjectPanel({ onFileContext }: Props) {
  const store = useStore()
  const { project } = store

  const [saving,    setSaving]    = useState(false)
  const [err,       setErr]       = useState<string | null>(null)
  const [editMode,  setEditMode]  = useState(false)
  const [editText,  setEditText]  = useState('')
  const [newMode,   setNewMode]   = useState(false)
  const [newName,   setNewName]   = useState('')
  const [newText,   setNewText]   = useState('')
  const textareaRef = useRef<HTMLTextAreaElement>(null)

  if (!project.projectId) return null

  const activeContent = project.activeFilePath != null
    ? (project.files[project.activeFilePath] ?? '')
    : null
  const pending  = project.pendingChange
  const fileList = Object.keys(project.files).sort()

  // Enter edit mode with current content
  const startEdit = () => {
    setEditText(activeContent ?? '')
    setEditMode(true)
    setTimeout(() => textareaRef.current?.focus(), 50)
  }

  const cancelEdit = () => { setEditMode(false); setEditText('') }

  const saveEdit = async () => {
    const path = project.activeFilePath
    if (!path || !project.projectId) return
    setSaving(true); setErr(null)
    try {
      await writeFile(project.projectId, path, editText)
      const original = project.files[path] ?? ''
      const diffLines = computeDiff(original, editText)
      store.setFileContent(path, editText)
      store.setPendingChange({ path, originalContent: original, newContent: editText, diffLines })
      setEditMode(false)
    } catch (e: any) { setErr(e.message ?? String(e)) }
    finally { setSaving(false) }
  }

  const saveNew = async () => {
    const path = newName.trim()
    if (!path || !project.projectId) return
    setSaving(true); setErr(null)
    try {
      await writeFile(project.projectId, path, newText)
      const diffLines = computeDiff('', newText)
      store.setFileContent(path, newText)
      store.setPendingChange({ path, originalContent: '', newContent: newText, diffLines })
      store.setActiveFile(path)
      setNewMode(false); setNewName(''); setNewText('')
    } catch (e: any) { setErr(e.message ?? String(e)) }
    finally { setSaving(false) }
  }

  const onAccept = async () => {
    if (!pending || !project.projectId) return
    setSaving(true); setErr(null)
    try {
      await writeFile(project.projectId, pending.path, pending.newContent)
      store.acceptPendingChange()
    } catch (e: any) { setErr(e.message ?? String(e)) }
    finally { setSaving(false) }
  }

  const onTabClick = (path: string) => {
    store.setActiveFile(path)
    setEditMode(false)
    // Inject file context into chat input
    if (onFileContext) onFileContext(`@${path} `)
  }

  return (
    <div
      className="fixed top-14 right-0 bottom-0 flex flex-col"
      style={{ width: '420px', background: 'var(--surface)', borderLeft: '1px solid var(--border)', zIndex: 20 }}
    >
      {/* Header */}
      <div className="flex items-center gap-2 px-3 py-2 shrink-0 text-xs"
           style={{ borderBottom: '1px solid var(--border)', color: 'var(--mute)' }}>
        <svg className="w-3.5 h-3.5 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round"
            d="M3 7a2 2 0 012-2h4l2 2h8a2 2 0 012 2v9a2 2 0 01-2 2H5a2 2 0 01-2-2V7z"/>
        </svg>
        <span className="font-medium flex-1 truncate" style={{ color: 'var(--text)' }}>{project.projectId}</span>
        {/* New file button */}
        <button onClick={() => { setNewMode(true); setEditMode(false) }}
                className="px-2 py-0.5 rounded text-[10px] cursor-pointer transition"
                style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)' }}
                title="Yeni dosya">
          + Yeni
        </button>
        <button onClick={() => store.setProjectId(null)}
                className="px-2 py-0.5 rounded text-[10px] cursor-pointer"
                style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)' }}>
          ✕
        </button>
      </div>

      {/* File tabs */}
      {fileList.length > 0 && (
        <div className="flex overflow-x-auto shrink-0 scrollbar-thin"
             style={{ borderBottom: '1px solid var(--border)' }}>
          {fileList.map(path => {
            const active = project.activeFilePath === path
            return (
              <button key={path} onClick={() => onTabClick(path)}
                      className="px-3 py-1.5 text-[11px] whitespace-nowrap shrink-0 cursor-pointer transition border-r flex items-center gap-1.5"
                      style={{
                        borderColor: 'var(--border)',
                        background:  active ? 'var(--surface-hi)' : 'transparent',
                        color:       active ? 'var(--accent-hi)'  : 'var(--mute)',
                        borderBottom: active ? '2px solid var(--accent)' : '2px solid transparent',
                      }}
                      title="Tıkla → chat'e ekle">
                {path.split('/').pop()}
              </button>
            )
          })}
        </div>
      )}

      {/* Pending change banner */}
      {pending && !editMode && (
        <div className="shrink-0 px-3 py-2 flex items-center gap-2"
             style={{ background: 'rgba(251,191,36,0.08)', borderBottom: '1px solid rgba(251,191,36,0.3)' }}>
          <span className="text-xs font-medium flex-1 truncate" style={{ color: '#fbbf24' }}>
            ✏️ <span className="font-mono">{pending.path}</span>
          </span>
          <button onClick={onAccept} disabled={saving}
                  className="px-3 py-1 rounded text-xs font-semibold cursor-pointer disabled:opacity-50"
                  style={{ background: '#34a853', color: '#fff' }}>
            {saving ? '...' : 'Tamam'}
          </button>
          <button onClick={() => store.setPendingChange(null)}
                  className="px-3 py-1 rounded text-xs cursor-pointer"
                  style={{ border: '1px solid var(--border)', color: 'var(--mute)' }}>
            Reddet
          </button>
        </div>
      )}

      {err && (
        <div className="shrink-0 px-3 py-1.5 text-xs"
             style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335' }}>{err}</div>
      )}

      {/* New file form */}
      {newMode && (
        <div className="shrink-0 p-3 space-y-2" style={{ borderBottom: '1px solid var(--border)', background: 'var(--surface-2)' }}>
          <div className="text-xs font-medium" style={{ color: 'var(--text)' }}>Yeni Dosya</div>
          <input value={newName} onChange={e => setNewName(e.target.value)}
                 placeholder="kredi-raporu.sql"
                 className="w-full px-2 py-1.5 rounded text-xs outline-none font-mono"
                 style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
          <textarea value={newText} onChange={e => setNewText(e.target.value)}
                    placeholder="Dosya içeriği..."
                    rows={6}
                    className="w-full px-2 py-1.5 rounded text-xs outline-none font-mono resize-y"
                    style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
          <div className="flex gap-2">
            <button onClick={saveNew} disabled={saving || !newName.trim()}
                    className="px-3 py-1.5 rounded text-xs font-medium cursor-pointer disabled:opacity-50"
                    style={{ background: 'var(--accent)', color: '#0b1929' }}>
              {saving ? '...' : 'Kaydet'}
            </button>
            <button onClick={() => { setNewMode(false); setNewName(''); setNewText('') }}
                    className="px-3 py-1.5 rounded text-xs cursor-pointer"
                    style={{ background: 'var(--surface-hi)', color: 'var(--mute)' }}>
              İptal
            </button>
          </div>
        </div>
      )}

      {/* Content area */}
      <div className="flex-1 overflow-auto">
        {editMode && project.activeFilePath ? (
          <div className="p-3 flex flex-col h-full gap-2">
            <div className="flex items-center justify-between">
              <span className="text-[10px] uppercase tracking-wider font-semibold" style={{ color: 'var(--mute)' }}>
                Düzenle — {project.activeFilePath}
              </span>
              <div className="flex gap-1.5">
                <button onClick={saveEdit} disabled={saving}
                        className="px-3 py-1 rounded text-xs font-medium cursor-pointer disabled:opacity-50"
                        style={{ background: '#34a853', color: '#fff' }}>
                  {saving ? '...' : 'Kaydet'}
                </button>
                <button onClick={cancelEdit}
                        className="px-3 py-1 rounded text-xs cursor-pointer"
                        style={{ border: '1px solid var(--border)', color: 'var(--mute)' }}>
                  İptal
                </button>
              </div>
            </div>
            <textarea
              ref={textareaRef}
              value={editText}
              onChange={e => setEditText(e.target.value)}
              className="flex-1 p-2 text-xs font-mono rounded outline-none resize-none"
              style={{ background: 'var(--bg)', border: '1px solid var(--accent)', color: 'var(--text)', minHeight: 200 }}
            />
          </div>
        ) : pending ? (
          <div className="p-3">
            <div className="text-[10px] mb-2 uppercase tracking-wider font-semibold" style={{ color: 'var(--mute)' }}>
              {langFromPath(pending.path)} — Diff
            </div>
            <DiffView lines={pending.diffLines} />
          </div>
        ) : project.activeFilePath && activeContent !== null ? (
          <div className="p-3">
            <div className="flex items-center justify-between mb-2">
              <span className="text-[10px] uppercase tracking-wider font-semibold" style={{ color: 'var(--mute)' }}>
                {langFromPath(project.activeFilePath)}
              </span>
              <div className="flex items-center gap-2">
                <span className="text-[10px]" style={{ color: 'var(--mute)' }}>
                  {activeContent.split('\n').length} satır
                </span>
                <button onClick={startEdit}
                        className="flex items-center gap-1 px-2 py-0.5 rounded text-[10px] cursor-pointer transition"
                        style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text)' }}
                        title="Düzenle">
                  <svg className="w-2.5 h-2.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                    <path strokeLinecap="round" strokeLinejoin="round"
                      d="M15.232 5.232l3.536 3.536m-2.036-5.036a2.5 2.5 0 113.536 3.536L6.5 21.036H3v-3.572L16.732 3.732z"/>
                  </svg>
                  Düzenle
                </button>
              </div>
            </div>
            <pre className="text-xs font-mono leading-5 whitespace-pre overflow-x-auto select-text"
                 style={{ color: 'var(--text)' }}>
              {activeContent}
            </pre>
          </div>
        ) : (
          <div className="flex flex-col items-center justify-center h-full gap-3 text-sm" style={{ color: 'var(--mute)' }}>
            {fileList.length === 0
              ? <>
                  <span>Henüz dosya yok</span>
                  <button onClick={() => setNewMode(true)}
                          className="px-3 py-1.5 rounded text-xs cursor-pointer"
                          style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text)' }}>
                    + Dosya Oluştur
                  </button>
                </>
              : 'Dosya seçin'}
          </div>
        )}
      </div>
    </div>
  )
}
