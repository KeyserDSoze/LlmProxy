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
