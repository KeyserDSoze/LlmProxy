import { expect, test } from '@playwright/test'
import { installMockAdmin, node1Overview, enginesOverview } from './support/mockAdmin'

test('a model the server restarts by itself is shown, with what it is doing', async ({ page }) => {
  await installMockAdmin(page)
  await page.route('**/api/admin/model-management/nodes/node-1/overview', route => {
    const body = node1Overview() as { installations: { logicalModel: string; agentState: Record<string, unknown> }[] }
    const stopped = body.installations.find(item => item.logicalModel === 'lfm25-24b')!
    stopped.agentState = { ...stopped.agentState, desiredRunning: true, autoStart: 'retrying', autoStartDetail: 'Start failed (2 of 5); next try in 2 min. out of memory' }
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
  })
  await page.goto('/admin/models?node=node-1&panel=installed')

  // Stopped models are hidden by default, but not one the Agent is bringing back.
  const card = page.getByRole('article', { name: 'lfm25-24b' })
  await expect(card).toBeVisible()
  await expect(card).toContainText('Restarting automatically')
  await expect(card).toContainText('next try in 2 min')
})

test('after the Agent gave up the card says it stopped trying', async ({ page }) => {
  await installMockAdmin(page)
  await page.route('**/api/admin/model-management/nodes/node-1/overview', route => {
    const body = node1Overview() as { installations: { logicalModel: string; agentState: Record<string, unknown> }[] }
    const stopped = body.installations.find(item => item.logicalModel === 'lfm25-24b')!
    stopped.agentState = { ...stopped.agentState, desiredRunning: true, autoStart: 'gave_up', autoStartDetail: 'Stopped trying after 5 failed starts. Start it by hand once the cause is fixed.' }
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
  })
  await page.goto('/admin/models?node=node-1&panel=installed')

  await expect(page.getByRole('article', { name: 'lfm25-24b' })).toContainText('Automatic restart stopped')
})

test('an engine update shows in the activity dock and leads back to the engines of that server', async ({ page }) => {
  await installMockAdmin(page)
  enginesOverview.jobs = [{ id: '7a3b0f0e-0000-4000-8000-0000000000bb', engineId: 'llama.cpp', engineLabel: 'llama.cpp', action: 'update', status: 'running',
    detail: 'Downloading ghcr.io/ggml-org/llama.cpp:server-cuda', error: null, result: null, createdAtUtc: new Date().toISOString(), completedAtUtc: null }]
  try {
    await page.goto('/admin/dashboard')
    const dock = page.getByRole('button', { name: /\d+ running/ })
    await expect(dock).toBeVisible()
    await dock.click()

    const item = page.locator('.activityItem', { hasText: 'Update engine · llama.cpp' })
    await expect(item).toContainText('Downloading')
    // Free to move around: the dock follows the operation, and one click returns to the engines of that server.
    await item.getByRole('button', { name: 'Open' }).click()
    await expect(page).toHaveURL(/section=engines&node=node-1/)
    await expect(page.getByLabel('Engines server')).toHaveValue('node-1')
  } finally { enginesOverview.jobs = [] }
})
