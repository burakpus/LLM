import { useEffect, useState } from 'react'
import { listTemplates, createTemplate, updateTemplate, deleteTemplate } from '../../../api/admin'
import type { PromptTemplate } from '../../../api/admin'

// Extract {{variable}} names from content
function extractVars(content: string): string[] {
  const matches = content.matchAll(/\{\{(\w+)\}\}/g)
  return [...new Set([...matches].map(m => m[1]))]
}

export default function TemplatesTab() {
  const [templates, setTemplates]   = useState<PromptTemplate[]>([])
  const [selected,  setSelected]    = useState<PromptTemplate | null>(null)
  const [isNew,     setIsNew]       = useState(false)
  const [name,      setName]        = useState('')
  const [collection,setCollection]  = useState('')
  const [content,   setContent]     = useState('')
  const [loading,   setLoading]     = useState(false)
  const [saving,    setSaving]      = useState(false)
  const [error,     setError]       = useState<string | null>(null)
  const [msg,       setMsg]         = useState<string | null>(null)

  const load = async () => {
    setLoading(true)
    try { setTemplates(await listTemplates()) }
    catch (e: any) { setError(e.message) }
    finally { setLoading(false) }
  }

  useEffect(() => { load() }, [])

  const openNew = () => {
    setSelected(null); setIsNew(true)
    setName(''); setCollection(''); setContent(''); setError(null); setMsg(null)
  }

  const openEdit = (t: PromptTemplate) => {
    setSelected(t); setIsNew(false)
    setName(t.name); setCollection(t.collection); setContent(t.content)
    setError(null); setMsg(null)
  }

  const onSave = async () => {
    if (!name.trim() || !content.trim()) { setError('Ad ve içerik zorunludur'); return }
    setSaving(true); setError(null)
    try {
      if (isNew) {
        await createTemplate(name.trim(), content, collection.trim())
        setMsg('Şablon oluşturuldu.')
      } else if (selected) {
        await updateTemplate(selected.id, name.trim(), content, collection.trim())
        setMsg('Şablon güncellendi.')
      }
      await load()
      setIsNew(false); setSelected(null)
    } catch (e: any) { setError(e.message) }
    finally { setSaving(false) }
  }

  const onDelete = async (t: PromptTemplate) => {
    if (!confirm(`"${t.name}" şablonunu sil?`)) return
    try {
      await deleteTemplate(t.id)
      if (selected?.id === t.id) { setSelected(null); setIsNew(false) }
      await load()
    } catch (e: any) { setError(e.message) }
  }

  const vars      = extractVars(content)
  const hasForm   = isNew || selected !== null
  const collections = [...new Set(templates.map(t => t.collection).filter(Boolean))]

  return (
    <section className="space-y-5">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h2 className="text-lg font-medium">Prompt Şablonları</h2>
          <p className="text-xs mt-1" style={{ color: 'var(--mute)' }}>
            {'{{değişken}} sözdizimi ile şablonlar. Chat\'te / yazarak çağrılır.'}
          </p>
        </div>
        <button onClick={openNew}
                className="px-3 py-1.5 rounded-lg text-xs font-medium cursor-pointer shrink-0"
                style={{ background: 'var(--accent)', color: '#0b1929' }}>
          + Yeni Şablon
        </button>
      </div>

      {error && <div className="rounded-md px-3 py-2 text-xs" style={{ background: 'rgba(234,67,53,0.1)', color: '#ea4335', border: '1px solid rgba(234,67,53,0.3)' }}>{error}</div>}
      {msg   && <div className="rounded-md px-3 py-2 text-xs" style={{ background: 'rgba(52,168,83,0.1)',  color: '#34a853', border: '1px solid rgba(52,168,83,0.3)'  }}>{msg}</div>}

      <div className="grid grid-cols-1 md:grid-cols-5 gap-4">
        {/* List */}
        <div className="md:col-span-2 rounded-xl overflow-hidden"
             style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
          <div className="px-3 py-2 text-[11px] uppercase tracking-wider font-semibold flex items-center justify-between"
               style={{ color: 'var(--mute)', borderBottom: '1px solid var(--border)' }}>
            <span>{templates.length} şablon</span>
            {loading && <span>Yükleniyor…</span>}
          </div>
          {collections.length > 0 ? (
            // Grouped by collection
            [...collections, ''].filter(c => templates.some(t => t.collection === c)).map(col => (
              <div key={col || '__none__'}>
                {col && (
                  <div className="px-3 py-1 text-[10px] uppercase tracking-wider"
                       style={{ color: 'var(--mute)', background: 'var(--surface-2)', borderBottom: '1px solid var(--border)' }}>
                    {col}
                  </div>
                )}
                {templates.filter(t => t.collection === col).map(tmpl => (
                  <TemplateRow key={tmpl.id} tmpl={tmpl} active={selected?.id === tmpl.id}
                               onEdit={() => openEdit(tmpl)} onDelete={() => onDelete(tmpl)} />
                ))}
              </div>
            ))
          ) : (
            templates.map(tmpl => (
              <TemplateRow key={tmpl.id} tmpl={tmpl} active={selected?.id === tmpl.id}
                           onEdit={() => openEdit(tmpl)} onDelete={() => onDelete(tmpl)} />
            ))
          )}
          {!loading && templates.length === 0 && (
            <div className="px-3 py-8 text-center text-xs" style={{ color: 'var(--mute)' }}>
              Henüz şablon yok. + Yeni Şablon ile başlayın.
            </div>
          )}
        </div>

        {/* Form */}
        {hasForm ? (
          <div className="md:col-span-3 rounded-xl p-5 space-y-4"
               style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
            <div className="text-sm font-semibold" style={{ color: 'var(--text)' }}>
              {isNew ? '+ Yeni Şablon' : `Düzenle: ${selected?.name}`}
            </div>

            <div className="grid grid-cols-2 gap-3">
              <label className="block col-span-2 md:col-span-1">
                <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Ad *</div>
                <input value={name} onChange={e => setName(e.target.value)} placeholder="SQL Sorgu Şablonu"
                       className="w-full rounded-md px-3 py-2 text-sm outline-none"
                       style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
              </label>
              <label className="block col-span-2 md:col-span-1">
                <div className="text-xs mb-1" style={{ color: 'var(--mute)' }}>Koleksiyon (isteğe bağlı)</div>
                <input value={collection} onChange={e => setCollection(e.target.value)} placeholder="SQL, Genel, CRM…"
                       className="w-full rounded-md px-3 py-2 text-sm outline-none"
                       style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
              </label>
            </div>

            <label className="block">
              <div className="text-xs mb-1 flex items-center justify-between" style={{ color: 'var(--mute)' }}>
                <span>İçerik * <span style={{ opacity: 0.6 }}>— {`{{değişken}}`} kullanabilirsiniz</span></span>
                <span style={{ color: 'var(--mute-2)' }}>{content.length} karakter</span>
              </div>
              <textarea value={content} onChange={e => setContent(e.target.value)}
                        rows={10} placeholder={"{{tablo_adı}} tablosundan {{koşul}} koşuluna göre sorgu yaz."}
                        className="w-full rounded-md px-3 py-2 text-sm outline-none resize-y scrollbar-thin font-mono"
                        style={{ background: 'var(--input-bg)', border: '1px solid var(--border)', color: 'var(--text)' }} />
            </label>

            {/* Variable chips */}
            {vars.length > 0 && (
              <div>
                <div className="text-xs mb-1.5" style={{ color: 'var(--mute)' }}>Tespit edilen değişkenler:</div>
                <div className="flex flex-wrap gap-1.5">
                  {vars.map(v => (
                    <span key={v} className="px-2 py-0.5 rounded-full text-xs font-mono"
                          style={{ background: 'rgba(138,180,248,0.15)', color: 'var(--accent-hi)', border: '1px solid rgba(138,180,248,0.3)' }}>
                      {`{{${v}}}`}
                    </span>
                  ))}
                </div>
              </div>
            )}

            <div className="flex gap-2 pt-1">
              <button onClick={onSave} disabled={saving}
                      className="flex-1 py-2 rounded-lg text-sm font-semibold cursor-pointer disabled:opacity-50"
                      style={{ background: 'var(--accent)', color: '#0b1929' }}>
                {saving ? 'Kaydediliyor…' : (isNew ? 'Oluştur' : 'Güncelle')}
              </button>
              <button onClick={() => { setSelected(null); setIsNew(false) }}
                      className="px-4 py-2 rounded-lg text-sm cursor-pointer"
                      style={{ background: 'var(--surface-hi)', border: '1px solid var(--border)', color: 'var(--text-2)' }}>
                İptal
              </button>
            </div>
          </div>
        ) : (
          <div className="md:col-span-3 flex items-center justify-center rounded-xl"
               style={{ background: 'var(--surface)', border: '1px solid var(--border)', minHeight: 200 }}>
            <p className="text-sm" style={{ color: 'var(--mute)' }}>Düzenlemek için sol listeden seçin</p>
          </div>
        )}
      </div>
    </section>
  )
}

function TemplateRow({ tmpl, active, onEdit, onDelete }: {
  tmpl: PromptTemplate; active: boolean
  onEdit: () => void; onDelete: () => void
}) {
  return (
    <div className="flex items-center group"
         style={{ borderBottom: '1px solid var(--border)', background: active ? 'rgba(138,180,248,0.1)' : 'transparent' }}>
      <button onClick={onEdit}
              className="flex-1 text-left px-3 py-2.5 cursor-pointer transition"
              style={{ color: active ? 'var(--accent-hi)' : 'var(--text)' }}>
        <div className="text-sm truncate">{tmpl.name}</div>
        {tmpl.variables.length > 0 && (
          <div className="flex gap-1 mt-0.5 flex-wrap">
            {tmpl.variables.map(v => (
              <span key={v} className="text-[9px] px-1 rounded font-mono"
                    style={{ background: 'rgba(138,180,248,0.15)', color: 'var(--accent-hi)' }}>
                {`{{${v}}}`}
              </span>
            ))}
          </div>
        )}
      </button>
      <button onClick={onDelete}
              className="opacity-0 group-hover:opacity-100 px-2.5 py-2 cursor-pointer transition shrink-0"
              style={{ color: '#ea4335' }} title="Sil">
        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6M1 7h22M9 7V4a1 1 0 011-1h4a1 1 0 011 1v3"/>
        </svg>
      </button>
    </div>
  )
}
