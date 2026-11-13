import { expect, test } from '@playwright/test'
import { installMockAdmin } from './support/mockAdmin'

test('revoking an API key asks first and does nothing when declined', async ({ page }) => {
  await installMockAdmin(page)
  let revoked = false
  await page.route('**/api/admin/api-credentials/*/revoke', route => { revoked = true; return route.fulfill({ status: 204 }) })
  const prompts: string[] = []
  page.on('dialog', dialog => { prompts.push(dialog.message()); void dialog.dismiss() })

  await page.goto('/admin/credentials')
  await page.getByRole('row', { name: /GitHub Copilot/ }).getByRole('button', { name: 'Revoke' }).click()

  await expect.poll(() => prompts.length).toBe(1)
  expect(prompts[0]).toContain('cannot be re-enabled')
  expect(revoked).toBe(false)
})

test('a failed revoke is shown to the administrator instead of being swallowed', async ({ page }) => {
  await installMockAdmin(page)
  await page.route('**/api/admin/api-credentials/*/revoke', route =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'boom' }) }))
  page.on('dialog', dialog => void dialog.accept())

  await page.goto('/admin/credentials')
  await page.getByRole('row', { name: /GitHub Copilot/ }).getByRole('button', { name: 'Revoke' }).click()

  await expect(page.getByRole('alert').first()).toContainText('boom')
})

test('the routing tuning draft survives the periodic refresh while the operator types', async ({ page }) => {
  await installMockAdmin(page)
  await page.goto('/admin/routing')
  const field = page.getByLabel('Warm-up samples')
  await field.fill('37')

  await page.getByRole('button', { name: 'Refresh' }).first().click()
  await page.waitForTimeout(400)

  await expect(field).toHaveValue('37')
})

test('a read-only administrator cannot submit routing changes', async ({ page }) => {
  await installMockAdmin(page)
  await page.route('**/api/admin/session', route =>
    route.fulfill({ contentType: 'application/json', body: JSON.stringify({ canWrite: false, roles: ['LlmProxy.Reader'] }) }))

  await page.goto('/admin/routing')

  await expect(page.getByRole('button', { name: 'Apply routing strategy' })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Apply smart-routing tuning' })).toBeDisabled()
  await expect(page.getByText(/Read-only access/)).toBeVisible()
})
