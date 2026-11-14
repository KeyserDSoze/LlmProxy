import { expect, test } from '@playwright/test'
import { installMockAdmin } from './support/mockAdmin'

test('the playground offers only models that are switched on and can take requests', async ({ page }) => {
  await installMockAdmin(page)
  await page.goto('/admin/playground')

  const select = page.getByLabel('Active OpenAI logical model')
  // agic-code-fast is routed; gemma-vision-gguf is registered but its deployment is switched off.
  await expect(select.locator('option')).toHaveText(['agic-code-fast'])
  await expect(select.locator('option', { hasText: 'gemma-vision-gguf' })).toHaveCount(0)
})

test('Installed and Publishing show what runs and keep stopped models one tick away', async ({ page }) => {
  await installMockAdmin(page)
  await page.goto('/admin/models?node=node-1&panel=installed')

  // gemma-vision-gguf is installed but stopped: hidden by default, reachable with the tick.
  await expect(page.getByRole('article', { name: 'agic-code-fast' })).toBeVisible()
  await expect(page.getByRole('article', { name: 'gemma-vision-gguf' })).toHaveCount(0)
  await page.getByLabel(/Show stopped models/).check()
  await expect(page.getByRole('article', { name: 'gemma-vision-gguf' })).toBeVisible()

  await page.goto('/admin/models?panel=advanced')
  await expect(page.getByRole('row').filter({ hasText: 'agic-code-fast' })).toHaveCount(1)
  await expect(page.getByRole('row').filter({ hasText: 'gemma-vision-gguf' })).toHaveCount(0)
  await page.getByLabel(/Show models that are not running/).check()
  await expect(page.getByRole('row').filter({ hasText: 'gemma-vision-gguf' })).toHaveCount(1)
})
