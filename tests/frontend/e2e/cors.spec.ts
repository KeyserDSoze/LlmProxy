import { expect, test } from '@playwright/test'
import { FIXED_NOW, installMockAdmin } from './support/mockAdmin'

test.beforeEach(async ({ page }) => {
  await page.clock.setFixedTime(new Date(FIXED_NOW))
  await installMockAdmin(page)
})

test('credential list shows the browser origins of each key', async ({ page }) => {
  await page.goto('/admin/credentials')
  const row = page.getByRole('row').filter({ hasText: 'GitHub Copilot' })
  await expect(row).toContainText('https://ghostwriting.ws')
  await expect(row).toContainText('http://localhost:5173')
  await expect(page.getByRole('row').filter({ hasText: 'CI pipeline' })).toContainText('Site-wide only')
})

test('administrator edits the origins of one key', async ({ page }) => {
  let body: unknown = null
  await page.route('**/api/admin/api-credentials/credential-1/allowed-origins', route => {
    body = route.request().postDataJSON()
    return route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ id: 'credential-1', allowedOrigins: ['https://ghostwriting.ws'] }) })
  })
  await page.goto('/admin/credentials')
  await page.getByRole('row').filter({ hasText: 'GitHub Copilot' }).getByRole('button', { name: 'Origins' }).click()

  const dialog = page.getByRole('dialog')
  const field = dialog.getByLabel('Allowed browser origins')
  await expect(field).toHaveValue('https://ghostwriting.ws\nhttp://localhost:5173')
  await field.fill('https://ghostwriting.ws, https://staging.ghostwriting.ws\n*')
  await dialog.getByRole('button', { name: 'Save origins' }).click()

  await expect(dialog).toHaveCount(0)
  expect(body).toEqual({ allowedOrigins: ['https://ghostwriting.ws', 'https://staging.ghostwriting.ws', '*'] })
})

test('the server explains an invalid origin and keeps the dialog open', async ({ page }) => {
  await page.route('**/api/admin/api-credentials/credential-1/allowed-origins', route => route.fulfill({
    status: 400, contentType: 'application/json',
    body: JSON.stringify({ error: 'invalid_origins', message: "'https://bad.example/path' is not a valid origin. Use scheme://host[:port] without a path." }) }))
  await page.goto('/admin/credentials')
  await page.getByRole('row').filter({ hasText: 'GitHub Copilot' }).getByRole('button', { name: 'Origins' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Allowed browser origins').fill('https://bad.example/path')
  await dialog.getByRole('button', { name: 'Save origins' }).click()
  await expect(dialog.getByRole('alert')).toContainText('is not a valid origin')
})

test('new organization key can be created with its origins', async ({ page }) => {
  let body: { name?: string; allowedOrigins?: string[] } | null = null
  await page.route('**/api/admin/api-credentials', route => {
    if (route.request().method() !== 'POST') return route.fallback()
    body = route.request().postDataJSON()
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      id: 'credential-9', name: 'Web app', keyPrefix: 'lp_org_new1', enabled: true, createdAtUtc: FIXED_NOW,
      kind: 'organization', secretAvailable: true, allowedOrigins: ['https://app.example.com'], secret: 'lp_org_new_secret' }) })
  })
  await page.goto('/admin/credentials')
  await page.getByRole('button', { name: 'Create organization key' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Name').fill('Web app')
  await dialog.getByLabel('Allowed browser origins for the new key').fill('https://app.example.com')
  await dialog.getByRole('button', { name: 'Generate organization API key' }).click()

  await expect(page.getByText('lp_org_new_secret')).toBeVisible()
  expect(body).toEqual({ name: 'Web app', allowedOrigins: ['https://app.example.com'] })
})

test('site-wide CORS settings are loaded and saved', async ({ page }) => {
  let body: unknown = null
  await page.route('**/api/admin/cors-settings', route => {
    if (route.request().method() !== 'PUT') return route.fallback()
    body = route.request().postDataJSON()
    return route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ allowedOrigins: ['https://console.agic.it', 'https://ghostwriting.ws'], maxAgeSeconds: 1200, updatedAtUtc: FIXED_NOW }) })
  })
  await page.goto('/admin/credentials')
  const panel = page.getByRole('region', { name: 'Browser access (CORS)' })
  const origins = panel.getByLabel('Site-wide allowed origins')
  await expect(origins).toHaveValue('https://console.agic.it')

  await origins.fill('https://console.agic.it\nhttps://ghostwriting.ws')
  await panel.getByLabel('Preflight cache seconds').fill('1200')
  await panel.getByRole('button', { name: 'Save browser access' }).click()

  await expect(panel.getByText('Browser access settings saved.')).toBeVisible()
  expect(body).toEqual({ allowedOrigins: ['https://console.agic.it', 'https://ghostwriting.ws'], maxAgeSeconds: 1200 })
})

test('personal keys show their origins in the user portal and can be edited', async ({ page }) => {
  let body: unknown = null
  await page.route('**/api/me/api-credentials/my-1/allowed-origins', route => {
    body = route.request().postDataJSON()
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: 'my-1', allowedOrigins: [] }) })
  })
  await page.goto('/admin/me')
  const row = page.getByRole('row').filter({ hasText: 'VS Code Copilot' }).filter({ has: page.getByRole('button', { name: 'Origins' }) })
  await expect(row).toContainText('https://ghostwriting.ws')
  await row.getByRole('button', { name: 'Origins' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Allowed browser origins').fill('')
  await dialog.getByRole('button', { name: 'Save origins' }).click()
  await expect(dialog).toHaveCount(0)
  expect(body).toEqual({ allowedOrigins: [] })
})
