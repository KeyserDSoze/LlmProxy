import { expect, test } from '@playwright/test'
import { deployments, installMockAdmin, models } from './support/mockAdmin'

test('a published name nothing serves can be removed from the dashboard in one step', async ({ page }) => {
  await installMockAdmin(page)
  let published = [...models, { id: 'model-orphan', publicName: 'meta-muse-glimmer', providerModelName: 'meta/muse-glimmer', supportsStreaming: true, supportsTools: false, surface: 'OpenAi', enabled: true }]
  const deleted: string[] = []
  await page.route('**/api/admin/models', route => route.request().method() === 'GET'
    ? route.fulfill({ contentType: 'application/json', body: JSON.stringify(published) }) : route.fallback())
  await page.route('**/api/admin/models/*', route => {
    if (route.request().method() !== 'DELETE') return route.fallback()
    const id = new URL(route.request().url()).pathname.split('/').pop()!
    deleted.push(id)
    published = published.filter(model => model.id !== id)
    return route.fulfill({ status: 204, body: '' })
  })
  page.on('dialog', dialog => void dialog.accept())

  await page.goto('/admin/dashboard')
  const item = page.getByRole('listitem').filter({ hasText: 'meta-muse-glimmer' })
  await expect(item).toContainText('Published model without an active route')
  await item.getByRole('button', { name: 'Unpublish' }).click()

  await expect(page.getByRole('listitem').filter({ hasText: 'meta-muse-glimmer' })).toHaveCount(0)
  expect(deleted).toEqual(['model-orphan'])
})

test('a name that still has a deployment is not offered for removal here', async ({ page }) => {
  await installMockAdmin(page)
  // Installed on the server but switched off: nothing serves it, yet removing the name would also remove that deployment.
  await page.route('**/api/admin/models', route => route.request().method() === 'GET'
    ? route.fulfill({ contentType: 'application/json', body: JSON.stringify([...models, { id: 'model-off', publicName: 'lfm-switched-off', providerModelName: 'LiquidAI/LFM2.5', supportsStreaming: true, supportsTools: false, surface: 'OpenAi', enabled: true }]) })
    : route.fallback())
  await page.route('**/api/admin/deployments', route => route.request().method() === 'GET'
    ? route.fulfill({ contentType: 'application/json', body: JSON.stringify([...deployments, { id: 'deployment-off', nodeId: 'node-1', modelId: 'model-off', enabled: false, weight: 1 }]) })
    : route.fallback())
  await page.goto('/admin/dashboard')

  const item = page.getByRole('listitem').filter({ hasText: 'lfm-switched-off' })
  await expect(item).toBeVisible()
  await expect(item.getByRole('button', { name: 'Unpublish' })).toHaveCount(0)
})
