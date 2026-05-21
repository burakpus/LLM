// =============================================================================
// admin.ts — API calls for the admin panel
// =============================================================================

function token(): string {
  return localStorage.getItem('setllm-token') ?? ''
}

function authHeaders(extra?: Record<string, string>) {
  return {
    Authorization: `Bearer ${token()}`,
    ...extra,
  }
}

// ── Types ────────────────────────────────────────────────────────────────────

export interface UploadResult {
  file:    string
  ok:      boolean
  chunks?: number
  tokens?: number
  error?:  string
}

export interface DocumentRow {
  collection: string
  source:     string
  title:      string
  chunks:     number
  updatedAt:  string
}

export interface DocumentsPage {
  total:    number
  page:     number
  pageSize: number
  items:    DocumentRow[]
}

export interface CollectionRow {
  collection:  string
  sources:     number
  chunks:      number
  lastUpdated: string
}

export interface SkillRow {
  id:   string
  size: number
}

// ── Calls ────────────────────────────────────────────────────────────────────

export async function uploadFiles(files: File[], collection: string): Promise<UploadResult[]> {
  const fd = new FormData()
  fd.append('collection', collection)
  for (const f of files) fd.append('files', f, f.name)

  const r = await fetch('/api/admin/upload', {
    method: 'POST',
    headers: authHeaders(),
    body:    fd,
  })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}

export async function listDocuments(
  collection: string | null,
  page = 1,
  pageSize = 20,
): Promise<DocumentsPage> {
  const params = new URLSearchParams()
  if (collection) params.set('collection', collection)
  params.set('page', String(page))
  params.set('pageSize', String(pageSize))

  const r = await fetch(`/api/admin/documents?${params}`, { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function listCollections(): Promise<CollectionRow[]> {
  const r = await fetch('/api/admin/collections', { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function deleteDocument(collection: string, source: string): Promise<{ deleted: number }> {
  const r = await fetch(
    `/api/admin/documents/${encodeURIComponent(collection)}/${encodeURI(source)}`,
    { method: 'DELETE', headers: authHeaders() },
  )
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function listSkills(): Promise<SkillRow[]> {
  const r = await fetch('/api/admin/skills', { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function getSkill(id: string): Promise<string> {
  const r = await fetch(`/api/admin/skills/${encodeURIComponent(id)}`, { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.text()
}

export async function uploadSkills(files: FileList): Promise<{ file: string; ok: boolean; id?: string; error?: string }[]> {
  const fd = new FormData()
  for (const f of Array.from(files)) fd.append('files', f)
  const r = await fetch('/api/admin/skills', {
    method: 'POST',
    headers: authHeaders(),
    body:    fd,
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function deleteSkill(id: string): Promise<{ deleted: string }> {
  const r = await fetch(`/api/admin/skills/${encodeURIComponent(id)}`, {
    method:  'DELETE',
    headers: authHeaders(),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

// ── Usage (LiteLLM spend) ────────────────────────────────────────────────────

export interface UserSpend  { user_id: string; total_spend: number; total_tokens: number }
export interface ModelSpend { model: string; total_tokens: number; total_count: number }
export interface SpendLog   { request_id: string; user: string; model: string; prompt_tokens: number; completion_tokens: number; total_tokens: number; startTime: string }

async function usageGet(path: string) {
  const r = await fetch(path, { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  const d = await r.json()
  return Array.isArray(d) ? d : (d.users ?? d.models ?? d.logs ?? d.data ?? [])
}

export const getUsageUsers  = (): Promise<UserSpend[]>  => usageGet('/api/admin/usage/session-users')
  .then((rows: any[]) => rows.map(r => ({ user_id: r.userId, total_spend: 0, total_tokens: r.totalTokens })))
export const getUsageModels = (): Promise<ModelSpend[]> => usageGet('/api/admin/usage/models')
export const getUsageLogs   = (limit = 50): Promise<SpendLog[]> => usageGet(`/api/admin/usage/logs?limit=${limit}`)
