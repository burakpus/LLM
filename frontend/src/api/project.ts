// =============================================================================
// project.ts — Project file management API
// =============================================================================

function token(): string {
  return localStorage.getItem('setllm-token') ?? ''
}
function authHeaders(extra?: Record<string, string>) {
  return { Authorization: `Bearer ${token()}`, ...extra }
}

export interface ProjectFileMeta {
  path:      string
  updatedAt: string
  size:      number
}

export async function listFiles(projectId: string): Promise<ProjectFileMeta[]> {
  const r = await fetch(`/api/projects/${encodeURIComponent(projectId)}/files`,
    { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  return r.json()
}

export async function readFile(projectId: string, path: string): Promise<string> {
  const r = await fetch(
    `/api/projects/${encodeURIComponent(projectId)}/files/${encodeURIComponent(path)}`,
    { headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
  const { content } = await r.json()
  return content
}

export async function writeFile(projectId: string, path: string, content: string): Promise<void> {
  const r = await fetch(
    `/api/projects/${encodeURIComponent(projectId)}/files/${encodeURIComponent(path)}`,
    {
      method:  'PUT',
      headers: authHeaders({ 'Content-Type': 'application/json' }),
      body:    JSON.stringify({ content }),
    })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
}

export async function deleteFile(projectId: string, path: string): Promise<void> {
  const r = await fetch(
    `/api/projects/${encodeURIComponent(projectId)}/files/${encodeURIComponent(path)}`,
    { method: 'DELETE', headers: authHeaders() })
  if (!r.ok) throw new Error(`HTTP ${r.status}`)
}
