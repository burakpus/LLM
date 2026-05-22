import { useEffect, useRef, useState, memo, useCallback } from 'react'
import type { ReactNode } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import rehypeHighlight from 'rehype-highlight'
import { useShallow } from 'zustand/react/shallow'
import type { Message } from '../../store'
import { t, useStore } from '../../store'
import { writeFile } from '../../api/project'
import { computeDiff } from './DiffView'
import ThinkingBlock from './ThinkingBlock'
import ToolCallBlock from './ToolCallBlock'
import SetLogo from '../SetLogo'

interface Props {
  messages:        Message[]
  generating:      boolean
  onRegenerate?:   () => void
  onContinue?:     (msgIdx: number) => void
}

// ── Code block renderer ─────────────────────────────────────────────────────
/** Extract plain text from React node tree (handles syntax-highlighted nodes) */
function nodeToText(node: ReactNode): string {
  if (typeof node === 'string') return node
  if (typeof node === 'number') return String(node)
  if (Array.isArray(node)) return node.map(nodeToText).join('')
  if (node && typeof node === 'object' && 'props' in (node as any))
    return nodeToText((node as any).props?.children)
  return ''
}

function CodeBlock({
  className, children,
}: { className?: string; children?: ReactNode }) {
  const { project, setFileContent, setPendingChange, setActiveFile } =
    useStore(useShallow(s => ({
      project:         s.project,
      setFileContent:  s.setFileContent,
      setPendingChange: s.setPendingChange,
      setActiveFile:   s.setActiveFile,
    })))
  const [copied, setCopied]   = useState(false)
  const [saving, setSaving]   = useState(false)
  const [saved,  setSaved]    = useState(false)
  const [fname,  setFname]    = useState('')
  const [showInput, setShowInput] = useState(false)
  const lang = (className?.match(/language-(\w+)/)?.[1] ?? 'plain').toLowerCase()
  // nodeToText handles syntax-highlighted nodes (arrays of React elements)
  const text = nodeToText(children).replace(/\n$/, '')

  const hasProject = !!project.projectId

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(true)
      setTimeout(() => setCopied(false), 1200)
    } catch { /* ignore */ }
  }

  const download = () => {
    const ext = lang === 'plain' ? 'txt' : lang
    const blob = new Blob([text], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `snippet-${Date.now()}.${ext}`
    a.click()
    URL.revokeObjectURL(url)
  }

  const saveToProject = async () => {
    const path = fname.trim() || `snippet-${Date.now()}.${lang === 'plain' ? 'txt' : lang}`
    setSaving(true)
    try {
      const projectId = project.projectId!
      await writeFile(projectId, path, text)
      const original = project.files[path] ?? ''
      const diffLines = computeDiff(original, text)
      setFileContent(path, text)
      setPendingChange({ path, originalContent: original, newContent: text, diffLines })
      setActiveFile(path)
      setSaved(true)
      setShowInput(false)
      setTimeout(() => setSaved(false), 2000)
    } catch { /* ignore */ } finally {
      setSaving(false)
    }
  }

  return (
    <div className="code-block-wrapper">
      <div className="code-lang-bar">
        <span>{lang}</span>
        <div className="code-lang-bar-actions">
          <button className="copy-btn" onClick={copy}>{copied ? 'Copied' : t('copy')}</button>
          <button className="dl-btn"   onClick={download}>{t('txt')}</button>
          {hasProject && !showInput && (
            <button
              className="dl-btn"
              onClick={() => setShowInput(true)}
              title="Projeye kaydet"
              style={{ color: saved ? '#34a853' : undefined }}
            >
              {saved ? '✓ Kaydedildi' : '📁'}
            </button>
          )}
          {hasProject && showInput && (
            <span className="flex items-center gap-1">
              <input
                autoFocus
                value={fname}
                onChange={e => setFname(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') saveToProject(); if (e.key === 'Escape') setShowInput(false) }}
                placeholder={`dosya.${lang === 'plain' ? 'txt' : lang}`}
                style={{
                  fontSize: 11, padding: '1px 6px', borderRadius: 4, outline: 'none',
                  background: 'var(--bg)', border: '1px solid var(--accent)',
                  color: 'var(--text)', width: 130,
                }}
              />
              <button className="copy-btn" onClick={saveToProject} disabled={saving}>
                {saving ? '...' : '✓'}
              </button>
              <button className="dl-btn" onClick={() => setShowInput(false)}>✕</button>
            </span>
          )}
        </div>
      </div>
      <pre><code className={className}>{children}</code></pre>
    </div>
  )
}

function hasCode(text: string): boolean {
  return /```/.test(text)
}

function extractCode(text: string): string {
  const out: string[] = []
  const re = /```(\w+)?\n([\s\S]*?)```/g
  let m: RegExpExecArray | null
  while ((m = re.exec(text)) !== null) {
    out.push(m[2])
  }
  return out.join('\n\n')
}

// Small Gemini-style avatar
function AssistantAvatar() {
  return (
    <div className="gemini-avatar">
      <SetLogo className="w-5 h-5" />
    </div>
  )
}

// ── Memoized message item — only re-renders when this specific message changes ─
interface ItemProps {
  m:               Message
  idx:             number
  isLastAssistant: boolean
  onContinue?:     (i: number) => void
  copy:            (t: string) => void
  downloadTxt:     (m: Message) => void
  downloadCode:    (m: Message) => void
}

const MessageItem = memo(function MessageItem({
  m, idx, isLastAssistant, onContinue, copy, downloadTxt, downloadCode,
}: ItemProps) {
  if (m.role === 'tool_call' && m.toolCall) {
    return (
      <div className="w-full pl-10">
        <ToolCallBlock toolCall={m.toolCall} />
      </div>
    )
  }

  if (m.role === 'user') {
    return (
      <div className="flex justify-end w-full">
        <div className="px-[18px] py-3 text-sm leading-relaxed whitespace-pre-wrap break-words"
             style={{ background: 'var(--user-bubble)', color: 'var(--text)',
                      borderRadius: '18px 18px 4px 18px', maxWidth: '70%' }}>
          {m.image && <img src={m.image} alt="attached" className="rounded-lg mb-2 max-h-60" />}
          {m.content}
        </div>
      </div>
    )
  }

  if (m.isWarning) {
    return (
      <div className="flex gap-2 items-start px-1 py-2 rounded-xl text-sm"
           style={{ background: 'rgba(251,191,36,0.1)', border: '1px solid rgba(251,191,36,0.3)', color: '#fbbf24' }}>
        <span className="shrink-0 text-base">⏳</span>
        <span>{m.content.replace(/^⏳\s*/, '')}</span>
      </div>
    )
  }

  // assistant
  return (
    <AssistantMessage
      m={m} idx={idx} isLastAssistant={isLastAssistant}
      onContinue={onContinue} copy={copy} downloadTxt={downloadTxt} downloadCode={downloadCode}
    />
  )
})

// Separate component so ReactMarkdown re-renders don't bubble up
const AssistantMessage = memo(function AssistantMessage({
  m, idx, isLastAssistant, onContinue, copy, downloadTxt, downloadCode,
}: ItemProps) {
  return (
    <div className="group flex gap-3 w-full">
      <AssistantAvatar />
      <div className="flex-1 min-w-0 flex flex-col gap-1 pt-0.5">
        {m.thinking && <ThinkingBlock text={m.thinking} streaming={m.streaming} />}
        <div className={`text-[15px] leading-relaxed break-words ${m.streaming ? '' : 'prose'} ${m.streaming && !m.content ? 'cursor-blink' : ''}`}
             style={{ color: 'var(--text)' }}>
          {m.streaming ? (
            <div className="whitespace-pre-wrap">
              {m.content}
              {m.content && <span className="cursor-blink" />}
            </div>
          ) : m.content ? (
            <ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeHighlight]}
              components={{
                code({ className, children, ...rest }: any) {
                  return !className
                    ? <code className={className} {...rest}>{children}</code>
                    : <CodeBlock className={className}>{children}</CodeBlock>
                },
                pre({ children }: any) { return <>{children}</> },
              }}
            >{m.content}</ReactMarkdown>
          ) : null}
        </div>
        {m.truncated && !m.streaming && onContinue && (
          <button onClick={() => onContinue(idx)}
                  className="mt-1 self-start rounded-full px-3 py-1.5 text-xs font-medium cursor-pointer"
                  style={{ border: '1px solid #f59e0b', color: '#f59e0b', background: 'rgba(245,158,11,0.1)' }}>
            {t('truncated')}
          </button>
        )}
        {m.content && !m.streaming && (
          <MessageActions m={m} isLast={isLastAssistant}
            copy={copy} downloadTxt={downloadTxt} downloadCode={downloadCode} />
        )}
      </div>
    </div>
  )
})

// Action bar extracted to prevent hover state from affecting parent
const MessageActions = memo(function MessageActions({ m, copy, downloadTxt, downloadCode }: {
  m: Message; isLast: boolean
  copy: (t: string) => void; downloadTxt: (m: Message) => void; downloadCode: (m: Message) => void
}) {
  return (
    <div className="mt-2 flex items-center gap-0.5 text-[11px] opacity-0 group-hover:opacity-100 transition-opacity"
         style={{ color: 'var(--mute)' }}>
      {m.kbHits != null && m.kbHits > 0 && (
        <span className="mr-2 opacity-100" style={{ color: 'var(--accent)' }}>{m.kbHits} refs</span>
      )}
      <button onClick={() => copy(m.content)}
              className="flex items-center gap-1 px-2 py-1 rounded-full transition cursor-pointer" title={t('copy')}
              onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
              onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}>
        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <rect x="9" y="9" width="13" height="13" rx="2" />
          <path d="M5 15H4a2 2 0 01-2-2V4a2 2 0 012-2h9a2 2 0 012 2v1" />
        </svg>
      </button>
      <button onClick={() => downloadTxt(m)}
              className="flex items-center gap-1 px-2 py-1 rounded-full transition cursor-pointer" title={t('txt')}
              onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
              onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}>
        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
        </svg>
      </button>
      {hasCode(m.content) && (
        <button onClick={() => downloadCode(m)}
                className="flex items-center gap-1 px-2 py-1 rounded-full transition cursor-pointer" title={t('code')}
                onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
                onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}>
          <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M10 20l4-16m4 4l4 4-4 4M6 16l-4-4 4-4" />
          </svg>
        </button>
      )}
    </div>
  )
})

export default function MessageList({
  messages, generating, onRegenerate, onContinue,
}: Props) {
  const bottomRef = useRef<HTMLDivElement>(null)
  const scrollRef = useRef<HTMLDivElement>(null)

  // Scroll to bottom on every token while generating
  useEffect(() => {
    const el = scrollRef.current
    if (!el) return
    if (generating) {
      el.scrollTop = el.scrollHeight
    }
  })

  // Smooth scroll when a new message is added
  const prevLenRef = useRef(0)
  useEffect(() => {
    if (messages.length > prevLenRef.current) {
      const el = scrollRef.current
      if (el) el.scrollTop = el.scrollHeight
    }
    prevLenRef.current = messages.length
  }, [messages.length])

  const copy        = useCallback((text: string) => navigator.clipboard.writeText(text), [])
  const downloadTxt  = useCallback((msg: Message) => {
    const blob = new Blob([msg.content], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url; a.download = `response-${msg.id.slice(0, 8)}.txt`; a.click()
    URL.revokeObjectURL(url)
  }, [])
  const downloadCode = useCallback((msg: Message) => {
    const code = extractCode(msg.content)
    if (!code) return
    const blob = new Blob([code], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url; a.download = `code-${msg.id.slice(0, 8)}.txt`; a.click()
    URL.revokeObjectURL(url)
  }, [])

  const downloadTxt = (msg: Message) => {
    const blob = new Blob([msg.content], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url; a.download = `response-${msg.id.slice(0, 8)}.txt`; a.click()
    URL.revokeObjectURL(url)
  }

  if (messages.length === 0) {
    return (
      <div className="flex-1 flex flex-col items-center justify-center px-6">
        <div className="mb-6">
          <SetLogo className="h-16 w-auto" />
        </div>
        <h1 className="text-4xl md:text-5xl font-medium tracking-tight text-center"
            style={{
              background: 'linear-gradient(90deg, #4285f4 0%, #9b72cb 50%, #d96570 100%)',
              WebkitBackgroundClip: 'text',
              WebkitTextFillColor: 'transparent',
              backgroundClip: 'text',
            }}>
          {t('selectStart') || 'How can I help you today?'}
        </h1>
      </div>
    )
  }

  let lastAssistantIdx = -1
  for (let i = messages.length - 1; i >= 0; i--) {
    if (messages[i].role === 'assistant') { lastAssistantIdx = i; break }
  }

  return (
    <div ref={scrollRef} className="flex-1 overflow-y-auto scrollbar-thin">
      <div className="px-5 py-8 flex flex-col gap-6 min-h-full"
           style={{ maxWidth: '760px', margin: '0 auto', width: '100%' }}>
        {messages.map((m, idx) => (
          <MessageItem
            key={m.id}
            m={m}
            idx={idx}
            isLastAssistant={idx === lastAssistantIdx}
            onContinue={onContinue}
            copy={copy}
            downloadTxt={downloadTxt}
            downloadCode={downloadCode}
          />
        ))}
        <div ref={bottomRef} />
      </div>
    </div>
  )
}
