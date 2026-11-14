import { expect, test, type Page } from '@playwright/test'
import { installMockAdmin } from './support/mockAdmin'

type Engine = Record<string, unknown>
const policy = { autoUpdate: false, updateAtLocal: '03:00', timeZone: 'UTC', lastRunAtUtc: null, lastResult: null, lastMessage: null }
const engine = (id: string, label: string, patch: Engine = {}) => ({
  id, label, description: label + ' engine', source: 'pulled', image: id + ':latest', installed: false, version: null, revision: null, imageId: null,
  createdAtUtc: null, sizeBytes: null, runningContainers: 0, installations: 0, serves: ['OpenAi'], ...patch
})

async function installEngines(page: Page, state: { engines: { engine: Engine; policy: Record<string, unknown> }[]; jobs: unknown[] }) {
  await installMockAdmin(page)
  const calls: { method: string; path: string; body: unknown }[] = []
  await page.route('**/api/admin/engines/nodes/**', route => {
    const request = route.request()
    const path = new URL(request.url()).pathname.replace('/api/admin/engines/nodes/', '')
    const body = request.postData() ? request.postDataJSON() : null
    calls.push({ method: request.method(), path, body })
    const json = (value: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) })
    if (request.method() === 'GET') return json({ agentConfigured: true, agentAvailable: true, agentError: null, engines: state.engines, jobs: state.jobs })
    if (path.endsWith('/check-updates')) return json([
      { id: 'llama.cpp', status: 'update_available', detail: 'The registry has a newer image for this tag.' },
      { id: 'faster-whisper', status: 'not_applicable', detail: 'Built by this Agent.' },
      { id: 'vllm', status: 'not_installed', detail: null }])
    if (request.method() === 'PUT') {
      const entry = state.engines.find(item => path.includes('/' + item.engine.id + '/'))!
      entry.policy = { ...entry.policy, ...(body as object), lastResult: null }
      return json(entry.policy)
    }
    if (request.method() === 'DELETE') {
      const entry = state.engines.find(item => path.endsWith('/' + item.engine.id))!
      entry.engine = { ...entry.engine, installed: false, version: null, imageId: null }
      return json({ message: `${entry.engine.label} removed. Downloaded model files were kept.` })
    }
    if (path.endsWith('/jobs')) {
      const id = path.split('/')[1]
      const job = { id: '7a3b0f0e-0000-4000-8000-0000000000aa', engineId: id, action: (body as { action: string }).action, status: 'running',
        detail: 'Downloading ' + id, error: null, result: null, createdAtUtc: new Date().toISOString(), completedAtUtc: null }
      state.jobs = [job]
      // The next read finds it finished and the engine installed.
      setTimeout(() => {
        state.jobs = [{ ...job, status: 'completed', detail: 'Installed.', result: { changed: true, message: 'vLLM installed.' }, completedAtUtc: new Date().toISOString() }]
        state.engines = state.engines.map(item => item.engine.id === id
          ? { ...item, engine: { ...item.engine, installed: true, version: '0.11.0', imageId: 'sha256:aaaabbbbccccdddd', sizeBytes: 9 * 1024 ** 3, createdAtUtc: '2026-11-01T00:00:00Z' } } : item)
      }, 1500)
      return json(job, 202)
    }
    return json({})
  })
  return calls
}

const initial = () => ({
  engines: [
    { engine: engine('llama.cpp', 'llama.cpp', { installed: true, version: 'b4567', imageId: 'sha256:1234567890abcdef', createdAtUtc: '2026-10-20T00:00:00Z', sizeBytes: 4 * 1024 ** 3, runningContainers: 1, installations: 2, serves: ['OpenAi', 'Embedding'] }), policy: { ...policy } },
    { engine: engine('vllm', 'vLLM'), policy: { ...policy } },
    { engine: engine('faster-whisper', 'faster-whisper', { source: 'built', installed: true, version: '0.1', imageId: 'sha256:fedcba0987654321', runningContainers: 0, installations: 0, serves: ['AudioTranscription'] }), policy: { ...policy } }
  ],
  jobs: [] as unknown[]
})

test('each server lists its engines: installed or not, version, and what uses them', async ({ page }) => {
  await installEngines(page, initial())
  await page.goto('/admin/infrastructure?section=engines')

  const llama = page.getByRole('row', { name: 'llama.cpp' })
  await expect(llama).toContainText('Installed')
  await expect(llama).toContainText('b4567')
  await expect(llama).toContainText('1234567890ab')
  await expect(llama).toContainText('4.0 GiB')
  await expect(llama).toContainText('1 running')
  await expect(llama).toContainText('2 installed models')
  await expect(llama).toContainText('Text (chat)')
  await expect(llama).toContainText('Embeddings')
  await expect(page.getByRole('row', { name: 'vLLM' })).toContainText('Not installed')
  await expect(page.getByRole('row', { name: 'faster-whisper' })).toContainText('Built by the Agent')
  await expect(page.getByText('2 of 3 installed')).toBeVisible()
})

test('installing an engine runs in the background and ends with its version', async ({ page }) => {
  const calls = await installEngines(page, initial())
  await page.goto('/admin/infrastructure?section=engines')
  const vllm = page.getByRole('row', { name: 'vLLM' })
  await vllm.getByRole('button', { name: 'Install' }).click()

  await expect(vllm).toContainText('Installing')
  await expect(vllm).toContainText('Downloading vllm')
  expect(calls.find(call => call.method === 'POST' && call.path.endsWith('/vllm/jobs'))?.body).toEqual({ action: 'install' })

  await expect(vllm).toContainText('Installed', { timeout: 15_000 })
  await expect(vllm).toContainText('0.11.0')
  await expect(page.getByText('vLLM installed.')).toBeVisible()
})

test('an engine in use cannot be uninstalled; an idle one is removed after confirmation', async ({ page }) => {
  const calls = await installEngines(page, initial())
  page.on('dialog', dialog => void dialog.accept())
  await page.goto('/admin/infrastructure?section=engines')

  await expect(page.getByRole('row', { name: 'llama.cpp' }).getByRole('button', { name: 'Uninstall' })).toBeDisabled()
  const whisper = page.getByRole('row', { name: 'faster-whisper' })
  await whisper.getByRole('button', { name: 'Uninstall' }).click()

  await expect(whisper).toContainText('Not installed')
  await expect(page.getByText(/faster-whisper removed/)).toBeVisible()
  expect(calls.some(call => call.method === 'DELETE' && call.path.endsWith('/faster-whisper'))).toBe(true)
})

test('checking for updates marks which installed engines are behind', async ({ page }) => {
  await installEngines(page, initial())
  await page.goto('/admin/infrastructure?section=engines')
  await page.getByRole('button', { name: 'Check for updates' }).click()

  await expect(page.getByRole('row', { name: 'llama.cpp' })).toContainText('Update available')
  await expect(page.getByRole('row', { name: 'faster-whisper' })).toContainText('Rebuild to refresh')
})

test('automatic update is set per engine with a time and a time zone', async ({ page }) => {
  const calls = await installEngines(page, initial())
  await page.goto('/admin/infrastructure?section=engines')
  await page.getByRole('row', { name: 'llama.cpp' }).getByRole('button', { name: 'Auto-update…' }).click()

  const dialog = page.getByRole('dialog', { name: /Automatic update · llama.cpp/ })
  await expect(dialog.getByLabel('Update time')).toBeDisabled()
  await dialog.getByLabel('Update automatically').check()
  await dialog.getByLabel('Update time').fill('04:15')
  await dialog.getByLabel('Time zone').fill('Europe/Rome')
  await dialog.getByRole('button', { name: 'Save' }).click()

  await expect(page.getByRole('row', { name: 'llama.cpp' })).toContainText('Daily at 04:15')
  await expect(page.getByRole('row', { name: 'llama.cpp' })).toContainText('Europe/Rome')
  expect(calls.find(call => call.method === 'PUT')?.body).toEqual({ autoUpdate: true, updateAtLocal: '04:15', timeZone: 'Europe/Rome' })
})

test('an Agent too old for engines says so instead of showing an empty table', async ({ page }) => {
  await installMockAdmin(page)
  await page.route('**/api/admin/engines/nodes/**', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify({
    agentConfigured: true, agentAvailable: false, agentError: "This server's Agent is too old to manage engines. Upgrade the Agent first.", engines: [], jobs: [] }) }))
  await page.goto('/admin/infrastructure?section=engines')

  await expect(page.getByRole('alert')).toContainText('too old to manage engines')
  await expect(page.getByRole('table')).toHaveCount(0)
})
