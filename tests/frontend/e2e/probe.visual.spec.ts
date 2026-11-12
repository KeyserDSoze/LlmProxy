import { test } from '@playwright/test'
import { FIXED_NOW, installMockAdmin } from './support/mockAdmin'

// Temporary screenshot helper for design iteration (deleted before commit).
const shots: Array<[string, string, number, 'light' | 'dark', boolean]> = [
  ['credentials4', '/admin/credentials', 1440, 'light', false],
  ['governance4', '/admin/governance', 1440, 'light', false],
  ['servers4', '/admin/infrastructure', 1440, 'light', false],
  ['phone-credentials4', '/admin/credentials', 390, 'light', false],
  ['userportal4', '/admin/me', 1024, 'light', true]
]
for (const [name, url, width, theme, full] of shots) {
  test(`shot ${name}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 })
    await page.addInitScript(value => window.localStorage.setItem('llmproxy.admin.preferences.v1', JSON.stringify({ locale: 'en', theme: value })), theme)
    await page.clock.setFixedTime(new Date(FIXED_NOW))
    await installMockAdmin(page)
    await page.goto(url)
    await page.waitForTimeout(1800)
    await page.screenshot({ path: `../../.tmp-shots/${name}.png`, fullPage: full })
  })
}
