import { useEffect, useRef, useState } from 'react'
import {
  listSkills, getSkill, uploadSkills, deleteSkill,
  importAnthropicSkills, ANTHROPIC_SKILLS, setSkillOrder,
  listSkillExamples, createSkillExample, updateSkillExample, deleteSkillExample,
} from '../../../api/admin'
import type { SkillRow, SkillExample, ImportAnthropicResult } from '../../../api/admin'
import { formatBytes } from './_shared'

export default function SkillsTab() {
  const [skills, setSkills]     = useState<SkillRow[]>([])
  const [selected, setSelected] = useState<string | null>(null)
  const [content, setContent]   = useState<string>('')
  const [loading, setLoading]   = useState(false)
  const [error, setError]       = useState<string | null>(null)
  const [uploadMsg, setUploadMsg] = useState<string | null>(null)
  // Few-shot examples state
  const [examples,    setExamples]    = useState<SkillExample[]>([])
  const [exLoading,   setExLoading]   = useState(false)
  const [showExForm,  setShowExForm]  = useState(false)
  const [editEx,      setEditEx]      = useState<SkillExample | null>(null)
  const [exUser,      setExUser]      = useState('')
  const [exAssistant, setExAssistant] = useState('')
  const [exSaving,    setExSaving]    = useState(false)
  const fileRef = useRef<HTMLInputElement>(null)
  // Anthropic Import modal
  const [showImport,    setShowImport]    = useState(false)
  const [importSelected, setImportSelected] = useState<Set<string>>(new Set())
  const [importOverwrite, setImportOverwrite] = useState(false)
  const [importing,      setImporting]     = useState(false)
  const [importResult,   setImportResult]  = useState<ImportAnthropicResult | null>(null)

  const reload = () => listSkills().then(setSkills).catch(e => setError(e.message ?? String(e)))

  useEffect(() => { reload() }, [])

  const loadExamples = async (id: string) => {
    setExLoading(true)
    try { setExamples(await listSkillExamples(id)) }
    catch { setExamples([]) }
    finally { setExLoading(false) }
  }

  const openSkill = async (id: string) => {
    setSelected(id)
    setShowExForm(false); setEditEx(null); setExUser(''); setExAssistant('')
    loadExamples(id)
    setLoading(true)
    setError(null)
    try {
      setContent(await getSkill(id))
    } catch (e: any) {
      setError(e.message ?? String(e))
      setContent('')
    } finally {
      setLoading(false)
    }
  }

  const onUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = e.target.files
    if (!files || files.length === 0) return
    setError(null)
    try {
      const results = await uploadSkills(files)
      const ok  = results.filter(r => r.ok).length
      const bad = results.filter(r => !r.ok)
      setUploadMsg(`${ok} skill(s) uploaded.${bad.length > 0 ? ' Errors: ' + bad.map(r => r.error).join(', ') : ''}`)
      reload()
    } catch (e: any) {
      setError(e.message ?? String(e))
    } finally {
      e.target.value = ''
    }
  }

  const onDelete = async (id: string) => {
    if (!confirm(`"${id}" skill'ini sil?`)) return
    setError(null)
    try {
      await deleteSkill(id)
      if (selected === id) { setSelected(null); setContent('') }
      reload()
    } catch (e: any) {
      setError(e.message ?? String(e))
    }
  }

  return (
    <section className="space-y-5">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h2 className="text-lg font-medium">Skills</h2>
          <p className="text-xs mt-1" style={{ color: 'var(--mute)' }}>
            System prompts loaded from the Skills directory. Click to view.
          </p>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          <input ref={fileRef} type="file" accept=".md,.zip" multiple className="hidden" onChange={onUpload} />
          <button
            onClick={() => { setShowImport(true); setImportResult(null) }}
            className="px-3 py-1.5 rounded-lg text-xs font-medium cursor-pointer transition"
            style={{ background: 'rgba(138,180,248,0.15)', color: 'var(--accent-hi)', border: '1px solid rgba(138,180,248,0.3)' }}
            title="anthropics/skills GitHub repo'sundan resmi skill'leri indir"
          >
            📥 Anthropic Import
          </button>
          <button
            onClick={() => fileRef.current?.click()}
            className="px-3 py-1.5 rounded-lg text-xs font-medium cursor-pointer transition"
            style={{ background: 'var(--accent)', color: '#0b1929' }}
          >
            + Skill Yükle (.md/.zip)
          </button>
        </div>
      </div>

      {uploadMsg && (
        <div className="rounded-md px-3 py-2 text-xs"
             style={{ background: 'rgba(52,168,83,0.1)', color: '#34a853', border: '1px solid rgba(52,168,83,0.3)' }}>
          {uploadMsg}
        </div>
      )}

      {error && (
        <div className="rounded-md px-3 py-2 text-xs"
             style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}>
          {error}
        </div>
      )}

      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        {/* List */}
        <div className="rounded-xl overflow-hidden md:col-span-1"
             style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
          <div className="px-3 py-2 text-[11px] uppercase tracking-wider font-semibold"
               style={{ color: 'var(--mute)', borderBottom: '1px solid var(--border)' }}>
            {skills.length} skill{skills.length === 1 ? '' : 's'}
          </div>
          <div className="px-3 py-1.5 text-[10px] flex items-center justify-between"
               style={{ background: 'var(--surface-2)', color: 'var(--mute)', borderBottom: '1px solid var(--border)' }}>
            <span>SIRA</span>
            <span>SKILL</span>
            <span>BOYUT</span>
          </div>
          <ul className="max-h-[60vh] overflow-y-auto">
            {skills.map(s => {
              const active = selected === s.id
              return (
                <li key={s.id}>
                  <div
                    className="flex items-center group"
                    style={{ borderBottom: '1px solid var(--border)', background: active ? 'rgba(138,180,248,0.15)' : 'transparent' }}
                  >
                    {/* Order input — inline edit */}
                    <SkillOrderInput
                      skillId={s.id}
                      initialOrder={s.order ?? 999}
                      onSaved={(newOrder) => setSkills(prev => {
                        const next = prev.map(x => x.id === s.id ? { ...x, order: newOrder } : x)
                        // Re-sort: order asc, name asc
                        next.sort((a, b) => (a.order ?? 999) - (b.order ?? 999) || (a.name || a.id).localeCompare(b.name || b.id))
                        return next
                      })}
                    />
                    <button
                      onClick={() => openSkill(s.id)}
                      className="flex-1 text-left px-3 py-2.5 text-sm cursor-pointer transition flex items-center justify-between gap-2 min-w-0"
                      style={{ color: active ? 'var(--accent-hi)' : 'var(--text)' }}
                    >
                      <span className="truncate flex items-center gap-1.5 min-w-0">
                        <span className="truncate">{s.name || s.id}</span>
                        {s.isFolder && (
                          <span className="px-1 py-0.5 text-[9px] rounded shrink-0"
                                style={{ background: 'rgba(52,168,83,0.15)', color: '#34a853', border: '1px solid rgba(52,168,83,0.25)' }}
                                title={`Folder skill: ${s.referenceCount ?? 0} referans dosyası`}>
                            📁 {s.referenceCount ?? 0}
                          </span>
                        )}
                      </span>
                      <span className="text-[10px] shrink-0" style={{ color: 'var(--mute)' }}>
                        {formatBytes(s.size)}
                      </span>
                    </button>
                    <button
                      onClick={() => onDelete(s.id)}
                      className="opacity-0 group-hover:opacity-100 px-2 py-2.5 cursor-pointer transition shrink-0"
                      style={{ color: '#ea4335' }}
                      title="Sil"
                    >
                      <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6M1 7h22M9 7V4a1 1 0 011-1h4a1 1 0 011 1v3"/>
                      </svg>
                    </button>
                  </div>
                </li>
              )
            })}
            {skills.length === 0 && (
              <li className="px-3 py-6 text-center text-xs" style={{ color: 'var(--mute)' }}>
                No skills found.
              </li>
            )}
          </ul>
        </div>

        {/* Viewer */}
        <div className="rounded-xl md:col-span-2 flex flex-col"
             style={{ background: 'var(--surface)', border: '1px solid var(--border)', minHeight: '60vh' }}>
          <div className="px-3 py-2 text-xs flex items-center justify-between"
               style={{ color: 'var(--mute)', borderBottom: '1px solid var(--border)' }}>
            <span className="truncate">{selected ?? 'Select a skill to view'}</span>
            {loading && <span>Loading…</span>}
          </div>
          <pre className="flex-1 overflow-auto p-4 text-xs whitespace-pre-wrap font-mono"
               style={{ color: 'var(--text-2)', maxHeight: '40vh' }}>
            {content || (selected ? '' : 'No skill selected.')}
          </pre>

          {/* ── Few-Shot Examples Panel ──────────────────────────────── */}
          {selected && (
            <div style={{ borderTop: '1px solid var(--border)' }}>
              <div className="px-3 py-2 flex items-center justify-between"
                   style={{ borderBottom: examples.length > 0 || showExForm ? '1px solid var(--border)' : 'none' }}>
                <span className="text-xs font-semibold" style={{ color: 'var(--mute)' }}>
                  FEW-SHOT ÖRNEKLER {examples.length > 0 && `(${examples.length})`}
                </span>
                {!showExForm && !editEx && (
                  <button onClick={() => { setShowExForm(true); setExUser(''); setExAssistant('') }}
                          className="text-[10px] px-2 py-0.5 rounded cursor-pointer"
                          style={{ background: 'var(--accent)', color: '#0b1929' }}>
                    + Ekle
                  </button>
                )}
              </div>

              {/* Example list */}
              {exLoading ? (
                <div className="px-3 py-3 text-xs" style={{ color: 'var(--mute)' }}>Yükleniyor…</div>
              ) : (
                <div className="max-h-64 overflow-y-auto scrollbar-thin">
                  {examples.map((ex, i) => (
                    editEx?.id === ex.id ? (
                      // Inline edit form
                      <ExampleForm key={ex.id}
                        userVal={exUser} assistantVal={exAssistant}
                        saving={exSaving}
                        onUserChange={setExUser} onAssistantChange={setExAssistant}
                        onSave={async () => {
                          if (!exUser.trim() || !exAssistant.trim()) return
                          setExSaving(true)
                          try {
                            await updateSkillExample(selected, ex.id, exUser, exAssistant)
                            setEditEx(null); loadExamples(selected)
                          } catch (e: any) { setError(e.message) }
                          finally { setExSaving(false) }
                        }}
                        onCancel={() => setEditEx(null)}
                      />
                    ) : (
                      <div key={ex.id} className="px-3 py-2 group flex gap-2 items-start text-xs"
                           style={{ borderBottom: i < examples.length-1 ? '1px solid var(--border)' : 'none' }}>
                        <span className="shrink-0 w-4 text-center font-bold" style={{ color: 'var(--mute)' }}>{i+1}</span>
                        <div className="flex-1 min-w-0 space-y-1">
                          <div className="flex items-start gap-1">
                            <span className="shrink-0 text-[9px] uppercase font-semibold px-1 rounded mt-0.5"
                                  style={{ background: 'rgba(138,180,248,0.15)', color: 'var(--accent-hi)' }}>U</span>
                            <span className="truncate" style={{ color: 'var(--text-2)' }}>{ex.userMessage}</span>
                          </div>
                          <div className="flex items-start gap-1">
                            <span className="shrink-0 text-[9px] uppercase font-semibold px-1 rounded mt-0.5"
                                  style={{ background: 'rgba(52,168,83,0.15)', color: '#34a853' }}>A</span>
                            <span className="truncate" style={{ color: 'var(--text-2)' }}>{ex.assistantMessage}</span>
                          </div>
                        </div>
                        <div className="flex gap-1 opacity-0 group-hover:opacity-100 shrink-0">
                          <button onClick={() => { setEditEx(ex); setExUser(ex.userMessage); setExAssistant(ex.assistantMessage); setShowExForm(false) }}
                                  className="px-1.5 py-0.5 rounded text-[10px] cursor-pointer"
                                  style={{ background: 'var(--surface-hi)', color: 'var(--text-2)' }}>✏️</button>
                          <button onClick={async () => {
                                    if (!confirm('Bu örneği sil?')) return
                                    await deleteSkillExample(selected, ex.id).catch(e => setError(e.message))
                                    loadExamples(selected)
                                  }}
                                  className="px-1.5 py-0.5 rounded text-[10px] cursor-pointer"
                                  style={{ color: '#ea4335' }}>✕</button>
                        </div>
                      </div>
                    )
                  ))}
                  {examples.length === 0 && !showExForm && (
                    <div className="px-3 py-4 text-xs text-center" style={{ color: 'var(--mute)' }}>
                      Henüz örnek yok. + Ekle ile başlayın.
                    </div>
                  )}
                </div>
              )}

              {/* New example form */}
              {showExForm && (
                <ExampleForm
                  userVal={exUser} assistantVal={exAssistant} saving={exSaving}
                  onUserChange={setExUser} onAssistantChange={setExAssistant}
                  onSave={async () => {
                    if (!exUser.trim() || !exAssistant.trim()) return
                    setExSaving(true)
                    try {
                      await createSkillExample(selected, exUser, exAssistant)
                      setShowExForm(false); setExUser(''); setExAssistant('')
                      loadExamples(selected)
                    } catch (e: any) { setError(e.message) }
                    finally { setExSaving(false) }
                  }}
                  onCancel={() => { setShowExForm(false); setExUser(''); setExAssistant('') }}
                />
              )}
            </div>
          )}
        </div>
      </div>

      {/* Anthropic Skills Import Modal */}
      {showImport && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4"
             style={{ background: 'rgba(0,0,0,0.6)', backdropFilter: 'blur(4px)' }}
             onClick={e => { if (e.target === e.currentTarget && !importing) setShowImport(false) }}>
          <div className="w-full max-w-2xl rounded-2xl overflow-hidden shadow-2xl flex flex-col"
               style={{ background: 'var(--bg)', border: '1px solid var(--border)', maxHeight: '85vh' }}>
            <div className="px-5 py-4 flex items-center gap-3 shrink-0"
                 style={{ borderBottom: '1px solid var(--border)', background: 'var(--surface)' }}>
              <span className="text-2xl">📥</span>
              <div className="flex-1">
                <div className="font-semibold" style={{ color: 'var(--text)' }}>Anthropic Skills Import</div>
                <div className="text-xs" style={{ color: 'var(--mute)' }}>
                  anthropics/skills GitHub repo&apos;sundan direkt indir (SKILL.md + referans .md dosyaları)
                </div>
              </div>
              <button onClick={() => !importing && setShowImport(false)}
                      disabled={importing}
                      className="w-8 h-8 rounded-full flex items-center justify-center cursor-pointer text-lg disabled:opacity-30"
                      style={{ color: 'var(--mute)' }}>×</button>
            </div>

            <div className="flex-1 overflow-y-auto p-5 space-y-3">
              <div className="flex items-center gap-3 text-xs flex-wrap" style={{ color: 'var(--mute)' }}>
                <button onClick={() => setImportSelected(new Set(Object.keys(ANTHROPIC_SKILLS)))}
                        className="cursor-pointer hover:underline"
                        style={{ color: 'var(--accent-hi)' }}>
                  Tümünü seç (17)
                </button>
                <span>·</span>
                <button onClick={() => setImportSelected(new Set())}
                        className="cursor-pointer hover:underline">
                  Tümünü kaldır
                </button>
                <span>·</span>
                <label className="flex items-center gap-1.5 cursor-pointer">
                  <input type="checkbox" checked={importOverwrite}
                         onChange={e => setImportOverwrite(e.target.checked)} />
                  Mevcut olanların üzerine yaz
                </label>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-2 gap-2">
                {Object.entries(ANTHROPIC_SKILLS).map(([id, info]) => {
                  const checked = importSelected.has(id)
                  return (
                    <label key={id}
                           className="flex items-start gap-2 p-2 rounded-lg cursor-pointer text-xs"
                           style={{
                             background: checked ? 'rgba(138,180,248,0.10)' : 'var(--surface-2)',
                             border: `1px solid ${checked ? 'rgba(138,180,248,0.35)' : 'var(--border)'}`,
                           }}>
                      <input type="checkbox" checked={checked} className="mt-0.5 cursor-pointer"
                             onChange={e => setImportSelected(prev => {
                               const next = new Set(prev)
                               if (e.target.checked) next.add(id); else next.delete(id)
                               return next
                             })} />
                      <div className="min-w-0 flex-1">
                        <div className="font-medium flex items-center gap-1.5" style={{ color: 'var(--text)' }}>
                          <span className="font-mono">{id}</span>
                          {info.hasRefs && (
                            <span className="px-1 py-0.5 text-[9px] rounded"
                                  style={{ background: 'rgba(52,168,83,0.15)', color: '#34a853' }}>
                              +refs
                            </span>
                          )}
                        </div>
                        <div className="truncate" style={{ color: 'var(--mute)' }}>{info.description}</div>
                      </div>
                    </label>
                  )
                })}
              </div>

              {importResult && (
                <div className="rounded-xl p-3 space-y-1 text-xs"
                     style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
                  <div className="font-semibold mb-2" style={{ color: 'var(--text)' }}>
                    İçe aktarıldı: {importResult.imported} / {importResult.results.length}
                  </div>
                  <div className="max-h-48 overflow-y-auto space-y-0.5">
                    {importResult.results.map((r, i) => (
                      <div key={i} className="flex items-center gap-2"
                           style={{ color: r.ok ? '#34a853' : '#ea4335' }}>
                        <span>{r.ok ? '✓' : '✕'}</span>
                        <span className="font-mono">{r.skill}</span>
                        <span style={{ color: 'var(--mute)' }}>
                          {r.action ?? r.error}
                          {r.files ? ` (${r.files} dosya)` : ''}
                        </span>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>

            <div className="px-5 py-4 flex gap-2 shrink-0"
                 style={{ borderTop: '1px solid var(--border)', background: 'var(--surface)' }}>
              <button onClick={async () => {
                        if (importSelected.size === 0) return
                        setImporting(true); setImportResult(null); setError(null)
                        try {
                          const res = await importAnthropicSkills(Array.from(importSelected), importOverwrite)
                          setImportResult(res)
                          reload()
                        } catch (e: any) {
                          setError(`Import hatası: ${e.message}`)
                        } finally {
                          setImporting(false)
                        }
                      }}
                      disabled={importing || importSelected.size === 0}
                      className="flex-1 py-2 rounded-lg text-sm font-semibold cursor-pointer disabled:opacity-50"
                      style={{ background: 'var(--accent)', color: '#0b1929' }}>
                {importing
                  ? `İndiriliyor… (${importSelected.size} skill)`
                  : `📥 ${importSelected.size > 0 ? importSelected.size : 'Seçili'} Skill İndir`}
              </button>
              <button onClick={() => !importing && setShowImport(false)}
                      disabled={importing}
                      className="px-4 py-2 rounded-lg text-sm cursor-pointer disabled:opacity-50"
                      style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
                Kapat
              </button>
            </div>
          </div>
        </div>
      )}
    </section>
  )
}

// Small inline-editable order input for skill list
function SkillOrderInput({ skillId, initialOrder, onSaved }: {
  skillId: string
  initialOrder: number
  onSaved: (order: number) => void
}) {
  const [val, setVal] = useState(String(initialOrder))
  const [saving, setSaving] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  useEffect(() => { setVal(String(initialOrder)) }, [initialOrder])

  const commit = async () => {
    const n = parseInt(val, 10)
    if (isNaN(n) || n === initialOrder) { setVal(String(initialOrder)); return }
    setSaving(true); setErr(null)
    try {
      const res = await setSkillOrder(skillId, n)
      onSaved(res.order)
    } catch (e: any) {
      setErr(e.message)
      setVal(String(initialOrder))
    } finally {
      setSaving(false)
    }
  }

  return (
    <input type="number" min={0} max={9999}
           value={val}
           onChange={e => setVal(e.target.value)}
           onBlur={commit}
           onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }}
           disabled={saving}
           title={err ?? 'Skill sırası (düşük = önce). Enter veya Tab ile kaydet.'}
           className="w-12 ml-2 my-1 px-1 py-0.5 text-[11px] text-center rounded outline-none font-mono shrink-0"
           style={{
             background: err ? 'rgba(234,67,53,0.12)' : 'var(--input-bg)',
             border: `1px solid ${err ? '#ea4335' : 'var(--border)'}`,
             color: 'var(--text)',
             opacity: saving ? 0.5 : 1,
           }} />
  )
}

function ExampleForm({ userVal, assistantVal, saving, onUserChange, onAssistantChange, onSave, onCancel }: {
  userVal: string; assistantVal: string; saving: boolean
  onUserChange: (v: string) => void; onAssistantChange: (v: string) => void
  onSave: () => void; onCancel: () => void
}) {
  return (
    <div className="px-3 py-3 space-y-2" style={{ borderTop: '1px solid var(--border)', background: 'var(--surface-2)' }}>
      <div>
        <div className="text-[10px] font-semibold mb-1 uppercase" style={{ color: 'var(--accent-hi)' }}>Kullanıcı mesajı</div>
        <textarea value={userVal} onChange={e => onUserChange(e.target.value)} rows={2}
                  placeholder="Örnek kullanıcı sorusu…"
                  className="w-full rounded px-2 py-1.5 text-xs outline-none resize-none"
                  style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
      </div>
      <div>
        <div className="text-[10px] font-semibold mb-1 uppercase" style={{ color: '#34a853' }}>Asistan yanıtı</div>
        <textarea value={assistantVal} onChange={e => onAssistantChange(e.target.value)} rows={3}
                  placeholder="Beklenen örnek yanıt…"
                  className="w-full rounded px-2 py-1.5 text-xs outline-none resize-none"
                  style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
      </div>
      <div className="flex gap-2">
        <button onClick={onSave} disabled={saving || !userVal.trim() || !assistantVal.trim()}
                className="flex-1 py-1 rounded text-xs font-semibold cursor-pointer disabled:opacity-50"
                style={{ background: 'var(--accent)', color: '#0b1929' }}>
          {saving ? 'Kaydediliyor…' : '✓ Kaydet'}
        </button>
        <button onClick={onCancel} className="px-3 py-1 rounded text-xs cursor-pointer"
                style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
          İptal
        </button>
      </div>
    </div>
  )
}
