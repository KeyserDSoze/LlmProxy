import { expect, Route, test } from '@playwright/test'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

test('release notes page exposes the current product version and versioned patch-note history', async ({ page }) => {
  await page.route('**/api/admin/product', route => json(route, {
    product: 'LlmProxy',
    version: '0.2.0-preview.2',
    channel: 'preview',
    releasedOn: '2026-09-16',
    buildRevision: 'abcdef1234567890',
    builtAtUtc: null,
    releases: [
      {
        version: '0.2.0-preview.2',
        releasedOn: '2026-09-16',
        title: 'Container SBOM and provenance',
        sections: {
          Added: ['GHCR container publication emits an SPDX software bill of materials as an OCI attestation.'],
          Security: ['Release consumers can inspect image dependency inventory and build provenance without relying only on mutable tags.']
        }
      },
      {
        version: '0.2.0-preview.1',
        releasedOn: '2026-09-16',
        title: 'Historical usage rollups',
        sections: {
          Added: ['Daily PostgreSQL usage rollups preserve accounting after granular metrics age out.'],
          Changed: ['Usage reporting windows are defined as UTC calendar days.']
        }
      },
      {
        version: '0.1.0-preview.1',
        releasedOn: '2026-09-16',
        title: 'Initial versioned preview baseline',
        sections: {
          Added: ['Safe node maintenance flow with distributed admission pre-block.'],
          Fixed: ['Closed the cross-replica drain race.']
        }
      }
    ]
  }))

  await page.goto('/admin/releases')
  await expect(page.getByRole('heading', { name: 'Release notes' })).toBeVisible()
  await expect(page.getByText('0.2.0-preview.2', { exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Container SBOM and provenance/ })).toBeVisible()
  await expect(page.getByText('GHCR container publication emits an SPDX software bill of materials as an OCI attestation.')).toBeVisible()
  await expect(page.getByRole('heading', { name: /Historical usage rollups/ })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Initial versioned preview baseline/ })).toBeVisible()
  await expect(page.getByText('abcdef123456')).toBeVisible()
})
