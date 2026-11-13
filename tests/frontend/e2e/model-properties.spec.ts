import { expect, test } from '@playwright/test'
import { installMockAdmin } from './support/mockAdmin'

test('pasting a Hugging Face link fills the form and publishes what the model can do', async ({ page }) => {
  await installMockAdmin(page)
  let queued: Record<string, unknown> | null = null
  await page.route('**/api/admin/model-management/nodes/*/models/custom/install-jobs', route => {
    if (route.request().method() !== 'POST') return route.fallback()
    queued = route.request().postDataJSON()
    return route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify({
      id: '11111111-1111-4111-8111-111111111111', request: { catalogModelId: 'custom', providerModelName: 'meta-models/Muse-Glimmer-30B', runtime: 'vllm' },
      status: 'queued', stage: 'queued', detail: 'Queued', createdAtUtc: '2026-11-12T10:00:00Z' }) })
  })

  await page.goto('/admin/models?node=node-1')
  await page.getByRole('tab', { name: /Discover/ }).click()
  await page.getByRole('button', { name: 'Install custom Hugging Face model' }).click()
  const dialog = page.getByRole('dialog', { name: 'Install inference profile' })

  // A pasted link becomes org/model at once.
  await dialog.getByLabel('Custom Hugging Face repository').fill('https://huggingface.co/meta-models/Muse-Glimmer-30B/tree/main')
  await expect(dialog.getByLabel('Custom Hugging Face repository')).toHaveValue('meta-models/Muse-Glimmer-30B')
  await expect(dialog.getByText(/Filled in from Hugging Face/)).toBeVisible()

  // What clients are told comes pre-filled from the repository metadata.
  const properties = dialog.getByRole('group', { name: 'What clients are told about this model' })
  await expect(properties.getByText('Hugging Face repository metadata')).toBeVisible()
  await expect(properties.getByRole('checkbox', { name: /Tool calling/ })).toBeChecked()
  await expect(properties.getByRole('checkbox', { name: /Image input/ })).toBeChecked()
  await expect(properties.getByRole('checkbox', { name: /Reasoning/ })).toBeChecked()
  await expect(properties.getByLabel('Context window (tokens)')).toHaveValue('32768')
  await expect(properties.getByLabel('Maximum output (tokens)')).toHaveValue('8192')

  // The operator can still change anything; the change survives a refreshed suggestion.
  await properties.getByRole('checkbox', { name: /Reasoning/ }).uncheck()
  await properties.getByLabel('Maximum output (tokens)').fill('4096')

  // The long list of runtime settings is tucked away.
  await expect(dialog.getByLabel('Tensor parallel size')).toBeHidden()
  await dialog.getByText('Advanced runtime settings').click()
  await expect(dialog.getByLabel('Tensor parallel size')).toBeVisible()

  await dialog.getByRole('checkbox', { name: /license|checkpoint/i }).check()
  await dialog.getByRole('button', { name: /Download|Schedule/ }).click()

  await expect.poll(() => queued).not.toBeNull()
  expect(queued!.providerModelName).toBe('meta-models/Muse-Glimmer-30B')
  expect(queued!.declared).toEqual({ streaming: true, tools: true, vision: true, reasoning: false, contextWindow: 32768, maxOutputTokens: 4096 })
})

test('published properties of an installed model can be edited', async ({ page }) => {
  await installMockAdmin(page)
  let saved: Record<string, unknown> | null = null
  await page.route('**/api/admin/models/model-1/properties', route => {
    saved = route.request().postDataJSON()
    return route.fulfill({ contentType: 'application/json', body: '{}' })
  })

  await page.goto('/admin/models?node=node-1')
  const card = page.getByRole('article', { name: 'agic-code-fast' })
  await expect(card.getByText('Published to clients')).toBeVisible()
  await card.getByRole('button', { name: 'Edit published properties' }).click()

  const dialog = page.getByRole('dialog', { name: 'Edit published properties' })
  await dialog.getByRole('checkbox', { name: /Image input/ }).check()
  await dialog.getByLabel('Context window (tokens)').fill('65536')
  await dialog.getByLabel('Maximum output (tokens)').fill('100000')
  await expect(dialog.getByRole('alert')).toContainText('cannot be larger than the context window')
  await expect(dialog.getByRole('button', { name: 'Save properties' })).toBeDisabled()
  await dialog.getByLabel('Maximum output (tokens)').fill('8192')
  await dialog.getByRole('button', { name: 'Save properties' }).click()

  await expect.poll(() => saved).not.toBeNull()
  expect(saved).toMatchObject({ supportsVision: true, contextWindowTokens: 65536, maxOutputTokens: 8192 })
})
