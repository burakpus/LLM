import { create } from 'zustand'
import { persist } from 'zustand/middleware'
import { v4 as uuidv4 } from 'uuid'

// ── Types ─────────────────────────────────────────────────────────────────────

export type MessageRole = 'user' | 'assistant' | 'system' | 'tool' | 'tool_call'

export interface ToolCallInfo {
  id:        string
  name:      string
  args:      Record<string, unknown>
  result?:   unknown
  status?:   'running' | 'done' | 'error'
  error?:    string
}

export interface Message {
  id:         string
  role:       MessageRole
  content:    string
  thinking?:  string
  streaming:  boolean
  meta?:      string
  kbHits?:    number
  tokens?:    number
  ts:         number
  truncated?: boolean
  isWarning?: boolean
  /** Image attached to user message (data URL) */
  image?:     string
  /** Tool call info, when role === 'tool_call' or message represents one */
  toolCall?:  ToolCallInfo
}

export interface Endpoint {
  name:  string
  model: string  // LiteLLM model alias (chat / code / reason)
  // host & port removed — all traffic goes through /api/llm/completions proxy
}

export interface ConvStats {
  ttft:        number | null
  tokens:      number
  tokensPerSec: number | null
  elapsed:     number
  finishReason: string | null
}

export interface ConvSettings {
  systemPrompt:   string
  temperature:    number
  maxTokens:      number
  stream:         boolean
  agenticEnabled: boolean
  maxAgentLoops:  number
  customTools:    unknown[]
  autoComplete:   boolean
  skillId:         string | null
  skillName:       string | null
  skillCollection: string | null
  model:           string | null
  agentMode:      boolean
  projectId:       string | null  // linked project (null = no project)
}

export const defaultSettings: ConvSettings = {
  systemPrompt:   '',
  temperature:    0.7,
  maxTokens:      1024,
  stream:         true,
  agenticEnabled: false,
  maxAgentLoops:  10,
  customTools:    [],
  autoComplete:   false,
  skillId:         null,
  skillName:       null,
  skillCollection: null,
  model:          'chat',
  agentMode:      false,
  projectId:       null,
}

export interface Conversation {
  id:           string
  title:        string
  skillId?:     string
  skillName?:   string
  messages:     Message[]
  updatedAt:    number
  totalTokens:  number
  settings:     ConvSettings
  generating:   boolean
  /** API-formatted history for re-sending (matches messages but in OpenAI format) */
  apiHistory:   ChatApiMessage[]
  stats:        ConvStats | null
}

export interface ChatApiMessage {
  role:          string
  content:       any
  name?:         string
  tool_calls?:   unknown[]
  tool_call_id?: string
}

export interface AuthState {
  token?:    string
  username?: string
  domain?:   string
}

export type Status =
  | 'not connected'
  | 'connecting'
  | 'connected'
  | 'disconnected'
  | 'unreachable'

export const DEFAULT_ENDPOINTS: Endpoint[] = [
  { name: 'Chating', model: 'chat' },
  { name: 'Coding',  model: 'code' },
]

// ── Store ─────────────────────────────────────────────────────────────────────

interface AppStore {
  // Auth
  auth: AuthState
  setAuth: (a: AuthState) => void
  clearAuth: () => void

  // Theme / Lang
  darkMode: boolean
  lang: 'tr' | 'en'
  toggleTheme: () => void
  toggleLang: () => void

  // Connection / endpoints (model selectors — all proxied via .NET API)
  apiKey: string
  setApiKey: (k: string) => void
  endpoints: Endpoint[]
  setEndpoints: (eps: Endpoint[]) => void
  activeEpIdx:  number | null
  activeModel:  string | null
  setActiveEndpoint: (model: string | null, idx: number | null) => void
  status:   Status
  statusOk: boolean | null
  setStatus: (s: Status, ok: boolean | null) => void

  // Model capabilities
  modelCapabilities: Record<string, { supportsVision: boolean; supportsTools: boolean; contextWindow: number }>
  setModelCapabilities: (caps: Record<string, { supportsVision: boolean; supportsTools: boolean; contextWindow: number }>) => void

  // Conversations
  conversations: Conversation[]
  currentId: string | null

  // Legacy global skill (kept for header display when conv has none yet)
  activeSkillId:   string | null
  activeSkillName: string | null
  setSkill: (id: string | null, name: string | null) => void

  currentConv: () => Conversation | undefined
  getConv:     (id: string) => Conversation | undefined
  newConversation: () => string
  loadConversation: (id: string) => void
  deleteConversation: (id: string) => void
  renameConversation: (id: string, title: string) => void
  clearConversation:  (id: string) => void

  updateConvSettings: (id: string, patch: Partial<ConvSettings>) => void

  addMessage:    (convId: string, msg: Omit<Message, 'id' | 'ts'>) => string
  updateMessage: (convId: string, msgId: string, patch: Partial<Message>) => void
  removeMessage: (convId: string, msgId: string) => void
  appendToken:    (convId: string, msgId: string, token: string) => void
  appendThinking: (convId: string, msgId: string, token: string) => void
  setTruncated:   (convId: string, msgId: string, val: boolean) => void
  setToolCallResult: (convId: string, msgId: string, result: unknown, status?: 'done' | 'error', error?: string) => void

  // Per-conv runtime state
  addApiHistory: (convId: string, msg: ChatApiMessage) => void
  setApiHistory: (convId: string, msgs: ChatApiMessage[]) => void
  setGenerating: (convId: string, val: boolean) => void
  setStats:      (convId: string, stats: ConvStats | null) => void

  // UI
  historyOpen: boolean
  settingsOpen: boolean
  toggleHistory: () => void
  toggleSettings: () => void

  // ── Project mode ──────────────────────────────────────────────────────────
  project: ProjectState
  setProjectId:       (id: string | null) => void
  setActiveFile:      (path: string | null) => void
  setFileContent:     (path: string, content: string) => void
  setPendingChange:   (change: PendingChange | null) => void
  acceptPendingChange: () => void
}

// ── Project types ─────────────────────────────────────────────────────────────

export interface DiffLine {
  type:       'unchanged' | 'added' | 'removed'
  lineNumber: number   // line number in new file (added/unchanged) or old file (removed)
  text:       string
}

export interface PendingChange {
  path:            string
  originalContent: string
  newContent:      string
  diffLines:       DiffLine[]
}

export interface ProjectState {
  projectId:      string | null
  files:          Record<string, string>  // path → content (accepted)
  activeFilePath: string | null
  pendingChange:  PendingChange | null
}

const defaultProject: ProjectState = {
  projectId:      null,
  files:          {},
  activeFilePath: null,
  pendingChange:  null,
}

function ensureSettings(c: Conversation): Conversation {
  if (!c.settings)    c = { ...c, settings:   { ...defaultSettings } }
  if (!c.apiHistory)  c = { ...c, apiHistory: [] }
  if (c.generating == null) c = { ...c, generating: false }
  if (c.stats === undefined) c = { ...c, stats: null }
  if (!c.settings.skillCollection) {
    c = { ...c, settings: { ...c.settings, skillCollection: null } }
  }

  return c
}

export const useStore = create<AppStore>()(
  persist(
    (set, get) => ({
      // ── Auth ────────────────────────────────────────────────────────────
      auth: {},
      setAuth:   (a) => set({ auth: a }),
      clearAuth: () => set({ auth: {} }),

      // ── Theme / Lang ────────────────────────────────────────────────────
      darkMode: true,
      lang: 'tr',
      toggleTheme: () => {
        const next = !get().darkMode
        document.documentElement.setAttribute('data-theme', next ? 'dark' : 'light')
        const el = document.getElementById('hljs-theme') as HTMLLinkElement | null
        if (el) el.href = next
          ? 'https://cdn.jsdelivr.net/npm/@highlightjs/cdn-assets@11/styles/github-dark.min.css'
          : 'https://cdn.jsdelivr.net/npm/@highlightjs/cdn-assets@11/styles/github.min.css'
        set({ darkMode: next })
      },
      toggleLang: () => set(s => ({ lang: s.lang === 'tr' ? 'en' : 'tr' })),

      // ── Connection ──────────────────────────────────────────────────────
      apiKey: '',
      setApiKey: (k) => set({ apiKey: k }),
      endpoints:    DEFAULT_ENDPOINTS,
      setEndpoints: (eps) => set({ endpoints: eps }),
      activeEpIdx:  null,
      activeModel:  null,
      setActiveEndpoint: (model, idx) =>
        set({ activeModel: model, activeEpIdx: idx }),
      status:   'not connected',
      statusOk: null,
      setStatus: (s, ok) => set({ status: s, statusOk: ok }),

      // ── Model capabilities ──────────────────────────────────────────────
      modelCapabilities: {},
      setModelCapabilities: (caps) => set({ modelCapabilities: caps }),

      // ── Conversations ───────────────────────────────────────────────────
      conversations: [],
      currentId:     null,
      activeSkillId:   null,
      activeSkillName: null,

      setSkill: (id, name) => set({ activeSkillId: id, activeSkillName: name }),

      currentConv: () => {
        const { conversations, currentId } = get()
        const c = conversations.find(c => c.id === currentId)
        return c ? ensureSettings(c) : undefined
      },

      getConv: (id) => {
        const c = get().conversations.find(c => c.id === id)
        return c ? ensureSettings(c) : undefined
      },

      newConversation: () => {
        const id: string = uuidv4()
        // Inherit active project into the new conversation
        const currentProjectId = get().project.projectId
        const conv: Conversation = {
          id,
          title:       'New conversation',
          messages:    [],
          updatedAt:   Date.now(),
          totalTokens: 0,
          settings:    { ...defaultSettings, projectId: currentProjectId },
          generating:  false,
          apiHistory:  [],
          stats:       null,
        }
        set(s => ({
          conversations: [conv, ...s.conversations],
          currentId:     id,
        }))
        return id
      },

      loadConversation: (id) => set(s => {
        // Sync project panel with the loaded conversation's projectId
        const conv = s.conversations.find(c => c.id === id)
        const projId = conv?.settings?.projectId ?? null
        return {
          currentId: id,
          project: projId !== s.project.projectId
            ? { ...defaultProject, projectId: projId, files: projId ? s.project.files : {} }
            : s.project,
        }
      }),

      deleteConversation: (id) => set(s => {
        const convs = s.conversations.filter(c => c.id !== id)
        return {
          conversations: convs,
          currentId: s.currentId === id ? (convs[0]?.id ?? null) : s.currentId,
        }
      }),

      renameConversation: (id, title) => set(s => ({
        conversations: s.conversations.map(c =>
          c.id === id ? { ...c, title } : c
        ),
      })),

      clearConversation: (id) => set(s => ({
        conversations: s.conversations.map(c =>
          c.id === id
            ? { ...c, messages: [], apiHistory: [], totalTokens: 0, stats: null, updatedAt: Date.now() }
            : c
        ),
      })),

      updateConvSettings: (id, patch) => set(s => ({
        conversations: s.conversations.map(c =>
          c.id === id
            ? { ...c, settings: { ...ensureSettings(c).settings, ...patch }, updatedAt: Date.now() }
            : c
        ),
      })),

      addMessage: (convId, msg) => {
        const id = uuidv4()
        const full: Message = { ...msg, id, ts: Date.now() }
        set(s => ({
          conversations: s.conversations.map(c =>
            c.id === convId
              ? ensureSettings({
                  ...c,
                  messages:  [...c.messages, full],
                  updatedAt: Date.now(),
                })
              : c
          ),
        }))
        return id
      },

      updateMessage: (convId, msgId, patch) => set(s => ({
        conversations: s.conversations.map(c =>
          c.id === convId
            ? ensureSettings({
                ...c,
                messages: c.messages.map(m => m.id === msgId ? { ...m, ...patch } : m),
                updatedAt: Date.now(),
                totalTokens: c.totalTokens + (patch.tokens ?? 0),
              })
            : c
        ),
      })),

      removeMessage: (convId, msgId) => set(s => ({
        conversations: s.conversations.map(c =>
          c.id === convId
            ? ensureSettings({ ...c, messages: c.messages.filter(m => m.id !== msgId) })
            : c
        ),
      })),

      appendToken: (convId, msgId, tok) => set(s => ({
        conversations: s.conversations.map(c =>
          c.id === convId
            ? ensureSettings({
                ...c,
                messages: c.messages.map(m =>
                  m.id === msgId ? { ...m, content: m.content + tok } : m
                ),
              })
            : c
        ),
      })),

      appendThinking: (convId, msgId, tok) => set(s => ({
        conversations: s.conversations.map(c =>
          c.id === convId
            ? ensureSettings({
                ...c,
                messages: c.messages.map(m =>
                  m.id === msgId ? { ...m, thinking: (m.thinking ?? '') + tok } : m
                ),
              })
            : c
        ),
      })),

      setTruncated: (convId, msgId, val) => set(s => ({
        conversations: s.conversations.map(c =>
          c.id === convId
            ? ensureSettings({
                ...c,
                messages: c.messages.map(m =>
                  m.id === msgId ? { ...m, truncated: val } : m
                ),
              })
            : c
        ),
      })),

      setToolCallResult: (convId, msgId, result, status, error) => set(s => ({
        conversations: s.conversations.map(c =>
          c.id === convId
            ? ensureSettings({
                ...c,
                messages: c.messages.map(m => {
                  if (m.id !== msgId || !m.toolCall) return m
                  return {
                    ...m,
                    toolCall: {
                      ...m.toolCall,
                      result,
                      status: status ?? 'done',
                      error,
                    },
                  }
                }),
              })
            : c
        ),
      })),

      addApiHistory: (convId, msg) => set(s => ({
        conversations: s.conversations.map(c =>
          c.id === convId
            ? ensureSettings({
                ...c,
                apiHistory: [...(c.apiHistory ?? []), msg],
              })
            : c
        ),
      })),

      setApiHistory: (convId, msgs) => set(s => ({
        conversations: s.conversations.map(c =>
          c.id === convId
            ? ensureSettings({ ...c, apiHistory: msgs })
            : c
        ),
      })),

      setGenerating: (convId, val) => set(s => ({
        conversations: s.conversations.map(c =>
          c.id === convId
            ? ensureSettings({ ...c, generating: val })
            : c
        ),
      })),

      setStats: (convId, stats) => set(s => ({
        conversations: s.conversations.map(c =>
          c.id === convId
            ? ensureSettings({ ...c, stats })
            : c
        ),
      })),

      // ── UI ───────────────────────────────────────────────────────────────
      historyOpen: true,
      settingsOpen: false,
      toggleHistory: () => set(s => ({ historyOpen: !s.historyOpen })),
      toggleSettings: () => set(s => ({ settingsOpen: !s.settingsOpen })),

      // ── Project ─────────────────────────────────────────────────────────
      project: defaultProject,
      setProjectId: (id) => set(s => {
        // Also persist projectId in the current conversation's settings
        const conversations = s.currentId
          ? s.conversations.map(c =>
              c.id === s.currentId
                ? ensureSettings({ ...c, settings: { ...c.settings, projectId: id } })
                : c)
          : s.conversations
        return {
          conversations,
          project: { ...s.project, projectId: id, pendingChange: null },
        }
      }),
      setActiveFile: (path) => set(s => ({ project: { ...s.project, activeFilePath: path } })),
      setFileContent: (path, content) => set(s => ({
        project: { ...s.project, files: { ...s.project.files, [path]: content } }
      })),
      setPendingChange: (change) => set(s => ({ project: { ...s.project, pendingChange: change } })),
      acceptPendingChange: () => set(s => {
        const ch = s.project.pendingChange
        if (!ch) return s
        return {
          project: {
            ...s.project,
            files:         { ...s.project.files, [ch.path]: ch.newContent },
            activeFilePath: ch.path,
            pendingChange:  null,
          }
        }
      }),
    }),
    {
      name: 'setllm-store',
      version: 3,
      migrate: (persisted: any, version: number) => {
        if (!persisted) return persisted
        // v1/v2→v3: strip host/port from endpoints (now model-only),
        //           remove baseUrl/endpointIdx from conv settings.
        if (version < 3) {
          if (Array.isArray(persisted.endpoints))
            persisted.endpoints = (persisted.endpoints as any[])
              .filter(ep => ep.model)
              .map(({ name, model }: any) => ({ name, model }))
          delete persisted.activeBaseUrl
          if (Array.isArray(persisted.conversations))
            persisted.conversations = (persisted.conversations as any[]).map((c: any) => {
              if (c.settings) {
                delete c.settings.baseUrl
                delete c.settings.endpointIdx
              }
              return c
            })
        }
        return persisted
      },
      partialize: (s) => ({
        conversations: s.conversations,
        currentId:     s.currentId,
        darkMode:      s.darkMode,
        lang:          s.lang,
        apiKey:        s.apiKey,
        endpoints:     s.endpoints,
        activeEpIdx:   s.activeEpIdx,
        activeModel:   s.activeModel,
      }),
    }
  )
)

// ── i18n ────────────────────────────────────────────────────────────────────

const T = {
  tr: {
    newChat: 'Yeni Sohbet', send: 'Gönder', stop: 'Durdur',
    search: 'Sohbetleri ara...', noConvs: 'Henüz sohbet yok',
    noMatch: 'Eşleşme bulunamadı', configuration: 'Yapılandırma',
    thinking: 'Düşünüyor', copy: 'Kopyala', txt: 'TXT', code: 'Kod',
    regen: 'Yeniden üret', light: 'Aydınlık', dark: 'Karanlık',
    lang: 'EN', logout: 'Çıkış', skill: 'Mod',
    connect: 'Bağlan', disconnect: 'Bağlantıyı Kes', connecting: 'Bağlanıyor...',
    connected: 'Bağlandı', notConnected: 'Bağlı değil', unreachable: 'Erişilemez',
    customUrl: 'Özel URL', modelName: 'Model Adı', apiKey: 'API Anahtarı',
    parameters: 'Parametreler', maxTokens: 'Maks. Token', temperature: 'Sıcaklık',
    stream: 'Akış', systemPrompt: 'Sistem Komutu', clearChat: 'Sohbeti Temizle',
    agentic: 'Otonom', maxLoops: 'Maks. Döngü', customTools: 'Özel Araçlar',
    autoComplete: 'Otomatik Devam', agentMode: 'RAG Modu',
    truncated: 'Yanıt kesildi — Devam et', continue: 'Devam',
    placeholder: 'Mesaj yazın... (Enter ile gönder, Shift+Enter ile yeni satır)',
    attach: 'Dosya ekle', online: 'çevrimiçi', offline: 'çevrimdışı',
    checking: 'kontrol ediliyor', send_: 'Gönder', settings: 'Ayarlar',
    ttft: 'İlk Token', tokens: 'token', tokPerSec: 'tok/s', elapsed: 'süre',
    history: 'Geçmiş', regenerate: 'Yeniden Üret',
    conversations: 'sohbet', selectStart: 'Bir mod seçin ve sohbete başlayın',
    arguments: 'Argümanlar', result: 'Sonuç', tool_call: 'Araç Çağrısı',
  },
  en: {
    newChat: 'New Chat', send: 'Send', stop: 'Stop',
    search: 'Search conversations...', noConvs: 'No conversations yet',
    noMatch: 'No matches', configuration: 'Configuration',
    thinking: 'Thinking', copy: 'Copy', txt: 'TXT', code: 'Code',
    regen: 'Regenerate', light: 'Light', dark: 'Dark',
    lang: 'TR', logout: 'Logout', skill: 'Mode',
    connect: 'Connect', disconnect: 'Disconnect', connecting: 'Connecting...',
    connected: 'Connected', notConnected: 'Not Connected', unreachable: 'Unreachable',
    customUrl: 'Custom URL', modelName: 'Model Name', apiKey: 'API Key',
    parameters: 'Parameters', maxTokens: 'Max Tokens', temperature: 'Temperature',
    stream: 'Stream', systemPrompt: 'System Prompt', clearChat: 'Clear Chat',
    agentic: 'Autonomous', maxLoops: 'Max Loops', customTools: 'Custom Tools',
    autoComplete: 'Auto-Complete', agentMode: 'RAG Mode',
    truncated: 'Response truncated — Continue', continue: 'Continue',
    placeholder: 'Type a message... (Enter to send, Shift+Enter for newline)',
    attach: 'Attach file', online: 'online', offline: 'offline',
    checking: 'checking', send_: 'Send', settings: 'Settings',
    ttft: 'TTFT', tokens: 'tokens', tokPerSec: 'tok/s', elapsed: 'elapsed',
    history: 'History', regenerate: 'Regenerate',
    conversations: 'conversations', selectStart: 'Select a skill and start chatting',
    arguments: 'Arguments', result: 'Result', tool_call: 'Tool Call',
  },
} as const

export type I18nKey = keyof typeof T.tr

export function t(key: I18nKey): string {
  const lang = useStore.getState().lang
  return T[lang][key] ?? key
}
