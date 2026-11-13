import { expect, test } from '@playwright/test'
import { installMockAdmin } from './support/mockAdmin'

const release = {
  version: '0.2.67', title: 'Next', publishedAtUtc: '2026-11-13T00:00:00Z', releaseUrl: '', isNewer: true,
  updateMode: 'standard', updateTitle: '', updateDescription: '', requiresHostRestart: false, operatorCommand: ''
}

test('navigation scrolls on its own when it is taller than the window', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 560 })
  await installMockAdmin(page)
  await page.goto('/admin/')
  await expect(page.getByRole('heading', { name: 'Gateway dashboard' })).toBeVisible()

  const before = await page.locator('.sidebar').evaluate(el => ({
    client: el.clientHeight, scroll: el.scrollHeight, bottom: el.getBoundingClientRect().bottom, viewport: window.innerHeight
  }))
  expect(before.scroll).toBeGreaterThan(before.client)
  expect(before.bottom).toBeLessThanOrEqual(before.viewport + 1)

  await page.locator('.sidebar').evaluate(el => { el.scrollTop = el.scrollHeight })
  expect(await page.evaluate(() => window.scrollY)).toBe(0)
  await expect(page.getByRole('button', { name: 'Release notes' })).toBeInViewport()
})

test('a newer release is announced everywhere and can be started from the banner', async ({ page }) => {
  await installMockAdmin(page)
  let scheduled: unknown = null
  await page.route('**/api/admin/updates', async route => {
    if (route.request().method() === 'POST') {
      scheduled = route.request().postDataJSON()
      return route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify({ id: 'job-1' }) })
    }
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify({
      currentVersion: '0.2.64', agentAvailable: true,
      policy: { mode: 'manual', timeZoneId: 'UTC', localHour: 2, localMinute: 0, dayOfWeek: 0, dayOfMonth: 1, updatedAtUtc: '2026-11-13T00:00:00Z' },
      agent: { installedVersion: '0.2.64', activeJob: null, recentJobs: [] },
      releases: [release]
    }) })
  })
  page.on('dialog', dialog => void dialog.accept())

  await page.goto('/admin/')
  const banner = page.getByTestId('update-banner')
  await expect(banner).toContainText('New version available: v0.2.67')
  await expect(page.getByRole('button', { name: /Release notes/ }).locator('.navBadge--info')).toHaveText(/1/)

  await banner.getByRole('button', { name: 'Update now' }).click()
  await expect.poll(() => scheduled).toEqual({ version: '0.2.67', force: true })
})
