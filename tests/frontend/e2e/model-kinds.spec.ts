import { expect, test } from '@playwright/test'
import { installMockAdmin, models, node1Overview } from './support/mockAdmin'

const embeddingEntry = {
  model: {
    id: 'qwen3-embedding-0.6b-gguf', displayName: 'Qwen3 Embedding 0.6B (llama.cpp)', providerModelName: 'Qwen/Qwen3-Embedding-0.6B-GGUF:Q8_0',
    family: 'Qwen3 Embedding', runtime: 'llama.cpp', surface: 'Embedding', license: 'Apache-2.0', sourceUrl: 'https://huggingface.co/Qwen/Qwen3-Embedding-0.6B-GGUF',
    parameterBillions: 0.6, contextTokens: 32768, precision: 'Q8_0', minimumGpuMemoryGiB: 2, recommendedGpuMemoryGiB: 4, minimumSystemMemoryGiB: 4,
    recommendedSystemMemoryGiB: 8, diskGiB: 1, minimumGpuCount: 0, recommendedGpuCount: 1, supportsStreaming: false, supportsTools: false,
    tags: ['embeddings'], notes: 'Multilingual text embeddings.', launchArguments: ['--embedding']
  },
  compatibility: { status: 'fits', summary: 'Fits easily.', reasons: [], suggestedTensorParallelSize: 1 },
  capabilities: {
    providerModelName: 'Qwen/Qwen3-Embedding-0.6B-GGUF:Q8_0', runtime: 'llama.cpp', source: 'Curated',
    chat: { model: 'unsupported', serving: 'unsupported', explanation: 'Embeddings only' }, toolCalling: { model: 'unsupported', serving: 'unsupported', explanation: '' },
    streaming: { model: 'unsupported', serving: 'unsupported', explanation: '' }, vision: { model: 'unsupported', serving: 'unsupported', explanation: '' },
    thinking: { model: 'unsupported', serving: 'unsupported', explanation: '' }, notes: 'Text embeddings only: POST /v1/embeddings'
  }
}

async function installEmbeddingFixtures(page: import('@playwright/test').Page) {
  await installMockAdmin(page)
  const embeddingModel = { id: 'model-embed', publicName: 'bge-embed', providerModelName: 'Qwen/Qwen3-Embedding-0.6B-GGUF:Q8_0', supportsStreaming: false, supportsTools: false, surface: 'Embedding', enabled: true }
  await page.route('**/api/admin/models', route => route.request().method() === 'GET'
    ? route.fulfill({ contentType: 'application/json', body: JSON.stringify([...models, embeddingModel]) }) : route.fallback())
  await page.route('**/api/admin/model-management/nodes/node-1/overview', async route => {
    const body = node1Overview() as Record<string, unknown[]>
    body.catalog = [...body.catalog, embeddingEntry]
    body.installations = [...body.installations, {
      id: 'deployment-embed', modelId: 'model-embed', logicalModel: 'bge-embed', providerModelName: embeddingModel.providerModelName, catalogModelId: embeddingEntry.model.id,
      managedInstallationId: 'qwen3-embedding-1a2b3c', runtime: 'llama.cpp', surface: 'Embedding', enabled: true, agentStatus: 'running',
      agentState: { installationId: 'qwen3-embedding-1a2b3c', status: 'running', runtime: 'llama.cpp', port: 18010, maxNumSeqs: 4, maxModelLen: 8192 },
      properties: { streaming: false, tools: false, vision: false, reasoning: false, contextWindow: 8192, maxOutputTokens: null },
      capabilities: embeddingEntry.capabilities
    }]
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) })
  })
}

test('the embeddings workspace lists its own models, endpoint and example call', async ({ page }) => {
  await installEmbeddingFixtures(page)
  await page.goto('/admin/models?kind=Embedding&node=node-1&panel=installed')

  await expect(page.getByRole('heading', { name: 'Embeddings', exact: true, level: 1 })).toBeVisible()
  const card = page.getByRole('article', { name: 'bge-embed' })
  await expect(card.getByText('POST /v1/embeddings')).toBeVisible()
  // Nothing chat-specific on an embedding model: no tool/vision/reasoning chips, only the context it accepts.
  await expect(card.getByText('Tool calling')).toHaveCount(0)
  await expect(card.getByText('Context 8,192')).toBeVisible()
  // Text models stay out of this workspace and the other way round.
  await expect(page.getByRole('article', { name: 'agic-code-fast' })).toHaveCount(0)
  await page.goto('/admin/models?kind=OpenAi&node=node-1&panel=installed')
  await expect(page.getByRole('article', { name: 'bge-embed' })).toHaveCount(0)
  await expect(page.getByRole('article', { name: 'agic-code-fast' })).toBeVisible()
})

test('a curated embedding model installs without chat settings and is published with its context only', async ({ page }) => {
  await installEmbeddingFixtures(page)
  let queued: Record<string, unknown> | null = null
  await page.route('**/api/admin/model-management/nodes/*/models/qwen3-embedding-0.6b-gguf/install-jobs', route => {
    queued = route.request().postDataJSON()
    return route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify({
      id: '22222222-2222-4222-8222-222222222222', request: { catalogModelId: 'qwen3-embedding-0.6b-gguf', providerModelName: embeddingEntry.model.providerModelName, runtime: 'llama.cpp' },
      status: 'queued', stage: 'queued', detail: 'Queued', createdAtUtc: '2026-11-12T10:00:00Z', surface: 'Embedding' }) })
  })

  await page.goto('/admin/catalog?kind=Embedding&node=node-1')
  await expect(page.getByText('Qwen3 Embedding 0.6B (llama.cpp)')).toBeVisible()
  await page.getByRole('row').filter({ hasText: 'Qwen3 Embedding 0.6B' }).getByRole('button', { name: 'Install' }).click()
  const dialog = page.getByRole('dialog', { name: 'Install inference profile' })
  await expect(dialog.getByRole('group', { name: 'What clients are told about this embedding model' })).toBeVisible()
  await expect(dialog.getByRole('checkbox', { name: /Tool calling/ })).toHaveCount(0)
  await dialog.getByRole('button', { name: /Download|Schedule/ }).click()

  await expect.poll(() => queued).not.toBeNull()
  expect(queued!.declared).toMatchObject({ streaming: false, tools: false, vision: false, reasoning: false, maxOutputTokens: null, contextWindow: 8192 })
  expect((queued!.declared as Record<string, unknown>).surface).toBeUndefined()
})

test('the jobs of another kind of model do not clutter this workspace', async ({ page }) => {
  await installEmbeddingFixtures(page)
  await page.route('**/api/admin/model-management/nodes/*/install-jobs', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify([
    { id: 'job-chat', request: { catalogModelId: 'qwen3-8b', providerModelName: 'Qwen/Qwen3-8B', runtime: 'vllm' }, status: 'running', stage: 'model-weights', percent: 30, detail: 'Downloading', createdAtUtc: '2026-11-12T09:00:00Z', surface: 'OpenAi' },
    { id: 'job-embed', request: { catalogModelId: 'qwen3-embedding-0.6b-gguf', providerModelName: 'Qwen/Qwen3-Embedding-0.6B-GGUF:Q8_0', runtime: 'llama.cpp' }, status: 'running', stage: 'model-weights', percent: 60, detail: 'Downloading', createdAtUtc: '2026-11-12T09:30:00Z', surface: 'Embedding' }
  ]) }))

  await page.goto('/admin/models?kind=Embedding&node=node-1&panel=activity')
  await expect(page.getByRole('row').filter({ hasText: 'Qwen3-Embedding-0.6B' })).toHaveCount(1)
  await expect(page.getByRole('row').filter({ hasText: 'Qwen/Qwen3-8B' })).toHaveCount(0)
  await page.goto('/admin/models?kind=OpenAi&node=node-1&panel=activity')
  await expect(page.getByRole('row').filter({ hasText: 'Qwen/Qwen3-8B' })).toHaveCount(1)
  await expect(page.getByRole('row').filter({ hasText: 'Qwen3-Embedding-0.6B' })).toHaveCount(0)
})
