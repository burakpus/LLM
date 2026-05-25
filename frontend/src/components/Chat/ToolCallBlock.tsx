import { useState } from 'react'
import { t } from '../../store'
import type { ToolCallInfo } from '../../store'

interface Props {
  toolCall: ToolCallInfo
}

// Extract download chip info from a tool result, if applicable.
// Used for the generate_file tool which returns { ok, downloadUrl, filename, sizeBytes }.
function fileFromResult(result: unknown): { url: string; filename: string; sizeBytes?: number } | null {
  if (!result || typeof result !== 'object') return null
  const r = result as Record<string, unknown>
  if (r.ok !== true) return null
  const url = typeof r.downloadUrl === 'string' ? r.downloadUrl : null
  const fn  = typeof r.filename    === 'string' ? r.filename    : null
  if (!url || !fn) return null
  return { url, filename: fn, sizeBytes: typeof r.sizeBytes === 'number' ? r.sizeBytes : undefined }
}

function formatBytes(n?: number): string {
  if (n == null) return ''
  if (n < 1024)        return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / (1024 * 1024)).toFixed(1)} MB`
}

function ext2icon(filename: string): string {
  const e = filename.split('.').pop()?.toLowerCase()
  return e === 'docx' ? '📄' : e === 'xlsx' ? '📊' : e === 'pptx' ? '📽️' : e === 'pdf' ? '📕' : '📎'
}

export default function ToolCallBlock({ toolCall }: Props) {
  const [open, setOpen] = useState(false)
  const status = toolCall.status ?? 'running'
  const statusClass =
    status === 'running' ? 'running' :
    status === 'error'   ? 'error'   : 'done'

  const fileChip = fileFromResult(toolCall.result)
  const tok = typeof window !== 'undefined' ? (localStorage.getItem('setllm-token') ?? '') : ''
  const fileUrl = fileChip ? `${fileChip.url}${fileChip.url.includes('?') ? '&' : '?'}token=${encodeURIComponent(tok)}` : ''

  return (
    <div className="toolcall-block">
      <div className="toolcall-header" onClick={() => setOpen(o => !o)}>
        <span className={`toolcall-status ${statusClass}`} />
        <svg
          className={`w-3 h-3 transition-transform ${open ? 'rotate-90' : ''}`}
          fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}
          style={{ color: 'var(--mute)' }}
        >
          <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
        </svg>
        <span className="toolcall-name">{toolCall.name}</span>
        <span style={{ color: 'var(--mute)', fontSize: 11 }}>
          {status === 'running' ? '...' : status === 'error' ? 'failed' : 'done'}
        </span>
        {fileChip && (
          <a href={fileUrl} download={fileChip.filename} onClick={e => e.stopPropagation()}
             className="ml-auto inline-flex items-center gap-1.5 px-2 py-1 text-xs rounded-md cursor-pointer"
             style={{ background: 'rgba(138,180,248,0.15)', border: '1px solid rgba(138,180,248,0.35)', color: 'var(--accent-hi)' }}
             title={`İndir: ${fileChip.filename}`}>
            <span>{ext2icon(fileChip.filename)}</span>
            <span style={{ fontWeight: 500 }}>{fileChip.filename}</span>
            {fileChip.sizeBytes != null && (
              <span style={{ color: 'var(--mute)', fontSize: 10 }}>· {formatBytes(fileChip.sizeBytes)}</span>
            )}
          </a>
        )}
      </div>
      {open && (
        <div className="toolcall-body">
          <div className="toolcall-section">
            <div className="toolcall-section-label">{t('arguments')}</div>
            <pre>{JSON.stringify(toolCall.args, null, 2)}</pre>
          </div>
          {toolCall.result !== undefined && (
            <div className="toolcall-section">
              <div className="toolcall-section-label">{t('result')}</div>
              <pre>
                {typeof toolCall.result === 'string'
                  ? toolCall.result
                  : JSON.stringify(toolCall.result, null, 2)}
              </pre>
            </div>
          )}
          {toolCall.error && (
            <div className="toolcall-section">
              <div className="toolcall-section-label" style={{ color: '#ef4444' }}>Error</div>
              <pre>{toolCall.error}</pre>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
