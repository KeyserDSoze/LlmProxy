import { expect, Route, test } from '@playwright/test'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

test('release notes page exposes the current product version and versioned patch-note history', async ({ page }) => {
  await page.route('**/api/admin/product', route => json(route, {
    product: 'LlmProxy',
    version: '0.2.0-preview.5',
    channel: 'preview',
    releasedOn: '2026-09-16',
    buildRevision: 'abcdef1234567890',
    builtAtUtc: null,
    releases: [
      {
        version: '0.2.0-preview.5',
        releasedOn: '2026-09-16',
        title: 'Production environment acceptance evidence',
        sections: {
          Added: ['A production environment acceptance command validates the actual Linux host, VM-to-DGX connectivity and deployed OpenAI-compatible surfaces after installation.'],
          Changed: ['Target-host acceptance is now an executable, repeatable evidence step rather than only a manual checklist.'],
          Security: ['Acceptance evidence excludes prompts, request and response bodies, generated model output and API secrets.']
        }
      },
      {
        version: '0.2.0-preview.4',
        releasedOn: '2026-09-16',
        title: 'Consolidated Linux production deployment',
        sections: {
          Added: ['Linux production deployment now has one documented full-stack path with PostgreSQL, Redis and bundled observability.'],
          Changed: ['The production deploy script and GitHub Actions deploy workflow now use the Redis-enabled full stack instead of the legacy minimal Compose overlay.'],
          Security: ['Grafana can bind to loopback independently from the gateway, and public-tunnel guidance requires Entra protection before exposing administrative surfaces.']
        }
      },
      {
        version: '0.2.0-preview.3',
        releasedOn: '2026-09-16',
        title: 'Validated tagged releases',
        sections: {
          Added: ['Exact SemVer tag publication requires evidence that the same source SHA already completed CI successfully from a push to main.'],
          Security: ['Exact versioned container releases are tied to previously validated main source rather than trusting tag creation alone.']
        }
      },
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
  await expect(page.getByText('0.2.0-preview.5', { exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Production environment acceptance evidence/ })).toBeVisible()
  await expect(page.getByText('A production environment acceptance command validates the actual Linux host, VM-to-DGX connectivity and deployed OpenAI-compatible surfaces after installation.')).toBeVisible()
  await expect(page.getByRole('heading', { name: /Consolidated Linux production deployment/ })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Validated tagged releases/ })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Container SBOM and provenance/ })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Historical usage rollups/ })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Initial versioned preview baseline/ })).toBeVisible()
  await expect(page.getByText('abcdef123456')).toBeVisible()
})
