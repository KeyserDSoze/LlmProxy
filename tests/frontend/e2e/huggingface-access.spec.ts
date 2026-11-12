import { expect, test } from '@playwright/test'
import { FIXED_NOW, installMockAdmin } from './support/mockAdmin'

test('gated Hugging Face failure walks the operator through storing a token and retries the install', async ({ page }) => {
  await page.clock.setFixedTime(new Date(FIXED_NOW))
  await installMockAdmin(page)
  let saved: unknown = null
  let retried = false
  // Routes registered after installMockAdmin take precedence.
  await page.route('**/api/admin/model-management/nodes/node-1/huggingface-access', route => {
    if (route.request().method() === 'PUT') {
      saved = route.request().postDataJSON()
      return route.fulfill({ status: 200, contentType: 'application/json',
        body: JSON.stringify({ configured: true, source: 'admin', account: 'alessandro', verified: true, updatedAtUtc: FIXED_NOW }) })
    }
    return route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ configured: false, source: 'none', account: null, verified: false, updatedAtUtc: null }) })
  })
  await page.route('**/api/admin/model-management/nodes/node-1/install-jobs/job-gated/retry', route => {
    retried = true
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      id: 'job-retry', request: { catalogModelId: 'custom', providerModelName: 'meta/muse-glimmer', runtime: 'llama.cpp', startAfterInstall: false },
      status: 'queued', stage: 'queued', percent: null, detail: 'Queued', createdAtUtc: FIXED_NOW }) })
  })

  await page.goto('/admin/models?node=node-1&panel=activity')
  const guide = page.getByRole('form', { name: 'Hugging Face access needed' })
  await expect(guide).toBeVisible()
  await expect(guide.getByRole('link', { name: /Open the repository · meta\/muse-glimmer/ }))
    .toHaveAttribute('href', 'https://huggingface.co/meta/muse-glimmer')
  await expect(guide.getByRole('link', { name: 'Create a read token' }))
    .toHaveAttribute('href', 'https://huggingface.co/settings/tokens/new?tokenType=read')
  await expect(guide.getByRole('button', { name: 'Save token and retry' })).toBeDisabled()

  const field = guide.getByLabel('Hugging Face access token')
  await expect(field).toHaveAttribute('type', 'password')
  await field.fill('hf_exampleReadToken1234567890')
  await guide.getByRole('button', { name: 'Save token and retry' }).click()

  await expect(guide.getByText('Token verified for account alessandro')).toBeVisible()
  expect(saved).toEqual({ token: 'hf_exampleReadToken1234567890' })
  expect(retried).toBe(true)
  // The secret must not linger in the DOM once it has been sent.
  await expect(field).toHaveValue('')
})

test('other install failures do not offer a token form', async ({ page }) => {
  await installMockAdmin(page)
  await page.goto('/admin/models?node=node-1&panel=activity')
  await expect(page.getByText(/No GGUF file in 'bartowski\/Some-Model-GGUF'/)).toBeVisible()
  await expect(page.getByRole('form', { name: 'Hugging Face access needed' })).toHaveCount(1)
})
