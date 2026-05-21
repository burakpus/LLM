import { useEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import rehypeHighlight from 'rehype-highlight'
import type { Message } from '../../store'
import { t } from '../../store'
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
function CodeBlock({
  className, children,
}: { className?: string; children?: ReactNode }) {
  const [copied, setCopied] = useState(false)
  const lang = (className?.match(/language-(\w+)/)?.[1] ?? 'plain').toLowerCase()
  const text = String(children ?? '').replace(/\n$/, '')

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

  return (
    <div className="code-block-wrapper">
      <div className="code-lang-bar">
        <span>{lang}</span>
        <div className="code-lang-bar-actions">
          <button className="copy-btn" onClick={copy}>{copied ? 'Copied' : t('copy')}</button>
          <button className="dl-btn"   onClick={download}>{t('txt')}</button>
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

  const copy = (text: string) => navigator.clipboard.writeText(text)

  const downloadTxt = (msg: Message) => {
    const blob = new Blob([msg.content], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url; a.download = `response-${msg.id.slice(0, 8)}.txt`; a.click()
    URL.revokeObjectURL(url)
  }

  const downloadCode = (msg: Message) => {
    const code = extractCode(msg.content)
    if (!code) return
    const blob = new Blob([code], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url; a.download = `code-${msg.id.slice(0, 8)}.txt`; a.click()
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
        {messages.map((m, idx) => {
          if (m.role === 'tool_call' && m.toolCall) {
            return (
              <div key={m.id} className="w-full pl-10">
                <ToolCallBlock toolCall={m.toolCall} />
              </div>
            )
          }

          if (m.role === 'user') {
            return (
              <div key={m.id} className="flex justify-end w-full">
                <div
                  className="px-[18px] py-3 text-sm leading-relaxed whitespace-pre-wrap break-words"
                  style={{
                    background: 'var(--user-bubble)',
                    color: 'var(--text)',
                    borderRadius: '18px 18px 4px 18px',
                    maxWidth: '70%',
                  }}
                >
                  {m.image && (
                    <img src={m.image} alt="attached" className="rounded-lg mb-2 max-h-60" />
                  )}
                  {m.content}
                </div>
              </div>
            )
          }

          // assistant
          const isLastAssistant = idx === lastAssistantIdx
          return (
            <div key={m.id} className="group flex gap-3 w-full">
              <AssistantAvatar />
              <div className="flex-1 min-w-0 flex flex-col gap-1 pt-0.5">
                {/* Thinking */}
                {m.thinking && (
                  <ThinkingBlock text={m.thinking} streaming={m.streaming} />
                )}

                {/* Content */}
                <div
                  className={`text-[15px] leading-relaxed break-words ${m.streaming ? '' : 'prose'} ${m.streaming && !m.content ? 'cursor-blink' : ''}`}
                  style={{ color: 'var(--text)' }}
                >
                  {m.streaming ? (
                    <div className="whitespace-pre-wrap">
                      {m.content}
                      {m.content && <span className="cursor-blink" />}
                    </div>
                  ) : m.content ? (
                    <ReactMarkdown
                      remarkPlugins={[remarkGfm]}
                      rehypePlugins={[rehypeHighlight]}
                      components={{
                        code({ className, children, ...rest }: any) {
                          const inline = !className
                          if (inline) {
                            return <code className={className} {...rest}>{children}</code>
                          }
                          return <CodeBlock className={className}>{children}</CodeBlock>
                        },
                        pre({ children }: any) {
                          return <>{children}</>
                        },
                      }}
                    >
                      {m.content}
                    </ReactMarkdown>
                  ) : null}
                </div>

                {/* Truncated */}
                {m.truncated && !m.streaming && onContinue && (
                  <button
                    onClick={() => onContinue(idx)}
                    className="mt-1 self-start rounded-full px-3 py-1.5 text-xs font-medium cursor-pointer"
                    style={{
                      border: '1px solid #f59e0b',
                      color:  '#f59e0b',
                      background: 'rgba(245, 158, 11, 0.1)',
                    }}
                  >
                    {t('truncated')}
                  </button>
                )}

                {/* Action bar — only on hover */}
                {m.content && !m.streaming && (
                  <div className="mt-2 flex items-center gap-0.5 text-[11px] opacity-0 group-hover:opacity-100 transition-opacity"
                       style={{ color: 'var(--mute)' }}>
                    {m.kbHits != null && m.kbHits > 0 && (
                      <span className="mr-2 opacity-100" style={{ color: 'var(--accent)' }}>
                        {m.kbHits} refs
                      </span>
                    )}
                    <button
                      onClick={() => copy(m.content)}
                      className="flex items-center gap-1 px-2 py-1 rounded-full transition cursor-pointer"
                      title={t('copy')}
                      onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
                      onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}
                    >
                      <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24"
                           stroke="currentColor" strokeWidth={2}>
                        <rect x="9" y="9" width="13" height="13" rx="2" />
                        <path d="M5 15H4a2 2 0 01-2-2V4a2 2 0 012-2h9a2 2 0 012 2v1" />
                      </svg>
                    </button>
                    <button
                      onClick={() => downloadTxt(m)}
                      className="flex items-center gap-1 px-2 py-1 rounded-full transition cursor-pointer"
                      title={t('txt')}
                      onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
                      onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}
                    >
                      <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24"
                           stroke="currentColor" strokeWidth={2}>
                        <path strokeLinecap="round" strokeLinejoin="round"
                          d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                      </svg>
                    </button>
                    {hasCode(m.content) && (
                      <button
                        onClick={() => downloadCode(m)}
                        className="flex items-center gap-1 px-2 py-1 rounded-full transition cursor-pointer"
                        title={t('code')}
                        onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
                        onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}
                      >
                        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24"
                             stroke="currentColor" strokeWidth={2}>
                          <path strokeLinecap="round" strokeLinejoin="round"
                            d="M10 20l4-16m4 4l4 4-4 4M6 16l-4-4 4-4" />
                        </svg>
                      </button>
                    )}
                    {isLastAssistant && onRegenerate && (
                      <button
                        onClick={onRegenerate}
                        disabled={generating}
                        className="flex items-center gap-1 px-2 py-1 rounded-full transition cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed"
                        title={t('regen')}
                        onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
                        onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}
                      >
                        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24"
                             stroke="currentColor" strokeWidth={2}>
                          <path strokeLinecap="round" strokeLinejoin="round"
                            d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                        </svg>
                      </button>
                    )}
                    {m.tokens != null && m.tokens > 0 && <span className="ml-2 text-[10px]">{m.tokens} tok</span>}
                    {m.meta && <span className="ml-1 text-[10px]">{m.meta}</span>}
                  </div>
                )}
              </div>
            </div>
          )
        })}
        <div ref={bottomRef} />
      </div>
    </div>
  )
}
