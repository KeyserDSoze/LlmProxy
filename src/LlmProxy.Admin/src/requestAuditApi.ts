import type { RequestAuditSummary, RequestAuditSummarySettings } from './requestAuditTypes'

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    credentials: 'same-origin',
    headers: init?.body ? { 'Content-Type': 'application/json', ...(init.headers ?? {}) } : init?.headers,
    ...init
  })
  if (response.status === 401) throw new Error('AUTH_REQUIRED')
  if (response.status === 403) throw new Error('FORBIDDEN')
  if (!response.ok) {
    const body = await response.text()
    throw new Error(body || `${response.status} ${response.statusText}`)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export const requestAuditApi = {
  summarySettings: () => request<RequestAuditSummarySettings>('/api/admin/content-logs/summary-settings'),
  updateSummarySettings: (body: { systemPrompt: string; defaultLogicalModel?: string | null; defaultNodeId?: string | null }) =>
    request<RequestAuditSummarySettings>('/api/admin/content-logs/summary-settings', { method: 'PUT', body: JSON.stringify(body) }),
  summary: (contentLogId: number) => request<RequestAuditSummary>(`/api/admin/content-logs/${contentLogId}/summary`),
  generateSummary: (contentLogId: number, body: { logicalModel?: string | null; nodeId?: string | null } = {}) =>
    request<RequestAuditSummary>(`/api/admin/content-logs/${contentLogId}/summary`, { method: 'POST', body: JSON.stringify(body) })
}
