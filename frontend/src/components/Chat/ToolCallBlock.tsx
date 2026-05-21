import { useState } from 'react'
import { t } from '../../store'
import type { ToolCallInfo } from '../../store'

interface Props {
  toolCall: ToolCallInfo
}

export default function ToolCallBlock({ toolCall }: Props) {
  const [open, setOpen] = useState(false)
  const status = toolCall.status ?? 'running'
  const statusClass =
    status === 'running' ? 'running' :
    status === 'error'   ? 'error'   : 'done'

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
