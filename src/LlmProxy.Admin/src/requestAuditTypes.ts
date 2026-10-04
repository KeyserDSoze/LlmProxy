export type RequestAuditSummary = {
  contentLogId: number
  summary: string
  logicalModel: string
  nodeId?: string | null
  deploymentId?: string | null
  generatedBy: string
  generatedAtUtc: string
  updatedAtUtc: string
}

export type RequestAuditSummarySettings = {
  systemPrompt: string
  defaultLogicalModel?: string | null
  defaultNodeId?: string | null
  defaultSystemPrompt: string
  updatedAtUtc: string
}

export type RequestAuditSummaryState = {
  hasSummary?: boolean
  summaryUpdatedAtUtc?: string | null
}
