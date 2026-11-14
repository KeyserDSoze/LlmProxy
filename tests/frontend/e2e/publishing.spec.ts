import { expect, test, type Page } from '@playwright/test'
import { installMockAdmin, models, node1Overview } from './support/mockAdmin'

const lfmInstallation = 'lfm25-24b-a2b-gguf-q4-9f8e7d6c'

async function arrange(page: Page, completedAt: Date, agentStatus: string) {
  await installMockAdmin(page)
  const activated: string[] = []
  await page.route('**/api/admin/model-management/deployments/*/activate', route => {
    activated.push(new URL(route.request().url()).pathname)
    return route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ error: 'runtime_not_running', message: 'The Agent reports that this runtime is not running. Start it before activating routing.' }) })
  })
  await page.route('**/api/admin/model-management/nodes/node-1/start-jobs', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify([{
    id: '7a3b0f0e-0000-4000-8000-0000000000cc', installationId: lfmInstallation, status: 'completed', detail: 'Runtime healthy.', error: null,
    result: { status: 'running', runtimeBaseAddress: 'http://agent/runtime/18002' }, createdAtUtc: completedAt.toISOString(), completedAtUtc: completedAt.toISOString() }]) }))
  await page.route('**/api/admin/model-management/nodes/node-1/overview', route => {
    const body = structuredClone(node1Overview()) as { installations: { managedInstallationId: string; agentStatus: string; enabled: boolean }[] }
    const item = body.installations.find(entry => entry.managedInstallationId === lfmInstallation)!
    item.agentStatus = agentStatus
    item.enabled = false
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
  })
  return activated
}

test('a start the Agent remembers from long ago does not raise a false "model is running" error', async ({ page }) => {
  const activated = await arrange(page, new Date(Date.now() - 2 * 24 * 3600_000), 'stopped')
  await page.goto('/admin/models?node=node-1&panel=installed')
  await expect(page.getByRole('article', { name: 'agic-code-fast' })).toBeVisible()
  await page.waitForTimeout(4500)

  expect(activated).toEqual([])
  await expect(page.getByText(/Model is running but routing activation needs attention/)).toHaveCount(0)
})

test('a start that has just finished on a running model is activated', async ({ page }) => {
  const activated = await arrange(page, new Date(Date.now() - 60_000), 'running')
  await page.goto('/admin/models?node=node-1&panel=installed')

  await expect.poll(() => activated.length, { timeout: 10_000 }).toBeGreaterThan(0)
  expect(activated[0]).toContain('/deployment-4/activate')
})

test('a recent start of a model that is no longer running is not activated either', async ({ page }) => {
  const activated = await arrange(page, new Date(Date.now() - 60_000), 'stopped')
  await page.goto('/admin/models?node=node-1&panel=installed')
  await expect(page.getByRole('article', { name: 'agic-code-fast' })).toBeVisible()
  await page.waitForTimeout(4500)

  expect(activated).toEqual([])
})

test('Models on a server shows what is live and what is off, and keeps the unused names out of the way', async ({ page }) => {
  await installMockAdmin(page)
  // A published name nothing runs for yet: it must not crowd the list of what is on the server.
  await page.route('**/api/admin/models', route => route.request().method() === 'GET'
    ? route.fulfill({ contentType: 'application/json', body: JSON.stringify([...models, { id: 'model-unused', publicName: 'meta-muse-glimmer', providerModelName: 'meta/muse-glimmer', supportsStreaming: true, supportsTools: false, surface: 'OpenAi', enabled: true }]) })
    : route.fallback())
  await page.goto('/admin/models?node=node-1&panel=advanced&advancedTab=nodes')
  await page.getByRole('button', { name: 'Manage models' }).first().click()

  const dialog = page.getByRole('dialog', { name: /Models on / })
  await expect(dialog).toContainText('A model answers clients only while it is switched on here')
  const live = dialog.locator('.deployRow', { hasText: 'agic-code-fast' })
  await expect(live).toContainText('Live')
  await expect(live.getByRole('button', { name: 'Disable' })).toBeVisible()
  await expect(live.getByRole('button', { name: 'Capacity' })).toBeVisible()
  // The internal relay address is not shown to the operator.
  await expect(dialog).not.toContainText('llmproxy.invalid')
  await expect(dialog).not.toContainText('runtime/18')

  const rest = dialog.locator('details.deployMore')
  await expect(rest).not.toHaveAttribute('open', '')
  await rest.locator('summary').click()
  await expect(rest).toContainText('meta-muse-glimmer')
  await expect(rest.getByRole('button', { name: 'Deploy' })).toHaveCount(1)
  await expect(live.getByRole('button', { name: 'Deploy' })).toHaveCount(0)
})

test('the Publishing page says that installing a model already publishes it', async ({ page }) => {
  await installMockAdmin(page)
  await page.goto('/admin/models?node=node-1&panel=advanced')

  await expect(page.getByText('You normally do not publish anything by hand')).toBeVisible()
})
