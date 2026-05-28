import { useRef, useState } from 'react'
import { uploadFiles } from '../../../api/admin'
import type { UploadResult } from '../../../api/admin'
import { formatBytes } from './_shared'

export default function UploadTab() {
  const [collection, setCollection] = useState('default')
  const [files, setFiles]           = useState<File[]>([])
  const [results, setResults]       = useState<UploadResult[]>([])
  const [busy, setBusy]             = useState(false)
  const [dragOver, setDragOver]     = useState(false)
  const inputRef = useRef<HTMLInputElement>(null)

  const onPick = (list: FileList | null) => {
    if (!list) return
    setFiles(Array.from(list))
    setResults([])
  }

  const onDrop = (e: React.DragEvent) => {
    e.preventDefault()
    setDragOver(false)
    onPick(e.dataTransfer.files)
  }

  const onUpload = async () => {
    if (!files.length) return
    setBusy(true)
    setResults([])
    try {
      const r = await uploadFiles(files, collection.trim() || 'default')
      setResults(r)
      setFiles([])
      if (inputRef.current) inputRef.current.value = ''
    } catch (err: any) {
      setResults([{ file: '(batch)', ok: false, error: err.message ?? String(err) }])
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="space-y-5">
      <div>
        <h2 className="text-lg font-medium">Upload documents</h2>
        <p className="text-xs mt-1" style={{ color: 'var(--mute)' }}>
          Supported file types: .txt, .md, .pdf, .docx, .xlsx, .jpg, .png, .tiff
        </p>
      </div>

      <div className="rounded-xl p-5 space-y-4"
           style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>

        <label className="block">
          <div className="text-xs mb-1.5" style={{ color: 'var(--mute)' }}>Collection</div>
          <input
            value={collection}
            onChange={e => setCollection(e.target.value)}
            className="w-full px-3 py-2 rounded-md text-sm outline-none"
            style={{
              background: 'var(--input-bg)',
              border:     '1px solid var(--border)',
              color:      'var(--text)',
            }}
            placeholder="default"
          />
        </label>

        <div
          onDragOver={e => { e.preventDefault(); setDragOver(true) }}
          onDragLeave={() => setDragOver(false)}
          onDrop={onDrop}
          onClick={() => inputRef.current?.click()}
          className="rounded-lg p-8 text-center cursor-pointer transition"
          style={{
            background: dragOver ? 'var(--surface-hi)' : 'var(--surface-2)',
            border:     `2px dashed ${dragOver ? 'var(--accent)' : 'var(--border)'}`,
          }}
        >
          <svg className="w-8 h-8 mx-auto mb-2" fill="none" viewBox="0 0 24 24"
               stroke="currentColor" strokeWidth={1.5} style={{ color: 'var(--accent)' }}>
            <path strokeLinecap="round" strokeLinejoin="round"
                  d="M7 16a4 4 0 01-.88-7.9 5 5 0 019.9-1A4.5 4.5 0 0117 16M12 12v9m0-9l-3 3m3-3l3 3" />
          </svg>
          <div className="text-sm" style={{ color: 'var(--text)' }}>
            Drag & drop files here, or click to browse
          </div>
          <div className="text-xs mt-1" style={{ color: 'var(--mute)' }}>
            .txt, .md, .pdf, .docx, .xlsx, .jpg, .png, .tiff
          </div>
          <input
            ref={inputRef}
            type="file"
            multiple
            accept=".txt,.md,.pdf,.docx,.xlsx,.jpg,.jpeg,.png,.tiff,.tif,.bmp,.webp"
            onChange={e => onPick(e.target.files)}
            className="hidden"
          />
        </div>

        {files.length > 0 && (
          <div className="rounded-md p-3 text-xs"
               style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
            <div className="font-medium mb-2" style={{ color: 'var(--text)' }}>
              {files.length} file{files.length === 1 ? '' : 's'} selected
            </div>
            <ul className="space-y-1" style={{ color: 'var(--mute)' }}>
              {files.map(f => (
                <li key={f.name} className="flex justify-between">
                  <span className="truncate">{f.name}</span>
                  <span className="ml-2 shrink-0">{formatBytes(f.size)}</span>
                </li>
              ))}
            </ul>
          </div>
        )}

        <div className="flex justify-end">
          <button
            disabled={busy || !files.length}
            onClick={onUpload}
            className="px-4 py-2 rounded-md text-sm font-medium cursor-pointer transition disabled:opacity-50 disabled:cursor-not-allowed"
            style={{
              background: 'var(--accent)',
              color:      '#0a0a0a',
            }}
          >
            {busy ? 'Uploading…' : 'Upload & ingest'}
          </button>
        </div>
      </div>

      {results.length > 0 && (
        <div className="rounded-xl overflow-hidden"
             style={{ background: 'var(--surface)', border: '1px solid var(--border)' }}>
          <table className="w-full text-sm">
            <thead style={{ background: 'var(--surface-hi)', color: 'var(--mute)' }}>
              <tr>
                <th className="text-left px-4 py-2 font-medium">File</th>
                <th className="text-left px-4 py-2 font-medium">Status</th>
                <th className="text-right px-4 py-2 font-medium">Chunks</th>
                <th className="text-left px-4 py-2 font-medium">Notes</th>
              </tr>
            </thead>
            <tbody>
              {results.map((r, i) => (
                <tr key={i} style={{ borderTop: '1px solid var(--border)' }}>
                  <td className="px-4 py-2 truncate">{r.file}</td>
                  <td className="px-4 py-2">
                    <span style={{ color: r.ok ? '#34a853' : '#ea4335' }}>
                      {r.ok ? 'OK' : 'Failed'}
                    </span>
                  </td>
                  <td className="px-4 py-2 text-right" style={{ color: 'var(--mute)' }}>
                    {r.chunks ?? '—'}
                  </td>
                  <td className="px-4 py-2 text-xs" style={{ color: 'var(--mute)' }}>
                    {r.error ?? (r.tokens ? `${r.tokens} tokens` : '')}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
