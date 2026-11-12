import { describe, expect, it } from 'vitest'
import { attentionItems } from '../../../src/LlmProxy.Admin/src/App'
import { normalizeLegacyAdminUrl } from '../../../src/LlmProxy.Admin/src/adminNavigation'
import { enhanceScrollableRegions } from '../../../src/LlmProxy.Admin/src/scrollableRegions'
import { parseDiagnosis } from '../../../src/LlmProxy.Admin/src/StartupDiagnosis'
import type { Deployment, Model, Node } from '../../../src/LlmProxy.Admin/src/types'

describe('startup diagnosis parsing', () => {
  it('splits the Agent llama.cpp failure into summary, cause, advice, host log and tail', () => {
    const parsed = parseDiagnosis([
      'Model container exited during startup with exit code 1. Automatic GPU restart is disabled; review the diagnosis below before retrying.',
      'Likely root cause: E llama_model_load_from_file_impl: exactly one out metadata, path_model, and file must be defined',
      'What to do: Start again: the Agent downloads and pins the exact GGUF first.',
      'Full bounded startup log saved on the inference host: /var/lib/llmproxy-node-agent/startup-logs/custom-01ea8a84.log',
      'Last Docker log lines:',
      "E srv load_model: failed to load model, ''",
      '',
      'E srv server_run_i: exiting due to model loading error'
    ].join('\r\n'))
    expect(parsed.summary).toMatch(/^Model container exited during startup with exit code 1/)
    expect(parsed.rootCause).toContain('exactly one out metadata, path_model, and file must be defined')
    expect(parsed.whatToDo).toBe('Start again: the Agent downloads and pins the exact GGUF first.')
    expect(parsed.hostLog).toBe('/var/lib/llmproxy-node-agent/startup-logs/custom-01ea8a84.log')
    expect(parsed.tail).toEqual(["E srv load_model: failed to load model, ''", 'E srv server_run_i: exiting due to model loading error'])
    expect(parsed.details).toEqual([])
  })

  it('keeps unstructured install errors readable', () => {
    const parsed = parseDiagnosis("No GGUF file in 'org/repo' matches quantization 'Q4_K_M'.\nAvailable quantizations: Q5_K_M, Q8_0.")
    expect(parsed.summary).toBe("No GGUF file in 'org/repo' matches quantization 'Q4_K_M'.")
    expect(parsed.details).toEqual(['Available quantizations: Q5_K_M, Q8_0.'])
    expect(parsed.rootCause).toBeUndefined()
    expect(parsed.tail).toEqual([])
  })
})

describe('legacy Admin URLs', () => {
  it('maps the former Servers → Deploy models tab to the Models workspace', () => {
    expect(normalizeLegacyAdminUrl('/admin/infrastructure', '?section=inventory&tab=deploy&node=n1'))
      .toBe('/admin/catalog?node=n1')
    expect(normalizeLegacyAdminUrl('/admin/infrastructure/', '?section=inventory&tab=deploy&node=n1&panel=installed&junk=1'))
      .toBe('/admin/models?node=n1&panel=installed')
    expect(normalizeLegacyAdminUrl('/admin/infrastructure', '?section=inventory&tab=deploy'))
      .toBe('/admin/catalog')
  })

  it('maps the former inventory section to Hardware and leaves current URLs alone', () => {
    expect(normalizeLegacyAdminUrl('/admin/infrastructure', '?section=inventory&node=n2&tab=inventory'))
      .toBe('/admin/infrastructure?section=hardware&node=n2')
    expect(normalizeLegacyAdminUrl('/admin/infrastructure', '?section=fleet')).toBeNull()
    expect(normalizeLegacyAdminUrl('/admin/models', '?section=inventory&tab=deploy')).toBeNull()
  })
})

describe('dashboard attention items', () => {
  const node = (id: string, status: string, enabled = true): Node => ({
    id, name: id, baseAddress: 'http://' + id, weight: 1, maxConcurrency: 4, enabled, status,
    lastHealthError: status === 'Unhealthy' ? 'Connection refused' : null
  } as unknown as Node)
  const model = (id: string, enabled = true): Model => ({ id, publicName: id, providerModelName: id, enabled } as unknown as Model)
  const deployment = (id: string, nodeId: string, modelId: string, enabled: boolean): Deployment =>
    ({ id, nodeId, modelId, enabled, weight: 1 } as unknown as Deployment)

  it('asks to pair a server when the fleet is empty', () => {
    expect(attentionItems([], [], []).map(item => item.key)).toEqual(['no-servers'])
  })

  it('flags unhealthy/disabled servers, unrouted published models and disabled deployments', () => {
    const items = attentionItems(
      [node('ok', 'Healthy'), node('down', 'Unhealthy'), node('off', 'Healthy', false)],
      [model('routed'), model('orphan'), model('retired', false)],
      [deployment('d1', 'ok', 'routed', true), deployment('d2', 'down', 'orphan', true), deployment('d3', 'ok', 'orphan', false)])
    expect(items.map(item => [item.key, item.tone])).toEqual([
      ['node-unhealthy-down', 'danger'],
      ['node-disabled-off', 'warning'],
      ['model-orphan', 'warning'],
      ['disabled-deployments', 'info']
    ])
    expect(items.find(item => item.key === 'node-unhealthy-down')?.detail).toBe('Connection refused')
    expect(items.find(item => item.key === 'model-orphan')?.href).toBe('/admin/models?panel=advanced')
  })

  it('is empty for a healthy, fully routed fleet', () => {
    expect(attentionItems([node('ok', 'Healthy')], [model('m')], [deployment('d', 'ok', 'm', true)])).toEqual([])
  })
})

describe('scrollable table regions', () => {
  it('makes only overflowing scroll containers keyboard-focusable and labels them', async () => {
    document.body.innerHTML = `<section class="panel"><div class="panelTitle"><h2>Requests</h2></div>
      <div class="tableScroll" id="wide" style="overflow-x:auto"></div>
      <div class="tableScroll" id="narrow" style="overflow-x:auto"></div></section>`
    const wide = document.getElementById('wide')!
    const narrow = document.getElementById('narrow')!
    Object.defineProperty(wide, 'scrollWidth', { configurable: true, value: 900 })
    Object.defineProperty(wide, 'clientWidth', { configurable: true, value: 300 })
    Object.defineProperty(narrow, 'scrollWidth', { configurable: true, value: 300 })
    Object.defineProperty(narrow, 'clientWidth', { configurable: true, value: 300 })
    enhanceScrollableRegions(document)
    await new Promise(resolve => requestAnimationFrame(() => resolve(null)))
    expect(wide.getAttribute('tabindex')).toBe('0')
    expect(wide.getAttribute('role')).toBe('region')
    expect(wide.getAttribute('aria-label')).toBe('Requests')
    expect(narrow.hasAttribute('tabindex')).toBe(false)

    Object.defineProperty(wide, 'scrollWidth', { configurable: true, value: 300 })
    window.dispatchEvent(new Event('resize'))
    await new Promise(resolve => requestAnimationFrame(() => resolve(null)))
    expect(wide.hasAttribute('tabindex')).toBe(false)
    expect(wide.hasAttribute('role')).toBe(false)
    expect(wide.hasAttribute('aria-label')).toBe(false)
  })
})
