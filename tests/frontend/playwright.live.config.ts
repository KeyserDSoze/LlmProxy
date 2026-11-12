import { defineConfig, devices } from '@playwright/test'

// Admin UI against a real running LlmProxy (no API mocks). The stack is started by
// tests/backend/integration/admin_ui_live_smoke.sh; LLMPROXY_LIVE_BASE_URL overrides it.
const useSystemChrome = process.env.PLAYWRIGHT_USE_SYSTEM_CHROME === '1'
  || (Boolean(process.env.CI) && process.env.PLAYWRIGHT_USE_SYSTEM_CHROME !== '0')

export default defineConfig({
  testDir: './live',
  fullyParallel: false,
  workers: 2,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  reporter: [
    ['list'],
    ['html', { outputFolder: 'playwright-report/live', open: 'never' }]
  ],
  use: {
    baseURL: process.env.LLMPROXY_LIVE_BASE_URL ?? 'http://127.0.0.1:8080',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure'
  },
  projects: [
    {
      name: 'live',
      use: { ...devices['Desktop Chrome'], ...(useSystemChrome ? { channel: 'chrome' as const } : {}), viewport: { width: 1440, height: 900 } }
    }
  ]
})
