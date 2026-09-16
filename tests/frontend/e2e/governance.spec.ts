import { expect, Route, test } from '@playwright/test'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

test('admin can review grouped usage, historical rollups, rotate credentials and configure caller governance', async ({ page }) => {
  const groups = [{ id: 'group-1', name: 'Development CRM', description: 'CRM team', credentialCount: 1, createdAtUtc: '2026-09-14T10:00:00Z', updatedAtUtc: '2026-09-14T10:00:00Z' }]
  const credentials = [{ id: 'credential-1', name: 'Copilot CRM', keyPrefix: 'lp_abcd', enabled: true, usageGroupId: 'group-1', createdAtUtc: '2026-09-14T10:00:00Z' }]
  const policies: Array<Record<string, unknown>> = []

  await page.route('**/api/admin/**', async route => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname

    if (request.method() === 'GET' && path === '/api/admin/usage-groups') return json(route, groups)
    if (request.method() === 'GET' && path === '/api/admin/governance/credentials') return json(route, credentials)
    if (request.method() === 'GET' && path === '/api/admin/rate-limits') return json(route, policies)
    if (request.method() === 'GET' && path === '/api/admin/models') return json(route, [{ id: 'model-1', publicName: 'agic-code-fast', providerModelName: 'provider', supportsStreaming: true, supportsTools: true, enabled: true }])
    if (request.method() === 'GET' && path === '/api/admin/usage/summary') return json(route, {
      windowDays: Number(url.searchParams.get('days') ?? 30), sinceUtc: '2026-08-18T00:00:00Z', windowGranularity: 'utc_day',
      rawRetentionDays: 90, rollupRetentionDays: 730, rawRequestCount: 12, rolledUpRequestCount: 30, historicalRollupsUsed: true,
      requestCount: 42, errorCount: 2, inputTokens: 1000, outputTokens: 500, totalTokens: 1500,
      rateLimitedRequests: 3, capacityExhaustedRequests: 1,
      groups: [{ usageGroupId: 'group-1', name: 'Development CRM', requestCount: 42, errorCount: 2, inputTokens: 1000, outputTokens: 500, totalTokens: 1500, rateLimitedRequests: 3, averageTtftMilliseconds: 210, averageDurationMilliseconds: 1200 }],
      credentials: [{ apiCredentialId: 'credential-1', name: 'Copilot CRM', keyPrefix: 'lp_abcd', usageGroupId: 'group-1', requestCount: 42, errorCount: 2, inputTokens: 1000, outputTokens: 500, totalTokens: 1500, rateLimitedRequests: 3 }],
      models: [{ logicalModel: 'agic-code-fast', requestCount: 42, errorCount: 2, inputTokens: 1000, outputTokens: 500, totalTokens: 1500, rateLimitedRequests: 3 }]
    })
    if (request.method() === 'POST' && path === '/api/admin/api-credentials/credential-1/rotate') {
      credentials[0].keyPrefix = 'lp_rotated'
      return json(route, { ...credentials[0], secret: 'lp_rotated_secret_once', expiresAtUtc: null, lastUsedAtUtc: null })
    }
    if (request.method() === 'POST' && path === '/api/admin/rate-limits') {
      const input = request.postDataJSON() as { apiCredentialId: string; logicalModel: string | null; requestsPerWindow: number; windowSeconds: number; enabled: boolean }
      const created = { id: 'rate-1', credentialName: 'Copilot CRM', keyPrefix: 'lp_rotated', outputTokensPerWindow: null, maxOutputTokensPerRequest: null, ...input, createdAtUtc: '2026-09-14T10:00:00Z', updatedAtUtc: '2026-09-14T10:00:00Z' }
      policies.push(created)
      return json(route, created, 201)
    }
    if (request.method() === 'PUT' && path === '/api/admin/rate-limits/rate-1/output-token-budget') {
      const input = request.postDataJSON() as { outputTokensPerWindow: number; maxOutputTokensPerRequest: number }
      Object.assign(policies[0], input, { updatedAtUtc: '2026-09-15T15:00:00Z' })
      return json(route, policies[0])
    }
    if (request.method() === 'DELETE' && path === '/api/admin/rate-limits/rate-1/output-token-budget') {
      Object.assign(policies[0], { outputTokensPerWindow: null, maxOutputTokensPerRequest: null })
      return route.fulfill({ status: 204 })
    }
    return json(route, { error: `${request.method()} ${path}` }, 500)
  })

  await page.goto('/admin/governance')
  await expect(page.getByRole('heading', { name: 'Usage & Governance', exact: true }).first()).toBeVisible()
  await expect(page.getByTestId('historical-rollup-notice')).toContainText('30 rolled-up requests + 12 raw requests')
  await expect(page.getByText('Development CRM').first()).toBeVisible()
  await expect(page.getByText('Copilot CRM').first()).toBeVisible()

  await page.getByLabel('Usage window').selectOption('365')
  await expect(page.getByRole('heading', { name: 'Usage by logical model' }).locator('..').getByText('365-day UTC window')).toBeVisible()

  const modelSection = page.getByRole('heading', { name: 'Usage by logical model' }).locator('..').locator('..')
  await expect(modelSection.getByRole('row', { name: /agic-code-fast/ })).toBeVisible()

  const credentialSection = page.getByRole('heading', { name: 'Usage by credential' }).locator('..').locator('..')
  await expect(credentialSection.getByRole('row', { name: /Copilot CRM/ })).toContainText('1,500')

  await page.getByRole('button', { name: 'Rotate Copilot CRM' }).click()
  await expect(page.getByText('Credential Copilot CRM rotated. The previous secret is now invalid.')).toBeVisible()
  const rotatedSecret = page.getByTestId('rotated-credential-secret')
  await expect(rotatedSecret).toContainText('lp_rotated_secret_once')
  await expect(rotatedSecret).toContainText('will not be shown again')
  const membershipSection = page.getByRole('heading', { name: 'Credential → group membership & rotation' }).locator('..').locator('..')
  await expect(membershipSection.getByRole('row', { name: /Copilot CRM/ })).toContainText('lp_rotated')
  await expect(page.getByLabel('Usage group for Copilot CRM')).toHaveValue('group-1')

  await page.getByLabel('Requests per window').fill('2')
  await page.getByLabel('Window seconds').fill('60')
  await page.getByRole('button', { name: 'Add rate limit' }).click()
  await expect(page.getByText('Rate-limit policy created and applied live.')).toBeVisible()
  await expect(page.getByText('2 / 60s')).toBeVisible()
  await expect(page.getByText('Not set')).toBeVisible()

  await page.getByLabel('Output tokens per window').fill('17000')
  await page.getByLabel('Max output tokens per request').fill('4096')
  await page.getByRole('button', { name: 'Apply token budget' }).click()
  await expect(page.getByText('Output-token budget updated and applied live.')).toBeVisible()
  await expect(page.getByText('17,000 tokens / 60s')).toBeVisible()
  await expect(page.getByText('max 4,096 / request')).toBeVisible()

  await page.getByRole('button', { name: 'Clear token budget' }).click()
  await expect(page.getByText('Output-token budget cleared. Request-rate policy remains active.')).toBeVisible()
  await expect(page.getByText('Not set')).toBeVisible()
})
