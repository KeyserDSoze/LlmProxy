import { expect, test } from '@playwright/test'
import { installMockAdmin } from './support/mockAdmin'

test('the help page documents every client API, starting with how to discover models and their limits', async ({ page }) => {
  await installMockAdmin(page)
  await page.goto('/admin/help')
  const endpoints = page.getByRole('region', { name: 'Client endpoints' })

  await expect(endpoints.getByRole('tab', { name: /Discover & limits/ })).toHaveAttribute('aria-selected', 'true')
  await expect(endpoints.getByRole('heading', { name: 'Model details and limits' })).toBeVisible()
  await expect(endpoints.locator('pre', { hasText: 'curl "$BASE_URL/v1/models/details?type=chat"' })).toBeVisible()
  // The response example is built from the models published in this gateway.
  await expect(endpoints.getByRole('heading', { name: 'What /v1/models/details returns for your model' })).toBeVisible()
  await expect(endpoints.locator('.docCard', { hasText: 'What /v1/models/details returns' }).locator('pre')).toContainText('"id": "agic-code-fast"')
  await expect(endpoints.locator('.docCard', { hasText: 'Size a prompt' }).locator('pre')).toContainText('promptBudget')
  // The meaning of each field, and where an administrator changes the values.
  for (const field of ['context_window', 'max_output_tokens', 'input_modalities / output_modalities', 'capabilities'])
    await expect(endpoints.getByRole('cell', { name: field, exact: true })).toBeVisible()
  await expect(endpoints.getByText(/Models → Installed → Published properties/)).toBeVisible()
  await expect(endpoints.getByRole('row', { name: /embedding.*\/v1\/embeddings/ })).toBeVisible()
})

test('each kind of model has its own tab with a runnable example and the right parameters', async ({ page }) => {
  await installMockAdmin(page)
  await page.goto('/admin/help')
  const endpoints = page.getByRole('region', { name: 'Client endpoints' })
  const card = (title: string) => endpoints.locator('.docCard', { has: page.getByRole('heading', { name: title, exact: true }) })

  await endpoints.getByRole('tab', { name: /^Text/ }).click()
  await expect(page).toHaveURL(/tab=text/)
  await expect(card('Chat Completions (streaming)').locator('pre')).toContainText('"model":"agic-code-fast"')
  await expect(card('Chat Completions with an answer limit').locator('pre')).toContainText('"max_tokens":512')
  await expect(card('Responses').getByText(/max_output_tokens/)).toBeVisible()

  await endpoints.getByRole('tab', { name: /^Images/ }).click()
  await expect(card('Generate an image').locator('pre')).toContainText('base64 -d > image.png')

  await endpoints.getByRole('tab', { name: /^Audio/ }).click()
  await expect(card('Text to speech').locator('pre')).toContainText('"voice":"af_heart"')
  await expect(card('Speech to text').locator('pre')).toContainText('-F file=@audio.wav')
  await expect(card('Speech to English text').locator('pre')).toContainText('/v1/audio/translations')

  await endpoints.getByRole('tab', { name: /^Embeddings/ }).click()
  await expect(card('Embeddings').locator('pre')).toContainText('/v1/embeddings')

  await endpoints.getByRole('tab', { name: /^Voice/ }).click()
  await expect(card('Streaming voice (WebSocket)').locator('pre')).toContainText('/v1/realtime?model=')
  await expect(card('Streaming voice (WebSocket)').getByText(/browser WebSocket cannot set the Authorization header/)).toBeVisible()

  await endpoints.getByRole('tab', { name: /^System One/ }).click()
  await expect(card('System One classifier').locator('pre')).toContainText('X-LlmProxy-Model: systemone-laya')
  await expect(card('List classifiers').locator('pre')).toContainText('/v1/systemone/models')
})

test('a tab can be linked to and shows how many models of that kind are published', async ({ page }) => {
  await installMockAdmin(page)
  await page.goto('/admin/help?tab=audio')
  const endpoints = page.getByRole('region', { name: 'Client endpoints' })

  await expect(endpoints.getByRole('tab', { name: /^Audio/ })).toHaveAttribute('aria-selected', 'true')
  await expect(endpoints.getByRole('heading', { name: 'Text to speech' })).toBeVisible()
  await expect(endpoints.getByRole('tab', { name: /^Text/ })).toContainText('2')
  await expect(endpoints.getByRole('tab', { name: /^System One/ })).toContainText('1')
  await expect(endpoints.getByRole('tab', { name: /^Images/ })).toContainText('0')
})

test('an example can be copied exactly as shown', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write'])
  await installMockAdmin(page)
  await page.goto('/admin/help')
  const card = page.getByRole('region', { name: 'Client endpoints' }).locator('.docCard', { has: page.getByRole('heading', { name: 'List models', exact: true }) })
  const shown = await card.locator('pre').innerText()

  await card.getByRole('button', { name: 'Copy' }).click()

  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(shown)
})
