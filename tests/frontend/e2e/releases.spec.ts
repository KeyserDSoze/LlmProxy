import { expect, Route, test } from '@playwright/test'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

test('release notes page exposes the current product version and patch notes', async ({ page }) => {
  await page.route('**/api/admin/product', route => json(route, {
    product: 'LlmProxy',
    version: '0.1.0-preview.1',
    channel: 'preview',
    releasedOn: '2026-09-16',
    buildRevision: 'abcdef1234567890',
    builtAtUtc: null,
    releases: [{
      version: '0.1.0-preview.1',
      releasedOn: '2026-09-16',
      title: 'Initial versioned preview baseline',
      sections: {
        Added: ['Safe node maintenance flow with distributed admission pre-block.'],
        Changed: ['Release management now follows SemVer while the product remains pre-1.0.'],
        Fixed: ['Closed the cross-replica drain race.'],
        Security: ['Raw prompts and API secrets remain excluded from persistent telemetry.']
      }
    }]
  }))

  await page.goto('/admin/releases')
  await expect(page.getByRole('heading', { name: 'Release notes' })).toBeVisible()
  await expect(page.getByText('0.1.0-preview.1', { exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Initial versioned preview baseline/ })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Added' })).toBeVisible()
  await expect(page.getByText('Safe node maintenance flow with distributed admission pre-block.')).toBeVisible()
  await expect(page.getByText('abcdef123456')).toBeVisible()
})
