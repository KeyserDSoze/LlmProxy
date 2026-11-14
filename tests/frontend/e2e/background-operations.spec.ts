import { expect, test } from '@playwright/test'
import { installMockAdmin } from './support/mockAdmin'

test('uninstall runs in the background and shows the Agent reason when it fails', async ({ page }) => {
  await installMockAdmin(page)
  const operationId = '7a3b0f0e-0000-4000-8000-000000000001'
  const started = '2026-11-12T10:00:00Z'
  let polls = 0
  let queued: string | null = null
  page.on('dialog', dialog => void dialog.accept())

  await page.route('**/api/admin/model-management/deployments/deployment-2/remove-jobs', route => {
    queued = route.request().method()
    return route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify({
      id: operationId, kind: 'model.remove', title: 'Uninstall · custom-01ea8a84', nodeId: 'node-1', deploymentId: 'deployment-2',
      status: 'Running', detail: 'Queued', error: null, startedAtUtc: started, finishedAtUtc: null }) })
  })
  await page.route('**/api/admin/operations', route => {
    if (!queued) return route.fulfill({ contentType: 'application/json', body: '[]' })
    polls += 1
    const failed = polls > 1
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify([{
      id: operationId, kind: 'model.remove', title: 'Uninstall · custom-01ea8a84', nodeId: 'node-1', deploymentId: 'deployment-2',
      status: failed ? 'Failed' : 'Running', detail: failed ? 'Failed' : 'Queued',
      error: failed ? 'Routing was switched off, but the Agent could not complete the uninstall: Management agent returned HTTP 409: Another install, start, stop or edit operation is already running' : null,
      startedAtUtc: started, finishedAtUtc: failed ? '2026-11-12T10:00:03Z' : null }]) })
  })

  await page.goto('/admin/models?node=node-1')
  await page.getByLabel(/Show stopped models/).check()
  const card = page.getByRole('article', { name: 'gemma-vision-gguf' })
  await card.getByRole('button', { name: 'Uninstall' }).click()

  expect(queued).toBe('POST')
  await expect(page.getByText(/started in the background/)).toBeVisible()
  await expect(card.locator('.installedModelWorking')).toContainText('Uninstall · custom-01ea8a84')
  await expect(card.getByRole('button', { name: 'Uninstall' })).toBeDisabled()
  await expect(page.getByRole('button', { name: /\d+ running/ })).toBeVisible()

  await expect(page.getByText(/Management agent returned HTTP 409/).first()).toBeVisible({ timeout: 15_000 })
  await expect(card.getByRole('button', { name: 'Uninstall' })).toBeEnabled()
})

test('capability checks run in the background and the result reaches the card', async ({ page }) => {
  await installMockAdmin(page)
  const operationId = '7a3b0f0e-0000-4000-8000-000000000002'
  let queued = false
  let polls = 0
  const base = { id: operationId, kind: 'model.verify', title: 'Verify capabilities · qwen3-8b-1a2b3c4d', nodeId: 'node-1', deploymentId: 'deployment-1', startedAtUtc: '2026-11-12T10:00:00Z' }

  await page.route('**/api/admin/model-management/deployments/deployment-1/verify-jobs', route => {
    queued = true
    return route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify({ ...base, status: 'Running', detail: 'Queued' }) })
  })
  await page.route('**/api/admin/operations', route => {
    if (!queued) return route.fulfill({ contentType: 'application/json', body: '[]' })
    polls += 1
    const done = polls > 1
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify([done
      ? { ...base, status: 'Succeeded', detail: 'Checks finished.', finishedAtUtc: '2026-11-12T10:00:09Z', data: {
          deploymentId: 'deployment-1', model: 'agic-code-fast', checkedAtUtc: '2026-11-12T10:00:09Z', durationMilliseconds: 9000,
          checks: [{ id: 'chat', status: 'ok', detail: 'Answered in 1.2 s.' }, { id: 'tools', status: 'failed', detail: 'No tool call returned.' }] } }
      : { ...base, status: 'Running', detail: 'Queued' }]) })
  })

  await page.goto('/admin/models?node=node-1')
  const card = page.getByRole('article', { name: 'agic-code-fast' })
  await card.getByRole('button', { name: 'Verify capabilities' }).click()

  await expect(card.getByRole('button', { name: /Checking|Verifying|…/ })).toBeVisible()
  await expect(card.getByText('Answered in 1.2 s.')).toBeVisible({ timeout: 15_000 })
  await expect(card.getByText('No tool call returned.')).toBeVisible()
})
