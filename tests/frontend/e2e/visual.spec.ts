import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { FIXED_NOW, installMockAdmin } from './support/mockAdmin'

/**
 * Visual and layout regression suite. Every screen is rendered against the
 * deterministic mock in ./support/mockAdmin with a frozen clock and checked for:
 * - uncaught page errors and console errors;
 * - horizontal page overflow (desktop and phone width);
 * - serious/critical axe-core accessibility violations;
 * - pixel regressions (only when LLMPROXY_VISUAL_COMPARE=1, i.e. inside the
 *   pinned Playwright container: tests/frontend/scripts/visual-docker.mjs).
 */
const compareScreenshots = process.env.LLMPROXY_VISUAL_COMPARE === '1'

type Screen = { name: string; url: string; ready: (page: Page) => Promise<unknown> }

const text = (value: string | RegExp) => (page: Page) => expect(page.getByText(value).first()).toBeVisible()

const screens: Screen[] = [
  { name: 'dashboard', url: '/admin/dashboard', ready: text('Connection refused (10.0.0.22:8000)') },
  { name: 'models-installed', url: '/admin/models?node=node-1', ready: page => expect(page.locator('.installedModelCard')).toHaveCount(3) },
  { name: 'models-discover', url: '/admin/catalog?node=node-1', ready: text('Qwen3 8B') },
  { name: 'models-activity', url: '/admin/models?node=node-1&panel=activity', ready: text(/LFM2\.5-24B-A2B-Q4_K_M\.gguf/) },
  { name: 'models-publishing', url: '/admin/models?node=node-1&panel=advanced', ready: text('agic-code-fast') },
  { name: 'servers-fleet', url: '/admin/infrastructure', ready: text('spark-02') },
  { name: 'servers-hardware', url: '/admin/infrastructure?section=hardware&node=node-1', ready: text('NVIDIA GB10') },
  { name: 'servers-capacity', url: '/admin/infrastructure?section=capacity', ready: text('spark-01') },
  { name: 'routing', url: '/admin/routing', ready: text('Smart-routing tuning') },
  { name: 'system-one', url: '/admin/system-one', ready: page => expect(page.getByText('systemone-laya').filter({ visible: true }).first()).toBeVisible() },
  { name: 'playground', url: '/admin/playground', ready: text('agic-code-fast') },
  { name: 'credentials', url: '/admin/credentials', ready: text('GitHub Copilot') },
  { name: 'users', url: '/admin/users', ready: text('Alessandro Rapiti') },
  { name: 'governance', url: '/admin/governance', ready: text('Development CRM') },
  { name: 'metrics', url: '/admin/metrics', ready: text('agic-code-fast') },
  { name: 'request-audit', url: '/admin/logs', ready: text('req-7f3a') },
  { name: 'admin-audit', url: '/admin/audit', ready: text('model.start.queued') },
  { name: 'help', url: '/admin/help', ready: page => expect(page.locator('main h1')).toBeVisible() },
  { name: 'settings', url: '/admin/settings', ready: page => expect(page.locator('main h1')).toBeVisible() },
  { name: 'releases', url: '/admin/releases', ready: text('Current version') },
  { name: 'user-portal', url: '/admin/me', ready: text('lp_usr_3e1f') }
]

const darkScreens = new Set(['dashboard', 'models-installed', 'models-discover', 'servers-fleet', 'metrics'])
const phoneScreens = new Set(['dashboard', 'models-installed', 'servers-fleet'])

async function open(page: Page, screen: Screen, theme: 'light' | 'dark') {
  const problems: string[] = []
  page.on('pageerror', error => problems.push('pageerror: ' + error.message))
  page.on('console', message => { if (message.type() === 'error') problems.push('console: ' + message.text()) })
  page.on('response', response => {
    if (response.url().includes('/api/') && response.status() >= 400)
      problems.push(`http ${response.status()}: ${response.request().method()} ${new URL(response.url()).pathname}`)
  })
  await page.addInitScript(value => {
    window.localStorage.setItem('llmproxy.admin.preferences.v1', JSON.stringify({ locale: 'en', theme: value }))
  }, theme)
  await page.clock.setFixedTime(new Date(FIXED_NOW))
  await installMockAdmin(page)
  await page.goto(screen.url)
  await screen.ready(page)
  await expect(page.locator('.loading')).toHaveCount(0)
  // The activity dock appears once its own poll has answered; without waiting, a slow runner photographs the page without it.
  if (!screen.url.startsWith('/admin/me')) await expect(page.locator('.activityDock .activityButton')).toHaveText('1 running')
  await page.evaluate(() => document.fonts.ready)
  return problems
}

async function expectNoHorizontalOverflow(page: Page) {
  const overflow = await page.evaluate(() => {
    const root = document.documentElement
    const describe = (element: Element) =>
      `${element.tagName.toLowerCase()}${element.classList.length ? '.' + [...element.classList].join('.') : ''}`
    const scrollers = (style: CSSStyleDeclaration) => ['auto', 'scroll', 'hidden', 'clip'].includes(style.overflowX)
    const offenders = [...document.querySelectorAll<HTMLElement>('body *')]
      .filter(element => {
        const box = element.getBoundingClientRect()
        if (box.width === 0 || box.right <= root.clientWidth + 1) return false
        // Content inside an intentional horizontal scroller is checked below.
        for (let parent = element.parentElement; parent; parent = parent.parentElement)
          if (parent !== document.body && parent !== root && scrollers(getComputedStyle(parent))) return false
        return true
      })
      .slice(0, 5)
      .map(element => `${describe(element)} right=${Math.round(element.getBoundingClientRect().right)}`)
    // Wide data tables and code may scroll inside their container; cards, forms
    // and toolbars must reflow instead of producing a hidden sideways scrollbar.
    const innerScrollbars = [...document.querySelectorAll<HTMLElement>('body *')]
      .filter(element => {
        const style = getComputedStyle(element)
        if (!['auto', 'scroll'].includes(style.overflowX) || element.scrollWidth <= element.clientWidth + 1) return false
        if (element.closest('table, pre, .tableScroll, .tabs, .payload, .modalBody')) return false
        return ![...element.querySelectorAll('table, pre, code, .tabs')]
          .some(child => (child as HTMLElement).scrollWidth > element.clientWidth || child.getBoundingClientRect().right > element.getBoundingClientRect().right + 1)
      })
      .slice(0, 5)
      .map(element => `${describe(element)} scroll=${element.scrollWidth} client=${element.clientWidth}`)
    return { scroll: root.scrollWidth - root.clientWidth, offenders, innerScrollbars }
  })
  expect(overflow.offenders, 'elements overflowing the viewport').toEqual([])
  expect(overflow.innerScrollbars, 'unexpected horizontal scrollbars around non-tabular content').toEqual([])
  expect(overflow.scroll, 'page-level horizontal scrolling').toBeLessThanOrEqual(1)
}

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
    .analyze()
  const blocking = results.violations
    .filter(item => item.impact === 'serious' || item.impact === 'critical')
    .map(item => `${item.id} (${item.impact}): ${item.nodes.slice(0, 3).map(node => node.target.join(' ')).join(' | ')}`)
  expect(blocking, 'serious/critical accessibility violations').toEqual([])
}

for (const screen of screens) {
  test.describe(screen.name, () => {
    test(`desktop light`, async ({ page }, testInfo) => {
      const problems = await open(page, screen, 'light')
      await expectNoHorizontalOverflow(page)
      await expectAccessible(page)
      if (compareScreenshots) await expect(page).toHaveScreenshot(`${screen.name}-light.png`)
      else await testInfo.attach(`${screen.name}-light`, { body: await page.screenshot(), contentType: 'image/png' })
      expect(problems).toEqual([])
    })

    if (darkScreens.has(screen.name)) test(`desktop dark`, async ({ page }, testInfo) => {
      const problems = await open(page, screen, 'dark')
      await expectNoHorizontalOverflow(page)
      await expectAccessible(page)
      if (compareScreenshots) await expect(page).toHaveScreenshot(`${screen.name}-dark.png`)
      else await testInfo.attach(`${screen.name}-dark`, { body: await page.screenshot(), contentType: 'image/png' })
      expect(problems).toEqual([])
    })

    test(`phone layout`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width: 390, height: 844 })
      const problems = await open(page, screen, 'light')
      await expectNoHorizontalOverflow(page)
      if (phoneScreens.has(screen.name)) {
        if (compareScreenshots) await expect(page).toHaveScreenshot(`${screen.name}-phone.png`)
        else await testInfo.attach(`${screen.name}-phone`, { body: await page.screenshot(), contentType: 'image/png' })
      }
      expect(problems).toEqual([])
    })
  })
}

test('phone navigation drawer opens with every workspace grouped', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const problems = await open(page, screens[0], 'light')
  await page.getByRole('button', { name: 'Open navigation' }).click()
  const navigation = page.getByRole('navigation', { name: 'Admin navigation' })
  await expect(navigation).toBeVisible()
  for (const label of ['Models', 'Routing', 'Servers & hardware', 'Settings'])
    await expect(navigation.getByRole('button', { name: label, exact: true })).toBeVisible()
  await expectNoHorizontalOverflow(page)
  if (compareScreenshots) await expect(page).toHaveScreenshot('navigation-phone.png')
  expect(problems).toEqual([])
})

test('custom install shows parameters, formats and quantized builds of the typed repository', async ({ page }, testInfo) => {
  const problems = await open(page, screens[2], 'light')
  await page.getByRole('button', { name: 'Install custom Hugging Face model' }).click()
  await page.getByLabel('Custom Hugging Face repository').fill('meta-models/Muse-Glimmer-30B')
  const details = page.getByRole('region', { name: 'Repository details from Hugging Face' })
  await expect(details.getByRole('table', { name: 'Quantized builds from other publishers' })).toBeVisible()
  await expectAccessible(page)
  if (compareScreenshots) await expect(details).toHaveScreenshot('hf-repository-inspector.png')
  else await testInfo.attach('hf-repository-inspector', { body: await details.screenshot(), contentType: 'image/png' })
  const properties = page.getByRole('group', { name: 'What clients are told about this model' })
  await expect(properties.getByRole('checkbox', { name: /Tool calling/ })).toBeChecked()
  await expect(properties.getByLabel('Maximum output (tokens)')).toHaveValue('8192')
  await expectAccessible(page)
  if (compareScreenshots) await expect(properties).toHaveScreenshot('published-properties-editor.png')
  else await testInfo.attach('published-properties-editor', { body: await properties.screenshot(), contentType: 'image/png' })
  expect(problems).toEqual([])
})

test('gated Hugging Face install failure offers the guided token form', async ({ page }, testInfo) => {
  const problems = await open(page, screens[3], 'light')
  const card = page.locator('.modelJobCard', { hasText: "requires authentication for 'meta/muse-glimmer'" })
  await expect(card.getByRole('form', { name: 'Hugging Face access needed' })).toBeVisible()
  await page.addStyleTag({ content: '.tabs{position:static!important}' })
  if (compareScreenshots) await expect(card).toHaveScreenshot('gated-hugging-face-card.png')
  else await testInfo.attach('gated-hugging-face-card', { body: await card.screenshot(), contentType: 'image/png' })
  expect(problems).toEqual([])
})

test('failed llama.cpp start explains the root cause and next step on the model card', async ({ page }, testInfo) => {
  const problems = await open(page, screens[1], 'light')
  const card = page.locator('.installedModelCard', { hasText: 'gemma-vision-gguf' })
  const diagnosis = card.getByRole('alert')
  await expect(diagnosis).toContainText('exactly one out metadata, path_model, and file must be defined')
  await expect(diagnosis).toContainText('Start again: the Agent now downloads and pins the exact GGUF first')
  await expect(diagnosis).toContainText('/var/lib/llmproxy-node-agent/startup-logs/custom-01ea8a84.log')
  await card.locator('summary', { hasText: 'Last log lines (4)' }).click()
  await expect(diagnosis).toContainText("failed to load model ''")
  // The sticky tab bar would otherwise overlap the scrolled card in the capture.
  await page.addStyleTag({ content: '.tabs{position:static!important}' })
  if (compareScreenshots) await expect(card).toHaveScreenshot('failed-gguf-card.png')
  else await testInfo.attach('failed-gguf-card', { body: await card.screenshot(), contentType: 'image/png' })
  expect(problems).toEqual([])
})
