import { expect, test, type Page } from '@playwright/test'
import { FIXED_NOW, deployments, installMockAdmin } from './support/mockAdmin'

type Sent = { id: string; body: Record<string, unknown> }

const operation = (id: string) => ({
  id: 'op-' + id, kind: 'model.limits', title: 'Apply limits · agic-code-fast', nodeId: 'node-1', deploymentId: id,
  status: 'Running', detail: 'Queued', error: null, startedAtUtc: FIXED_NOW, finishedAtUtc: null
})

/** Captures every PUT to a deployment's configuration; answers like the gateway does (202 operation, or 200 when only saved). */
async function captureLimits(page: Page, answer: 'operation' | 'saved' = 'operation') {
  const sent: Sent[] = []
  await page.route('**/api/admin/model-management/deployments/*/configuration', route => {
    const id = route.request().url().split('/deployments/')[1].split('/')[0]
    sent.push({ id, body: route.request().postDataJSON() })
    return answer === 'operation'
      ? route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify(operation(id)) })
      : route.fulfill({ contentType: 'application/json', body: JSON.stringify({ deployment: {}, state: {} }) })
  })
  return sent
}

const openCapacity = async (page: Page, model: string) => {
  await page.goto('/admin/models?node=node-1&panel=installed')
  await page.getByLabel(/Show stopped models/).check()
  await page.getByRole('article', { name: model }).getByRole('button', { name: 'Capacity' }).click()
  return page.getByRole('dialog', { name: new RegExp('Capacity · ' + model) })
}

test('a running model takes new limits in one step: it restarts in the background and clients are told the new window', async ({ page }) => {
  await installMockAdmin(page)
  const sent = await captureLimits(page)
  const dialog = await openCapacity(page, 'agic-code-fast')

  await expect(dialog.getByRole('status')).toContainText('16,384')
  const context = dialog.getByLabel('Edit runtime context')
  // Dropdown up to what this model supports, not a number to type.
  await expect(context.locator('option')).toHaveText([
    '4K · 4,096 tokens', '8K · 8,192 tokens', '16K · 16,384 tokens', '32K · 32,768 tokens', 'Custom…'
  ])
  const apply = dialog.getByRole('button', { name: 'Apply and restart' })
  await expect(apply).toBeDisabled()
  // A running model can be changed: there is no "stop it first" step.
  await expect(dialog.getByRole('button', { name: 'Save limits' })).toHaveCount(0)

  await context.selectOption('32768')
  await expect(dialog.getByText('Each request can use up to 32,768 tokens.')).toBeVisible()
  await expect(dialog.getByRole('note')).toContainText('previous ones are restored automatically')
  await apply.click()

  await expect(page.getByText(/Capacity change started in the background/)).toBeVisible()
  await expect(dialog).toHaveCount(0)
  expect(sent).toEqual([{ id: 'deployment-1', body: {
    maxModelLen: 32768, maxNumSeqs: 4, maxOutputTokens: null, gpuMemoryUtilization: 0.7, applyNow: true, syncPublishedContext: true
  } }])
})

test('llama.cpp shows what one request can use when the pool is shared, and a stopped model can be saved or started', async ({ page }) => {
  await installMockAdmin(page)
  const sent = await captureLimits(page, 'saved')
  const dialog = await openCapacity(page, 'lfm25-24b')

  await expect(dialog.getByText('about 8,192 tokens when every slot is busy')).toBeVisible()
  await dialog.getByLabel('Edit runtime sequences').selectOption('2')
  await expect(dialog.getByText('about 16,384 tokens when every slot is busy')).toBeVisible()
  await dialog.getByLabel('Edit runtime context').selectOption('custom')
  await dialog.getByLabel('Custom context in tokens').fill('65536')
  await expect(dialog.getByText('about 32,768 tokens when every slot is busy')).toBeVisible()
  // The engine-specific settings of other engines are not offered.
  await expect(dialog.getByLabel('vLLM GPU memory fraction')).toHaveCount(0)
  await expect(dialog.getByLabel('Edit maximum output tokens')).toHaveCount(0)
  await expect(dialog.getByRole('button', { name: 'Apply and start' })).toBeEnabled()

  await dialog.getByRole('button', { name: 'Save limits' }).click()

  await expect(page.getByText('Limits saved. They apply the next time the model starts.')).toBeVisible()
  expect(sent).toEqual([{ id: 'deployment-4', body: {
    maxModelLen: 65536, maxNumSeqs: 2, maxOutputTokens: null, gpuMemoryUtilization: null, applyNow: false
  } }])
})

test('a stopped model can be started with the new limits straight away', async ({ page }) => {
  await installMockAdmin(page)
  const sent = await captureLimits(page)
  const dialog = await openCapacity(page, 'lfm25-24b')

  await dialog.getByLabel('Edit runtime context').selectOption('65536')
  await dialog.getByRole('button', { name: 'Apply and start' }).click()

  await expect(page.getByText(/Capacity change started in the background/)).toBeVisible()
  expect(sent.map(item => item.body.applyNow)).toEqual([true])
  expect(sent[0].body.maxModelLen).toBe(65536)
})

test('the same change can be sent to the same model on other servers, which never start by surprise', async ({ page }) => {
  await installMockAdmin(page)
  await page.route('**/api/admin/deployments', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify([
    ...deployments,
    { id: 'deployment-1b', nodeId: 'node-2', modelId: 'model-1', enabled: true, weight: 1, maxConcurrency: 4, managedInstallationId: 'qwen3-8b-2b2b2b2b', catalogModelId: 'qwen3-8b' }
  ]) }))
  const sent = await captureLimits(page)
  const dialog = await openCapacity(page, 'agic-code-fast')

  const others = dialog.getByRole('group', { name: 'Same model on other servers' })
  await expect(others.getByLabel('spark-02')).not.toBeChecked()
  // Other servers keep serving the old size, so clients are not promised the new one yet.
  const advertise = dialog.getByLabel(/Also tell clients the new window/)
  await expect(advertise).not.toBeChecked()

  await dialog.getByLabel('Edit runtime context').selectOption('32768')
  await others.getByLabel('spark-02').check()
  await expect(advertise).toBeChecked()
  await dialog.getByRole('button', { name: 'Apply and restart' }).click()

  await expect(page.getByText(/Capacity change started in the background/)).toBeVisible()
  expect(sent).toEqual([
    { id: 'deployment-1', body: { maxModelLen: 32768, maxNumSeqs: 4, maxOutputTokens: null, gpuMemoryUtilization: 0.7, applyNow: true, syncPublishedContext: true } },
    // Only what every engine understands; a stopped server is updated, not started.
    { id: 'deployment-1b', body: { maxModelLen: 32768, maxNumSeqs: 4, maxOutputTokens: null, gpuMemoryUtilization: null, applyNow: true, keepStopped: true } }
  ])
})

test('when the gateway refuses, the reason stays in the dialog and nothing is reported as started', async ({ page }) => {
  await installMockAdmin(page)
  await page.route('**/api/admin/model-management/deployments/*/configuration', route => route.fulfill({
    status: 409, contentType: 'application/json',
    body: JSON.stringify({ error: 'operation_in_progress', message: 'Another operation is already running for this model.' })
  }))
  const dialog = await openCapacity(page, 'agic-code-fast')

  await dialog.getByLabel('Edit runtime context').selectOption('32768')
  await dialog.getByRole('button', { name: 'Apply and restart' }).click()

  await expect(dialog.getByRole('alert')).toContainText('Another operation is already running for this model.')
  await expect(page.getByText(/Capacity change started/)).toHaveCount(0)
})

test('a context the model cannot hold is refused before anything is sent', async ({ page }) => {
  await installMockAdmin(page)
  const sent = await captureLimits(page)
  const dialog = await openCapacity(page, 'lfm25-24b')

  await dialog.getByLabel('Edit runtime context').selectOption('custom')
  await dialog.getByLabel('Custom context in tokens').fill('500000')

  await expect(dialog.getByRole('alert')).toContainText('256 – 131,072 tokens')
  await expect(dialog.getByRole('button', { name: 'Save limits' })).toBeDisabled()
  await expect(dialog.getByRole('button', { name: 'Apply and start' })).toBeDisabled()
  expect(sent).toEqual([])
})

test('a model that is still starting cannot be reconfigured until it is up or has failed', async ({ page }) => {
  await installMockAdmin(page)
  await page.route('**/api/admin/model-management/nodes/*/start-jobs', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify([{
    id: '7a3b0f0e-0000-4000-8000-0000000000dd', installationId: 'lfm25-24b-a2b-gguf-q4-9f8e7d6c', status: 'starting', detail: 'Loading the model', error: null,
    result: null, createdAtUtc: new Date().toISOString(), completedAtUtc: null
  }]) }))
  await page.goto('/admin/models?node=node-1&panel=installed')
  // A model that is loading is listed without asking for stopped ones.
  await expect(page.getByRole('article', { name: 'lfm25-24b' }).getByRole('button', { name: 'Capacity' })).toBeDisabled()
})
