import { useState, useEffect } from 'react'
import { t } from '../../store'

interface Props {
  text:      string
  streaming: boolean
}

export default function ThinkingBlock({ text, streaming }: Props) {
  const [open, setOpen] = useState(true)

  // collapse automatically a moment after streaming ends
  useEffect(() => {
    if (!streaming) {
      const tm = setTimeout(() => setOpen(false), 800)
      return () => clearTimeout(tm)
    }
  }, [streaming])

  return (
    <div className="thinking-block">
      <div className="thinking-block-header" onClick={() => setOpen(o => !o)}>
        <svg
          className={`w-3 h-3 transition-transform ${open ? 'rotate-90' : ''}`}
          fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}
        >
          <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
        </svg>
        <span>{t('thinking')}</span>
        {streaming && <span className="cursor-blink" style={{ marginLeft: 2 }} />}
      </div>
      {open && (
        <div className="thinking-block-body scrollbar-thin">
          {text || ' '}
        </div>
      )}
    </div>
  )
}
