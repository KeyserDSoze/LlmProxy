import { expect, test } from '@playwright/test'
import { deployments, installMockAdmin, models } from './support/mockAdmin'

test('running but unrouted models remain in Publishing across navigation and reload; public discovery stays active-only', async ({ page }) => {
  await installMockAdmin(page)
  await page.route('**/api/admin/deployments', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify(deployments.map(item => ({ ...item, enabled: false }))) }))
  await page.goto('/admin/models?panel=advanced')
  const row = page.getByRole('row').filter({ hasText: 'agic-code-fast' })
  await expect(row).toContainText('Running')
  await expect(row).toContainText('API off')
  await expect(row.getByRole('button', { name: 'Check health & activate' })).toBeVisible()
  await page.reload()
  await expect(row).toBeVisible()
  await page.goto('/admin/playground')
  await expect(page.getByLabel('Active OpenAI logical model')).toHaveValue('')
  await page.goto('/admin/models?panel=advanced')
  await expect(row).toBeVisible()
})

test('publish chooses an existing model, prefills the public name and displays running and stopped servers', async ({ page }) => {
  await installMockAdmin(page)
  await page.route('**/api/admin/model-publications/runtime-status', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify([
    { deploymentId: 'deployment-1', nodeId: 'node-1', status: 'running' },
    { deploymentId: 'deployment-2', nodeId: 'node-1', status: 'stopped' }
  ]) }))
  await page.goto('/admin/models?panel=advanced')
  await page.getByRole('button', { name: 'Publish / rename model', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByLabel('Public API name')).toHaveValue('agic-code-fast')
  await expect(dialog.getByLabel('Registered model and server').locator('option').filter({ hasText: 'agic-code-fast' })).toContainText('Running')
  await expect(dialog.getByLabel('Registered model and server').locator('option').filter({ hasText: 'gemma-vision-gguf' })).toContainText('Stopped')
  await dialog.getByLabel('Registered model and server').selectOption('model-2')
  await expect(dialog.getByLabel('Public API name')).toHaveValue('gemma-vision-gguf')
  await expect(dialog.getByRole('textbox')).toHaveCount(1) // The provider is read-only, not a second text field.
})

test('rename preserves model identity and deployment; no start, install or duplicate publication is requested', async ({ page }) => {
  await installMockAdmin(page)
  let current = [...models]
  const writes: string[] = []
  await page.route('**/api/admin/models', route => {
    if (route.request().method() !== 'GET') { writes.push(route.request().method()); return route.fallback() }
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify(current) })
  })
  await page.route('**/api/admin/models/model-1/name', route => {
    writes.push('rename model-1')
    const { publicName } = route.request().postDataJSON()
    current = current.map(item => item.id === 'model-1' ? { ...item, publicName } : item)
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify(current[0]) })
  })
  await page.goto('/admin/models?panel=advanced')
  await page.getByRole('row').filter({ hasText: 'agic-code-fast' }).getByRole('button', { name: 'Public name…' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Public API name').fill('my-coding-model')
  await dialog.getByRole('button', { name: 'Save public name' }).click()
  await expect(page.getByRole('row').filter({ hasText: 'my-coding-model' })).toContainText('API on')
  await page.reload()
  await expect(page.getByRole('row').filter({ hasText: 'my-coding-model' })).toBeVisible()
  expect(writes).toEqual(['rename model-1'])
  expect(current[0].providerModelName).toBe(models[0].providerModelName)
})

test('a taken name is explained in the editor rather than silently creating a duplicate', async ({ page }) => {
  await installMockAdmin(page)
  await page.route('**/api/admin/models/*/name', route => route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ error: 'public_name_taken', message: 'Another model already uses this public name.' }) }))
  await page.goto('/admin/models?panel=advanced&publish=model-1')
  await page.getByLabel('Public API name').fill('existing-name')
  await page.getByRole('button', { name: 'Save public name' }).click()
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('Another model already uses this public name')
})

test('all kinds keep their publication selector isolated', async ({ page }) => {
  await installMockAdmin(page)
  const kinds = ['OpenAi', 'SystemOne', 'ImageGeneration', 'SpeechSynthesis', 'AudioTranscription', 'Embedding', 'Realtime']
  await page.route('**/api/admin/models', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify(kinds.map(surface => ({
    id: surface, publicName: 'only-' + surface, providerModelName: 'org/' + surface, enabled: true, supportsStreaming: false, supportsTools: false, surface
  }))) }))
  for (const kind of kinds) {
    await page.goto(`/admin/models?kind=${kind}&panel=advanced`)
    await page.getByRole('button', { name: 'Publish / rename model', exact: true }).click()
    await expect(page.getByLabel('Registered model and server').locator('option')).toHaveText(['only-' + kind + ' — No server assigned'])
  }
})

test('publishing an external runtime twice never changes the workspace kind', async ({ page }) => {
  await installMockAdmin(page)
  const surfaces: string[] = []
  await page.route('**/api/admin/models', route => {
    if (route.request().method() !== 'POST') return route.fallback()
    const body = route.request().postDataJSON()
    surfaces.push(body.surface)
    return route.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify({ id: 'created', ...body }) })
  })
  await page.goto('/admin/models?kind=Embedding&panel=advanced')
  for (const name of ['first', 'second']) {
    await page.getByRole('button', { name: 'Connect external runtime', exact: true }).click()
    const dialog = page.getByRole('dialog')
    await expect(dialog.getByLabel('Model surface')).toHaveValue('Embedding')
    await expect(dialog.getByLabel('Model surface')).toBeDisabled()
    await dialog.getByLabel('Logical name', { exact: true }).fill(name)
    await dialog.getByLabel('Provider / runtime model name').fill('org/embedding')
    await dialog.getByRole('button', { name: 'Publish model', exact: true }).click()
    await expect(dialog).toHaveCount(0)
  }
  expect(surfaces).toEqual(['Embedding', 'Embedding'])
})
