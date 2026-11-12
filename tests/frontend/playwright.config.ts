import { defineConfig, devices } from '@playwright/test'

// GitHub's ubuntu runner ships Google Chrome; the Playwright container ships
// the bundled Chromium only. PLAYWRIGHT_USE_SYSTEM_CHROME=0 opts out in CI.
const useSystemChrome = process.env.PLAYWRIGHT_USE_SYSTEM_CHROME === '1'
  || (Boolean(process.env.CI) && process.env.PLAYWRIGHT_USE_SYSTEM_CHROME !== '0')
const chromeChannel = useSystemChrome ? { channel: 'chrome' as const } : {}

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  reporter: [
    ['list'],
    ['html', { outputFolder: 'playwright-report', open: 'never' }]
  ],
  // Baselines are rendered only inside mcr.microsoft.com/playwright (see
  // tests/frontend/scripts/visual-docker.mjs), so they carry no OS suffix.
  snapshotPathTemplate: '{testDir}/__screenshots__/{testFileName}/{arg}{ext}',
  expect: {
    // Absolute budget: a ratio would let a changed word or row pass on large pages.
    toHaveScreenshot: { maxDiffPixels: 100, animations: 'disabled', caret: 'hide', scale: 'css' }
  },
  use: {
    baseURL: 'http://127.0.0.1:4173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure'
  },
  webServer: {
    command: 'npm --prefix ../../src/LlmProxy.Admin run dev -- --host 127.0.0.1 --port 4173',
    url: 'http://127.0.0.1:4173',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000
  },
  projects: [
    {
      name: 'chromium',
      testIgnore: /visual\.spec\.ts$/,
      use: { ...devices['Desktop Chrome'], ...chromeChannel }
    },
    {
      name: 'visual',
      testMatch: /visual\.spec\.ts$/,
      use: {
        ...devices['Desktop Chrome'],
        ...chromeChannel,
        viewport: { width: 1440, height: 900 },
        locale: 'en-US',
        timezoneId: 'UTC'
      }
    }
  ]
})
