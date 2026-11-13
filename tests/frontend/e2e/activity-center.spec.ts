import { expect, test } from '@playwright/test'
import { installMockAdmin } from './support/mockAdmin'

const upgrading = [{
  nodeId: 'node-1', mode: 'outbound', lastHeartbeatAtUtc: '2026-11-12T09:59:55Z', agentVersion: '0.2.64', desiredAgentVersion: null,
  agentUpdateStatus: 'running:0.2.68:downloading:40', agentUpgradeTargetVersion: '0.2.68', agentUpgradeMaintenanceStage: 'updating',
  tunnelConnected: true, hasRecoverySecret: true, recoverySecretCreatedAtUtc: '2026-10-11T08:00:00Z'
}]

test('an Agent update stays visible in the activity dock after leaving the page', async ({ page }) => {
  await installMockAdmin(page)
  await page.route('**/api/admin/node-enrollment/nodes', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify(upgrading) }))
  await page.goto('/admin/infrastructure')

  const dock = page.getByRole('button', { name: /\d+ running/ })
  await expect(dock).toBeVisible()

  await page.getByRole('link', { name: 'Release notes' }).or(page.getByRole('button', { name: 'Release notes' })).first().click()
  await expect(page).toHaveURL(/\/admin\/releases/)
  await expect(dock).toBeVisible()

  await dock.click()
  await expect(page.getByText(/Agent update · .* → v0\.2\.68/)).toBeVisible()
  await page.locator('.activityItem', { hasText: 'Agent update' }).getByRole('button', { name: 'Open' }).click()
  await expect(page).toHaveURL(/\/admin\/infrastructure/)
})

test('the upgrade notice uses the full table width on a phone', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await installMockAdmin(page)
  await page.route('**/api/admin/node-enrollment/nodes', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify(upgrading) }))
  await page.goto('/admin/infrastructure')

  const notice = page.locator('.agentDetails .notice')
  await expect(notice).toBeVisible()
  const box = await notice.boundingBox()
  expect(box!.width).toBeGreaterThan(250)
  expect(box!.height).toBeLessThan(260)

  // Each server is a card: no cell is squeezed into a narrow column and the table does not scroll sideways.
  const status = await page.locator('.fleetTable td[data-label="Status"]').first().boundingBox()
  expect(status!.width).toBeGreaterThan(250)
  const scroll = await page.locator('.tableScroll').first().evaluate(element => [element.clientWidth, element.scrollWidth])
  expect(scroll[1]).toBeLessThanOrEqual(scroll[0] + 1)
})
