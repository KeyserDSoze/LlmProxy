import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../src/LlmProxy.Admin/src/api'

describe('admin api client', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn())
  })

  it('requests metrics with the requested take value and same-origin credentials', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response('[]', {
      status: 200,
      headers: { 'Content-Type': 'application/json' }
    }))

    await api.metrics(25)

    expect(fetch).toHaveBeenCalledWith('/api/admin/metrics?take=25', expect.objectContaining({
      credentials: 'same-origin'
    }))
  })

  it('requests the inference summary for the requested time window', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response('{}', {
      status: 200,
      headers: { 'Content-Type': 'application/json' }
    }))

    await api.metricsSummary(12)

    expect(fetch).toHaveBeenCalledWith('/api/admin/metrics/summary?hours=12', expect.objectContaining({
      credentials: 'same-origin'
    }))
  })

  it('requests both in-memory routing telemetry feeds', async () => {
    vi.mocked(fetch).mockImplementation(async () => new Response('[]', {
      status: 200,
      headers: { 'Content-Type': 'application/json' }
    }))

    await api.routingPerformance()
    await api.routingRuntime()

    expect(fetch).toHaveBeenNthCalledWith(1, '/api/admin/routing/performance', expect.objectContaining({ credentials: 'same-origin' }))
    expect(fetch).toHaveBeenNthCalledWith(2, '/api/admin/routing/runtime', expect.objectContaining({ credentials: 'same-origin' }))
  })

  it('requests capacity state and keeps profile save separate from apply', async () => {
    vi.mocked(fetch).mockImplementation(async () => new Response('{}', {
      status: 200,
      headers: { 'Content-Type': 'application/json' }
    }))

    await api.capacity()
    await api.updateCapacityProfile('deployment-1', {
      recommendedMaxConcurrency: 4,
      p95TtftMilliseconds: 420,
      p95DurationMilliseconds: 4800,
      sustainableOutputTokensPerSecond: 92,
      benchmarkSource: 'benchmark-results/run.json',
      measuredAtUtc: '2026-09-13T07:00:00Z'
    })
    await api.applyCapacityProfile('deployment-1')

    expect(fetch).toHaveBeenNthCalledWith(1, '/api/admin/capacity', expect.objectContaining({ credentials: 'same-origin' }))
    expect(fetch).toHaveBeenNthCalledWith(2, '/api/admin/deployments/deployment-1/capacity-profile', expect.objectContaining({ method: 'PUT' }))
    expect(fetch).toHaveBeenNthCalledWith(3, '/api/admin/deployments/deployment-1/capacity-profile/apply', expect.objectContaining({ method: 'POST' }))
  })

  it('serializes JSON commands with the correct content type', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({ id: 'node-1' }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' }
    }))

    await api.createNode({
      name: 'dgx-02',
      baseAddress: 'http://10.0.0.22:8000',
      weight: 1,
      maxConcurrency: 4
    })

    expect(fetch).toHaveBeenCalledWith('/api/admin/nodes', expect.objectContaining({
      method: 'POST',
      headers: expect.objectContaining({ 'Content-Type': 'application/json' }),
      body: JSON.stringify({
        name: 'dgx-02',
        baseAddress: 'http://10.0.0.22:8000',
        weight: 1,
        maxConcurrency: 4
      })
    }))
  })

  it.each([401, 403])('maps HTTP %s to AUTH_REQUIRED', async status => {
    vi.mocked(fetch).mockResolvedValue(new Response('', { status }))

    await expect(api.overview()).rejects.toThrow('AUTH_REQUIRED')
  })

  it('propagates the server error body for non-success responses', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response('gateway exploded', { status: 500 }))

    await expect(api.nodes()).rejects.toThrow('gateway exploded')
  })

  it('accepts 204 commands without trying to parse JSON', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(null, { status: 204 }))

    await expect(api.revokeApiCredential('credential-1')).resolves.toBeUndefined()
  })
})
