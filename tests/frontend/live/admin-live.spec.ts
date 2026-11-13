import { expect, test, type Page } from '@playwright/test'

/**
 * Admin UI against a real LlmProxy + PostgreSQL stack, without API mocks. It
 * catches contract drift between the React client and the ASP.NET Core API that
 * the mocked suites cannot see. Entra is disabled in this environment.
 */
const workspaces = [
  '/admin/dashboard',
  '/admin/models',
  '/admin/models?panel=activity',
  '/admin/models?panel=advanced',
  '/admin/catalog',
  '/admin/infrastructure',
  '/admin/infrastructure?section=hardware',
  '/admin/infrastructure?section=capacity',
  '/admin/routing',
  '/admin/system-one',
  '/admin/playground',
  '/admin/credentials',
  '/admin/users',
  '/admin/governance',
  '/admin/metrics',
  '/admin/logs',
  '/admin/audit',
  '/admin/help',
  '/admin/settings',
  '/admin/releases'
]

function collectProblems(page: Page) {
  const problems: string[] = []
  page.on('pageerror', error => problems.push('pageerror: ' + error.message))
  page.on('console', message => {
    // Failed requests are judged by status below; 4xx can be a legitimate empty state.
    if (message.type() === 'error' && !message.text().startsWith('Failed to load resource'))
      problems.push('console: ' + message.text())
  })
  page.on('response', response => {
    const url = new URL(response.url())
    const status = response.status()
    if (url.pathname.startsWith('/api/') && (status >= 500 || status === 404 || status === 405))
      problems.push(`http ${status}: ${response.request().method()} ${url.pathname}`)
  })
  return problems
}

async function settle(page: Page) {
  await expect(page.locator('main h1')).toBeVisible()
  await expect(page.locator('.loading')).toHaveCount(0, { timeout: 15_000 })
  await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => undefined)
}

for (const path of workspaces) {
  test(`${path} renders against the real API`, async ({ page }) => {
    const problems = collectProblems(page)
    await page.goto(path)
    await settle(page)
    expect(problems).toEqual([])
  })
}

test('organization key created in the UI authenticates, then stops working once revoked', async ({ page, request }) => {
  const problems = collectProblems(page)
  const name = `live-ui-${Date.now()}`
  await page.goto('/admin/credentials')
  await settle(page)
  await page.getByRole('button', { name: 'Create organization key' }).click()
  await page.getByRole('dialog').getByLabel('Name').fill(name)
  await page.getByRole('button', { name: 'Generate organization API key' }).click()
  const secret = (await page.locator('.secretBox code').innerText()).trim()
  expect(secret.length).toBeGreaterThan(20)

  const models = await request.get('/v1/models', { headers: { Authorization: `Bearer ${secret}` } })
  expect(models.status()).toBe(200)

  const row = page.getByRole('row').filter({ hasText: name })
  await expect(row).toContainText('Enabled')
  // Revoking is irreversible, so the UI asks first.
  page.once('dialog', dialog => void dialog.accept())
  await row.getByRole('button', { name: 'Revoke' }).click()
  await expect(row).toContainText('Revoked')
  await expect.poll(async () => (await request.get('/v1/models', { headers: { Authorization: `Bearer ${secret}` } })).status(),
    { timeout: 15_000 }).toBe(401)
  expect(problems).toEqual([])
})

test('routing strategy chosen in the UI is persisted by the API', async ({ page, request }) => {
  const problems = collectProblems(page)
  const original = (await (await request.get('/api/admin/routing')).json()).strategy as string
  const target = original === 'WeightedRoundRobin' ? 'WeightedLeastLoaded' : 'WeightedRoundRobin'
  try {
    await page.goto('/admin/routing')
    await settle(page)
    await page.getByLabel('Routing strategy').selectOption(target)
    await page.getByRole('button', { name: 'Apply routing strategy' }).click()
    await expect(page.getByText('Routing policy updated live.')).toBeVisible()
    expect((await (await request.get('/api/admin/routing')).json()).strategy).toBe(target)
    await page.reload()
    await settle(page)
    await expect(page.getByLabel('Routing strategy')).toHaveValue(target)
  } finally {
    await request.put('/api/admin/routing', { data: { strategy: original } })
  }
  expect(problems).toEqual([])
})
