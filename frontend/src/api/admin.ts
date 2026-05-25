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
  id:              string
  name?:           string
  description?:    string
  isFolder?:       boolean
  referenceCount?: number
  size:            number
}

export interface AnthropicSkillInfo {
  name:        string
  description: string
  hasRefs:     boolean
}

// Pre-defined catalog of all 17 skills in anthropics/skills repo
export const ANTHROPIC_SKILLS: Record<string, AnthropicSkillInfo> = {
  'algorithmic-art':       { name: 'Algorithmic Art',       description: 'Generative art with JavaScript canvas',          hasRefs: false },
  'brand-guidelines':      { name: 'Brand Guidelines',      description: 'Brand voice and visual consistency',             hasRefs: false },
  'canvas-design':         { name: 'Canvas Design',         description: 'HTML Canvas 2D graphics and typography',         hasRefs: false },
  'claude-api':            { name: 'Claude API',            description: 'Anthropic API: models, tools, streaming',        hasRefs: true  },
  'doc-coauthoring':       { name: 'Doc Co-authoring',      description: 'Document collaboration workflows',               hasRefs: false },
  'docx':                  { name: 'DOCX Processing',       description: 'Word document creation and manipulation',        hasRefs: false },
  'frontend-design':       { name: 'Frontend Design',       description: 'UI/UX, responsive design best practices',        hasRefs: false },
  'internal-comms':        { name: 'Internal Comms',        description: 'Company internal communication templates',       hasRefs: true  },
  'mcp-builder':           { name: 'MCP Builder',           description: 'Build Model Context Protocol servers',           hasRefs: true  },
  'pdf':                   { name: 'PDF Processing',        description: 'PDF read, merge, split, forms, OCR',             hasRefs: true  },
  'pptx':                  { name: 'PPTX Processing',       description: 'PowerPoint creation and editing',                hasRefs: true  },
  'skill-creator':         { name: 'Skill Creator',         description: 'Design and evaluate Claude Code skills',         hasRefs: true  },
  'slack-gif-creator':     { name: 'Slack GIF Creator',     description: 'Animated GIF generation for Slack',              hasRefs: false },
  'theme-factory':         { name: 'Theme Factory',         description: '10 pre-built design themes with palettes',       hasRefs: true  },
  'web-artifacts-builder': { name: 'Web Artifacts Builder', description: 'Build deployable HTML/React artifacts',          hasRefs: false },
  'webapp-testing':        { name: 'Web App Testing',       description: 'Browser automation and testing',                 hasRefs: false },
  'xlsx':                  { name: 'XLSX Processing',       description: 'Excel spreadsheet creation and manipulation',    hasRefs: false },
}

export interface ImportAnthropicResult {
  results:  { skill: string; ok: boolean; action?: string; error?: string; files?: number }[]
  imported: number
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

export async function importAnthropicSkills(skills: string[], overwrite = false): Promise<ImportAnthropicResult> {
  const r = await fetch('/api/admin/skills/import-anthropic', {
    method: 'POST',
    headers: authHeaders({ 'Content-Type': 'application/json' }),
    body:    JSON.stringify({ skills, overwrite }),
  })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}

// ── Skill Examples (Few-Shot) ─────────────────────────────────────────────────

export interface SkillExample {
  id:               number
  userMessage:      string
  assistantMessage: string
  sortOrder:        number
}

export async function listSkillExamples(skillId: string): Promise<SkillExample[]> {
  const r = await fetch(`/api/skills/${encodeURIComponent(skillId)}/examples`, { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function createSkillExample(skillId: string, userMessage: string, assistantMessage: string): Promise<{ id: number; sortOrder: number }> {
  const r = await fetch(`/api/admin/skills/${encodeURIComponent(skillId)}/examples`, {
    method:  'POST',
    headers: authHeaders({ 'Content-Type': 'application/json' }),
    body:    JSON.stringify({ userMessage, assistantMessage }),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function updateSkillExample(skillId: string, exId: number, userMessage: string, assistantMessage: string): Promise<void> {
  const r = await fetch(`/api/admin/skills/${encodeURIComponent(skillId)}/examples/${exId}`, {
    method:  'PUT',
    headers: authHeaders({ 'Content-Type': 'application/json' }),
    body:    JSON.stringify({ userMessage, assistantMessage }),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
}

export async function deleteSkillExample(skillId: string, exId: number): Promise<void> {
  const r = await fetch(`/api/admin/skills/${encodeURIComponent(skillId)}/examples/${exId}`, {
    method: 'DELETE', headers: authHeaders(),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
}

// ── Prompt Templates ─────────────────────────────────────────────────────────

export interface PromptTemplate {
  id:         number
  name:       string
  content:    string
  variables:  string[]   // extracted {{variable}} names
  collection: string
  createdBy:  string
  createdAt:  string
}

export async function listTemplates(): Promise<PromptTemplate[]> {
  const r = await fetch('/api/templates', { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function createTemplate(name: string, content: string, collection: string): Promise<PromptTemplate> {
  const r = await fetch('/api/admin/templates', {
    method:  'POST',
    headers: authHeaders({ 'Content-Type': 'application/json' }),
    body:    JSON.stringify({ name, content, collection }),
  })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}

export async function updateTemplate(id: number, name: string, content: string, collection: string): Promise<void> {
  const r = await fetch(`/api/admin/templates/${id}`, {
    method:  'PUT',
    headers: authHeaders({ 'Content-Type': 'application/json' }),
    body:    JSON.stringify({ name, content, collection }),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
}

export async function deleteTemplate(id: number): Promise<void> {
  const r = await fetch(`/api/admin/templates/${id}`, {
    method: 'DELETE', headers: authHeaders(),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
}

// ── Usage (LiteLLM spend) ────────────────────────────────────────────────────

export interface UserSpend  {
  user_id:           string
  total_spend:       number
  total_tokens:      number
  prompt_tokens?:    number
  completion_tokens?: number
  messages?:         number
  last_active?:      string
}
export interface ModelSpend { model: string; total_tokens: number; total_count: number }
export interface SpendLog   { request_id: string; user: string; model: string; prompt_tokens: number; completion_tokens: number; total_tokens: number; startTime: string }

async function usageGet(path: string) {
  const r = await fetch(path, { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  const d = await r.json()
  return Array.isArray(d) ? d : (d.users ?? d.models ?? d.logs ?? d.data ?? [])
}

export const getUsageUsers  = (): Promise<UserSpend[]>  => usageGet('/api/admin/usage/end-users')
  .then((rows: any[]) => rows.map(r => ({
    user_id:           r.userId,
    total_spend:       0,
    total_tokens:      r.totalTokens,
    prompt_tokens:     r.promptTokens,
    completion_tokens: r.completionTokens,
    messages:          r.messages,
    last_active:       r.lastActive,
  })))
export const getUsageModels = (): Promise<ModelSpend[]> => usageGet('/api/admin/usage/models')
export const getUsageLogs   = (limit = 50): Promise<SpendLog[]> => usageGet(`/api/admin/usage/logs?limit=${limit}`)

// ── SQL Connections ──────────────────────────────────────────────────────────

export type SqlDbType = 'mssql' | 'postgres' | 'mysql' | 'oracle'

export interface SqlConnection {
  id:                   number
  name:                 string
  dbType:               SqlDbType
  host:                 string
  port:                 number
  database:             string
  username:             string
  queryTimeoutSec?:     number
  autoSyncIntervalMin?: number   // 0 = disabled
  createdBy:            string
  createdAt:            string
}

export interface SqlConnectionUpsert {
  name:                 string
  dbType:               SqlDbType
  host:                 string
  port:                 number
  database:             string
  username:             string
  password:             string  // empty string on update = keep existing
  queryTimeoutSec?:     number  // 5..3600 — defaults to 120 server-side
  autoSyncIntervalMin?: number  // 0 = otomatik sync devre dışı
}

export async function listSqlConnections(): Promise<SqlConnection[]> {
  const r = await fetch('/api/admin/sql-connections', { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function createSqlConnection(payload: SqlConnectionUpsert): Promise<SqlConnection> {
  const r = await fetch('/api/admin/sql-connections', {
    method:  'POST',
    headers: authHeaders({ 'Content-Type': 'application/json' }),
    body:    JSON.stringify(payload),
  })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}

export async function updateSqlConnection(id: number, payload: SqlConnectionUpsert): Promise<SqlConnection> {
  const r = await fetch(`/api/admin/sql-connections/${id}`, {
    method:  'PUT',
    headers: authHeaders({ 'Content-Type': 'application/json' }),
    body:    JSON.stringify(payload),
  })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}

export async function getSqlConnection(id: number): Promise<SqlConnection> {
  const r = await fetch(`/api/admin/sql-connections/${id}`, { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function deleteSqlConnection(id: number): Promise<void> {
  const r = await fetch(`/api/admin/sql-connections/${id}`, {
    method: 'DELETE', headers: authHeaders(),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
}

export async function testSqlConnection(id: number): Promise<{ ok: boolean; error?: string }> {
  const r = await fetch(`/api/admin/sql-connections/${id}/test`, {
    method: 'POST', headers: authHeaders(),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function testSqlCredentials(payload: SqlConnectionUpsert): Promise<{ ok: boolean; error?: string }> {
  const r = await fetch('/api/admin/sql-connections/test-credentials', {
    method:  'POST',
    headers: authHeaders({ 'Content-Type': 'application/json' }),
    body:    JSON.stringify(payload),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

// ── Schema preview & ingest ──────────────────────────────────────────────────

export interface SqlObjectSummary {
  total:  number
  byType: { type: string; count: number }[]
}

export interface SqlIngestResult {
  total:      number
  success:    number
  chunks:     number
  collection: string
  failures:   { name: string; error: string }[]
}

export interface SqlIngestedStats {
  total:          number
  chunks:         number
  byType:         { type: string; count: number; chunks: number }[]
  lastIngestedAt: string | null
  collection:     string | null
}

export async function getSqlIngestedStats(connId: number): Promise<SqlIngestedStats> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/ingested-stats`, { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function listSqlObjects(connId: number): Promise<SqlObjectSummary> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/list-objects`, {
    method: 'POST', headers: authHeaders(),
  })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}

export async function ingestSqlSchema(
  connId:       number,
  collection:   string,
  includeTypes: string[],
): Promise<SqlIngestResult> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/ingest-schema`, {
    method:  'POST',
    headers: authHeaders({ 'Content-Type': 'application/json' }),
    body:    JSON.stringify({ collection, includeTypes }),
  })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}

export interface SqlSyncResult {
  added:      string[]
  updated:    string[]
  removed:    string[]
  unchanged:  number
  chunks:     number
  collection: string
  failures:   { name: string; error: string }[]
}

export async function syncSqlSchema(connId: number): Promise<SqlSyncResult> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/sync-schema`, {
    method: 'POST', headers: authHeaders(),
  })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}

// ── Data sampling (Phase 3) ──────────────────────────────────────────────────

export interface SqlTable {
  schema:        string
  name:          string
  estimatedRows: number
  columns:       { name: string; dataType: string; isPII: boolean }[]
}

export interface SqlTableSpec {
  schema: string
  name:   string
  limit:  number
  where:  string | null
}

export interface SqlDataIngestResult {
  success:    number
  total:      number
  rows:       number
  chunks:     number
  collection: string
  failures:   { name: string; error: string }[]
}

export async function listSqlTables(connId: number): Promise<SqlTable[]> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/tables`, { headers: authHeaders() })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}

// ── Table Groups + Configs + Delta Sync ──────────────────────────────────────

export interface SqlTableGroup {
  id:        number
  name:      string
  sortOrder: number
}

export interface SqlTableConfig {
  id:                number
  schema:            string
  table:             string
  pkCol:             string
  createdCol:        string
  updatedCol:        string
  rowLimit:          number
  whereClause:       string
  includedColumns:   string[]
  groupId:           number | null
  collection:        string
  lastSyncedAt:      string | null
  lastMaxUpdatedAt:  string | null
  lastSyncStatus:    'ok' | 'failed' | null
  lastSyncAdded:     number
  lastSyncUpdated:   number
  lastSyncError:     string
}

export interface SqlTableConfigUpsert {
  schema:           string
  table:            string
  pkCol:            string
  createdCol:       string
  updatedCol:       string
  rowLimit:         number
  whereClause:      string
  includedColumns:  string[]
  groupId:          number | null
  collection:       string
}

export async function listTableGroups(connId: number): Promise<SqlTableGroup[]> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/table-groups`, { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}
export async function createTableGroup(connId: number, name: string, sortOrder = 0): Promise<{ id: number }> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/table-groups`, {
    method: 'POST', headers: authHeaders({ 'Content-Type': 'application/json' }),
    body: JSON.stringify({ name, sortOrder }),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}
export async function updateTableGroup(connId: number, gid: number, name: string, sortOrder: number): Promise<void> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/table-groups/${gid}`, {
    method: 'PUT', headers: authHeaders({ 'Content-Type': 'application/json' }),
    body: JSON.stringify({ name, sortOrder }),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
}
export async function deleteTableGroup(connId: number, gid: number): Promise<void> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/table-groups/${gid}`, {
    method: 'DELETE', headers: authHeaders(),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
}

export async function listTableConfigs(connId: number): Promise<SqlTableConfig[]> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/table-configs`, { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}
export async function upsertTableConfig(connId: number, cfg: SqlTableConfigUpsert): Promise<{ id: number }> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/table-configs`, {
    method: 'POST', headers: authHeaders({ 'Content-Type': 'application/json' }),
    body: JSON.stringify(cfg),
  })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}
export async function deleteTableConfig(connId: number, tid: number): Promise<void> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/table-configs/${tid}`, {
    method: 'DELETE', headers: authHeaders(),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
}

export async function bulkAssignTableGroup(connId: number, tableConfigIds: number[], groupId: number | null): Promise<{ updated: number }> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/table-configs/bulk-assign-group`, {
    method: 'POST', headers: authHeaders({ 'Content-Type': 'application/json' }),
    body: JSON.stringify({ tableConfigIds, groupId }),
  })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}

export async function syncSqlData(connId: number, tableConfigIds?: number[]): Promise<{ jobId: number }> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/sync-data`, {
    method: 'POST', headers: authHeaders({ 'Content-Type': 'application/json' }),
    body: JSON.stringify({ tableConfigIds: tableConfigIds ?? null }),
  })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}

export async function ingestSqlData(
  connId:       number,
  collection:   string,
  defaultLimit: number,
  tables:       SqlTableSpec[],
): Promise<SqlDataIngestResult> {
  const r = await fetch(`/api/admin/sql-connections/${connId}/ingest-data`, {
    method:  'POST',
    headers: authHeaders({ 'Content-Type': 'application/json' }),
    body:    JSON.stringify({ collection, defaultLimit, tables }),
  })
  if (!r.ok) {
    const err = await r.json().catch(() => ({ error: r.statusText }))
    throw new Error(err?.error ?? `HTTP ${r.status}`)
  }
  return r.json()
}

// ── Background jobs ──────────────────────────────────────────────────────────

export interface JobInfo {
  id:          number
  type:        string
  status:      'queued' | 'running' | 'completed' | 'failed' | 'cancelled'
  progressCur: number
  progressTot: number
  message:     string
  createdBy:   string
  createdAt:   string
  startedAt:   string | null
  completedAt: string | null
  error:       string
  result:      any
}

export async function getJob(id: number): Promise<JobInfo> {
  const r = await fetch(`/api/jobs/${id}`, { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function getLatestJobForConnection(connId: number, type?: string): Promise<JobInfo | null> {
  const url = type
    ? `/api/admin/sql-connections/${connId}/latest-job?type=${encodeURIComponent(type)}`
    : `/api/admin/sql-connections/${connId}/latest-job`
  const r = await fetch(url, { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function listJobs(limit = 20, status?: string): Promise<JobInfo[]> {
  const p = new URLSearchParams({ limit: String(limit) })
  if (status) p.set('status', status)
  const r = await fetch(`/api/jobs?${p}`, { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export interface JobsPage {
  items:    JobInfo[]
  total:    number
  page:     number
  pageSize: number
}

export async function listAdminJobs(opts: { page?: number; pageSize?: number; type?: string; status?: string } = {}): Promise<JobsPage> {
  const p = new URLSearchParams()
  if (opts.page)     p.set('page',     String(opts.page))
  if (opts.pageSize) p.set('pageSize', String(opts.pageSize))
  if (opts.type)     p.set('type',     opts.type)
  if (opts.status)   p.set('status',   opts.status)
  const r = await fetch(`/api/admin/jobs?${p}`, { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function cancelJob(id: number): Promise<{ ok: boolean; error?: string }> {
  const r = await fetch(`/api/admin/jobs/${id}/cancel`, { method: 'POST', headers: authHeaders() })
  return r.json()
}

export async function retryJob(id: number): Promise<{ ok: boolean; newId?: number; error?: string }> {
  const r = await fetch(`/api/admin/jobs/${id}/retry`, { method: 'POST', headers: authHeaders() })
  return r.json()
}

// ── Activity log ─────────────────────────────────────────────────────────────

export interface ActivityEntry {
  id:        number
  username:  string
  action:    string
  target:    string
  details:   string
  createdAt: string
}
export interface ActivityPage {
  total:    number
  page:     number
  pageSize: number
  items:    ActivityEntry[]
}
export async function getActivityLog(page = 1, pageSize = 50, action?: string): Promise<ActivityPage> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (action) params.set('action', action)
  const r = await fetch(`/api/admin/activity-log?${params}`, { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

// ── Rating stats ─────────────────────────────────────────────────────────────
export interface RatingStats {
  total: number; ups: number; downs: number
  byModel: { model: string; total: number; ups: number; downs: number }[]
  recent:  { username: string; rating: number; model: string; createdAt: string }[]
}
export async function getRatingStats(): Promise<RatingStats> {
  const r = await fetch('/api/admin/ratings/stats', { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}
