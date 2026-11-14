import { expect, test } from '@playwright/test'
import { installMockAdmin, models, deployments } from './support/mockAdmin'

const png = '\x89PNG\r\n'
const kinds = [
  { surface: 'ImageGeneration', name: 'ssd-demo' },
  { surface: 'SpeechSynthesis', name: 'kokoro-demo' },
  { surface: 'AudioTranscription', name: 'whisper-demo' },
  { surface: 'Embedding', name: 'bge-demo' },
  { surface: 'Embedding', name: 'bge-stopped', running: false }
]

async function install(page: import('@playwright/test').Page) {
  await installMockAdmin(page)
  const extraModels = kinds.map(item => ({ id: 'model-' + item.name, publicName: item.name, providerModelName: 'org/' + item.name, supportsStreaming: false, supportsTools: false, surface: item.surface, enabled: true }))
  const extraDeployments = kinds.map(item => ({ id: 'deployment-' + item.name, nodeId: 'node-1', modelId: 'model-' + item.name, enabled: item.running !== false, weight: 1, maxConcurrency: 1 }))
  await page.route('**/api/admin/models', route => route.request().method() === 'GET'
    ? route.fulfill({ contentType: 'application/json', body: JSON.stringify([...models, ...extraModels]) }) : route.fallback())
  await page.route('**/api/admin/deployments', route => route.request().method() === 'GET'
    ? route.fulfill({ contentType: 'application/json', body: JSON.stringify([...deployments, ...extraDeployments]) }) : route.fallback())
}

test('every kind of model has a Playground tab that offers only its own running models', async ({ page }) => {
  await install(page)
  await page.goto('/admin/playground')

  const tabs = page.getByRole('tablist')
  for (const name of ['Model chat', 'Images', 'Text to speech', 'Speech to text', 'Embeddings', 'System One classifier'])
    await expect(tabs.getByRole('tab', { name: new RegExp(name) })).toBeVisible()

  await tabs.getByRole('tab', { name: /Embeddings/ }).click()
  const select = page.getByLabel('Active embedding model')
  await expect(select.locator('option')).toHaveText(['bge-demo'])
  await tabs.getByRole('tab', { name: /^Images/ }).click()
  await expect(page.getByLabel('Active image model').locator('option')).toHaveText(['ssd-demo'])
})

test('image generation shows the picture and the call summary', async ({ page }) => {
  await install(page)
  let body: Record<string, unknown> | null = null
  await page.route('**/api/admin/testing/images', route => {
    body = route.request().postDataJSON()
    return route.fulfill({ contentType: 'application/json', headers: { 'x-llmproxy-request-id': 'req-image-1' },
      body: JSON.stringify({ data: [{ b64_json: Buffer.from(png).toString('base64') }] }) })
  })
  await page.goto('/admin/playground?tab=image')
  await page.getByLabel('Image size').selectOption('256x256')
  await page.getByRole('button', { name: 'Run image test' }).click()

  await expect(page.getByAltText('Generated image')).toBeVisible()
  await expect(page.getByText('req-image-1')).toBeVisible()
  expect(body).toMatchObject({ model: 'ssd-demo', size: '256x256', n: 1, response_format: 'b64_json' })
})

test('text to speech returns a player', async ({ page }) => {
  await install(page)
  let body: Record<string, unknown> | null = null
  await page.route('**/api/admin/testing/speech', route => {
    body = route.request().postDataJSON()
    return route.fulfill({ contentType: 'audio/wav', body: Buffer.from('RIFF....WAVE') })
  })
  await page.goto('/admin/playground?tab=speech')
  await page.getByRole('button', { name: 'Run speech test' }).click()

  await expect(page.getByLabel('Generated speech')).toBeVisible()
  expect(body).toMatchObject({ model: 'kokoro-demo', voice: 'af_heart', response_format: 'wav' })
})

test('speech to text uploads the file and shows the transcript', async ({ page }) => {
  await install(page)
  let posted = ''
  await page.route('**/api/admin/testing/transcriptions', route => {
    posted = route.request().postData() ?? ''
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ text: 'hello from the recording' }) })
  })
  await page.goto('/admin/playground?tab=transcription')
  await page.getByLabel('Audio file').setInputFiles({ name: 'clip.wav', mimeType: 'audio/wav', buffer: Buffer.from('RIFFxxxxWAVE') })
  await page.getByRole('button', { name: 'Run transcription test' }).click()

  await expect(page.getByText('hello from the recording')).toBeVisible()
  expect(posted).toContain('whisper-demo')
  expect(posted).toContain('clip.wav')
})

test('embeddings show count, dimensions and a vector preview; a failure is readable', async ({ page }) => {
  await install(page)
  let calls = 0
  await page.route('**/api/admin/testing/embeddings', route => {
    calls += 1
    if (calls > 1) return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: { message: 'The model is not running.', code: 'model_not_running' } }) })
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify({
      data: [{ embedding: [0.1, 0.2, 0.3, 0.4] }, { embedding: [0.5, 0.6, 0.7, 0.8] }], usage: { prompt_tokens: 12 } }) })
  })
  await page.goto('/admin/playground?tab=embedding')
  await page.getByRole('button', { name: 'Run embedding test' }).click()
  await expect(page.getByText('2 vectors · 4 dimensions · 12 prompt tokens')).toBeVisible()
  await expect(page.getByText(/first vector: \[0\.1000, 0\.2000/)).toBeVisible()

  await page.getByRole('button', { name: 'Run embedding test' }).click()
  await expect(page.getByRole('alert')).toContainText('The model is not running.')
})
