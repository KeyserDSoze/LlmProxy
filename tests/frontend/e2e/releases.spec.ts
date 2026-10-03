import { expect, Route, test } from '@playwright/test'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

test('release notes page exposes the current product version and versioned patch-note history', async ({ page }) => {
  await page.route('**/api/admin/updates', route => json(route, {
    currentVersion: '0.0.6',
    agentAvailable: true,
    agent: { installedVersion: '0.0.6', activeJob: null, recentJobs: [] },
    releases: [
      {
        version: '0.0.7',
        title: 'LlmProxy 0.0.7',
        publishedAtUtc: '2026-10-03T16:00:00Z',
        releaseUrl: 'https://github.com/KeyserDSoze/LlmProxy/releases/tag/v0.0.7',
        isNewer: true,
        updateMode: 'standard',
        updateTitle: 'Standard immutable update',
        updateDescription: 'Uses the versioned LlmProxy installer.',
        requiresHostRestart: false,
        operatorCommand: 'sudo -E llmproxyctl update 0.0.7'
      }
    ]
  }))
  await page.route('**/api/admin/product', route => json(route, {
    product: 'LlmProxy',
    version: '0.2.0-preview.7',
    channel: 'preview',
    releasedOn: '2026-09-21',
    buildRevision: 'abcdef1234567890',
    builtAtUtc: null,
    releases: [
      {
        version: '0.2.0-preview.7',
        releasedOn: '2026-09-21',
        title: 'Aggregated Entra user request quotas',
        sections: {
          Added: ['Administrators can configure request-rate policies for an Entra user across all personal API keys.'],
          Changed: ['Request admission evaluates applicable user and credential policies together with AND semantics.'],
          Security: ['Monetary spend limits remain intentionally unsupported until an explicit pricing or chargeback model is configured.']
        }
      },
      {
        version: '0.2.0-preview.6',
        releasedOn: '2026-09-21',
        title: 'Entra-owned personal API keys',
        sections: {
          Added: ['Users with the LlmProxy.User Entra application role can create, list, rotate and revoke multiple personal inference API keys.'],
          Changed: ['The Entra authorization model now distinguishes LlmProxy.User self-service access from read-only operator access.'],
          Security: ['Credential ownership authorization uses immutable Entra tid and oid claims.']
        }
      },
      {
        version: '0.2.0-preview.5',
        releasedOn: '2026-09-16',
        title: 'Production environment acceptance evidence',
        sections: {
          Added: ['A production environment acceptance command validates the actual Linux host, VM-to-inference node connectivity and deployed OpenAI-compatible surfaces after installation.']
        }
      },
      {
        version: '0.2.0-preview.4',
        releasedOn: '2026-09-16',
        title: 'Consolidated Linux production deployment',
        sections: { Added: ['Linux production deployment now has one documented full-stack path.'] }
      },
      {
        version: '0.2.0-preview.3',
        releasedOn: '2026-09-16',
        title: 'Validated tagged releases',
        sections: { Added: ['Exact SemVer tag publication requires successful main CI.'] }
      },
      {
        version: '0.2.0-preview.2',
        releasedOn: '2026-09-16',
        title: 'Container SBOM and provenance',
        sections: { Added: ['GHCR publication emits an SPDX SBOM.'] }
      },
      {
        version: '0.2.0-preview.1',
        releasedOn: '2026-09-16',
        title: 'Historical usage rollups',
        sections: { Added: ['Daily PostgreSQL usage rollups preserve accounting.'] }
      },
      {
        version: '0.1.0-preview.1',
        releasedOn: '2026-09-16',
        title: 'Initial versioned preview baseline',
        sections: { Added: ['Safe node maintenance flow.'] }
      }
    ]
  }))

  await page.goto('/admin/releases')
  await expect(page.getByRole('heading', { name: 'Release notes' })).toBeVisible()
  await expect(page.getByText('0.2.0-preview.7', { exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Aggregated Entra user request quotas/ })).toBeVisible()
  await expect(page.getByText('Administrators can configure request-rate policies for an Entra user across all personal API keys.')).toBeVisible()
  await expect(page.getByRole('heading', { name: /Entra-owned personal API keys/ })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Production environment acceptance evidence/ })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Consolidated Linux production deployment/ })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Validated tagged releases/ })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Container SBOM and provenance/ })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Historical usage rollups/ })).toBeVisible()
  await expect(page.getByRole('heading', { name: /Initial versioned preview baseline/ })).toBeVisible()
  await expect(page.getByText('abcdef123456')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Host updates' })).toBeVisible()
  await expect(page.getByText('v0.0.7', { exact: true })).toBeVisible()
  await expect(page.getByText('Standard update')).toBeVisible()
  await expect(page.getByText('sudo -E llmproxyctl update 0.0.7')).toBeVisible()
})
