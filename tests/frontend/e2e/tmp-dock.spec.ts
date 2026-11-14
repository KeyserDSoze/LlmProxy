import { test } from '@playwright/test'
import { installMockAdmin } from './support/mockAdmin'

test('capture dock', async ({ page }) => {
  test.setTimeout(120000)
  await installMockAdmin(page)
  await page.route('**/api/admin/model-management/nodes/*/install-jobs', route => route.fulfill({ contentType: 'application/json', body: '[]' }))
  let calls = 0
  const job = (status: string) => ({ id: '7a3b0f0e-0000-4000-8000-0000000000dd', engineId: 'llama.cpp', engineLabel: 'llama.cpp', action: 'update', status,
    detail: 'Downloading', error: null, result: status === 'completed' ? { changed: true, message: 'llama.cpp updated.' } : null, createdAtUtc: new Date().toISOString(), completedAtUtc: status === 'completed' ? new Date().toISOString() : null })
  await page.route('**/api/admin/engines/nodes/*/jobs', route => { calls += 1; return route.fulfill({ contentType: 'application/json', body: JSON.stringify([job(calls < 3 ? 'running' : 'completed')]) }) })
  await page.goto('/admin/dashboard')
  await page.getByRole('button', { name: /Finished/ }).waitFor({ timeout: 90000 })
  await page.waitForTimeout(300)
  await page.screenshot({ path: '/work/tests/frontend/tmp-dock.png', clip: { x: 1000, y: 620, width: 420, height: 260 } })
  await page.getByRole('button', { name: 'Clear finished' }).click()
  await page.waitForTimeout(400)
  console.log('DOCK_AFTER_CLEAR', await page.locator('.activityDock').count())
})
