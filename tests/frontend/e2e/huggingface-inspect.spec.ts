import { expect, test } from '@playwright/test'
import { FIXED_NOW, installMockAdmin } from './support/mockAdmin'

test('custom install reads the repository from Hugging Face and switches to a quantized build', async ({ page }) => {
  await page.clock.setFixedTime(new Date(FIXED_NOW))
  await installMockAdmin(page)
  await page.goto('/admin/catalog?node=node-1')
  await page.getByRole('button', { name: 'Install custom Hugging Face model' }).click()

  const repository = page.getByLabel('Custom Hugging Face repository')
  await repository.fill('meta-models/Muse-Glimmer-30B')

  const details = page.getByRole('region', { name: 'Repository details from Hugging Face' })
  await expect(details).toBeVisible()
  await expect(details).toContainText('30.5B')
  await expect(details).toContainText('BF16')
  await expect(details).toContainText('131,072 tokens')
  await expect(details).toContainText('Suggested engine: vLLM')

  // Builds are listed with the most downloaded first; formats no engine can serve are not selectable.
  const builds = details.getByRole('table', { name: 'Quantized builds from other publishers' })
  await expect(builds.getByRole('row')).toHaveCount(5)
  await expect(builds.getByRole('button', { name: 'Use mlx-community/Muse-Glimmer-30B-4bit' })).toBeDisabled()

  await builds.getByRole('button', { name: 'Use unsloth/Muse-Glimmer-30B-GGUF' }).click()
  await expect(repository).toHaveValue('unsloth/Muse-Glimmer-30B-GGUF')
  await expect(page.getByRole('combobox', { name: 'Install inference engine' })).toHaveValue('llama.cpp')
  await expect(page.getByLabel('Available GGUF quantizations')).toBeVisible()

  await page.getByRole('combobox', { name: 'Install inference engine' }).selectOption('vllm')
  await details.getByRole('button', { name: 'Use these settings' }).click()
  await expect(page.getByLabel('Runtime maximum context')).toHaveValue('32768')
})

test('inspector stays hidden until the repository id is complete', async ({ page }) => {
  await installMockAdmin(page)
  await page.goto('/admin/catalog?node=node-1')
  await page.getByRole('button', { name: 'Install custom Hugging Face model' }).click()
  await page.getByLabel('Custom Hugging Face repository').fill('meta-models/')
  await expect(page.getByRole('region', { name: 'Repository details from Hugging Face' })).toHaveCount(0)
})
