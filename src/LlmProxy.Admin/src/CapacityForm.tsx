import { FormEvent, useState } from 'react'
import { useT } from './uiPreferences'
import {
  GPU_FRACTION_STEPS, OUTPUT_STEPS, SEQUENCE_STEPS, contextChoices, defaultSyncPublished, limitsChanged,
  perRequestTokens, shortTokens, sharesContextPool, stepsWith
} from './capacityOptions'
import type { Limits } from './capacityOptions'
import type { CapacityRequest, Deployment, ManagedInstallation } from './types'

export type CapacityTarget = { deploymentId: string; label: string; body: CapacityRequest }
export type CapacityPlan = { targets: CapacityTarget[]; savedOnly: boolean }
export type CapacitySibling = { deployment: Deployment; nodeName: string }

/**
 * Installed → Capacity: raise or lower what a model can hold with dropdowns, and apply it on the spot. A running model restarts
 * (the gateway takes it out of routing and puts the earlier limits back if the new ones do not start); a stopped one can be
 * started with them or just have them saved. The same change can be applied to the same model on other servers.
 */
export default function CapacityForm({ item, nodeName, ceiling, curated, siblings, onApply, onCancel }: {
  item: ManagedInstallation; nodeName: string; ceiling: number; curated: boolean; siblings: CapacitySibling[]
  onApply: (plan: CapacityPlan) => Promise<string | null>; onCancel: () => void
}) {
  const t = useT()
  const runtime = item.runtime ?? 'vllm'
  const state = item.agentState
  const running = item.agentStatus.toLowerCase() === 'running'
  const [initial] = useState<Limits>(() => ({
    context: state?.maxModelLen ?? 8192,
    sequences: state?.maxNumSeqs ?? 1,
    output: runtime === 'airllm' ? state?.maxOutputTokens ?? 256 : null,
    // Suggested conservative start on unified-memory servers when the operator never set one.
    gpu: runtime === 'vllm' ? state?.gpuMemoryUtilization ?? 0.7 : null
  }))
  const [draft, setDraft] = useState<Limits>(initial)
  const [custom, setCustom] = useState(false)
  const [picked, setPicked] = useState<Set<string>>(new Set())
  const [syncChoice, setSyncChoice] = useState<boolean | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const published = item.properties?.contextWindow ?? null
  const offerSync = Boolean(item.properties) && (item.surface === undefined || item.surface === 'OpenAi' || item.surface === 'Embedding')
  const sync = syncChoice ?? defaultSyncPublished(published, runtime, initial, siblings.length, picked.size)
  const dirty = limitsChanged(initial, draft)
  const contextValid = Number.isInteger(draft.context) && draft.context >= 256 && draft.context <= ceiling
  const outputValid = runtime !== 'airllm' || draft.output == null || draft.output <= draft.context
  const perRequest = perRequestTokens(runtime, draft.context, draft.sequences)
  const canSubmit = !busy && contextValid && outputValid && (!running || dirty || picked.size > 0)

  const body = (extra: Partial<CapacityRequest>): CapacityRequest => ({
    maxModelLen: draft.context, maxNumSeqs: draft.sequences,
    maxOutputTokens: runtime === 'airllm' ? draft.output : null,
    gpuMemoryUtilization: runtime === 'vllm' ? draft.gpu : null, ...extra
  })

  async function submit(applyNow: boolean) {
    const targets: CapacityTarget[] = []
    if (dirty || !running) targets.push({ deploymentId: item.id, label: nodeName, body: body({ applyNow }) })
    for (const sibling of siblings.filter(entry => picked.has(entry.deployment.id)))
      targets.push({
        deploymentId: sibling.deployment.id, label: sibling.nodeName,
        // Another server may run another engine: send only what every engine understands and never start a stopped one.
        body: { maxModelLen: draft.context, maxNumSeqs: draft.sequences, maxOutputTokens: null, gpuMemoryUtilization: null, applyNow: true, keepStopped: true }
      })
    if (sync && offerSync && targets.length > 0) targets[0] = { ...targets[0], body: { ...targets[0].body, syncPublishedContext: true } }
    setBusy(true); setError(null)
    try {
      const problem = await onApply({ targets, savedOnly: !applyNow && picked.size === 0 })
      if (problem) setError(problem)
    } finally { setBusy(false) }
  }

  const contextOptions = contextChoices(initial.context, ceiling)
  const tokenText = (value: number) => `${shortTokens(value)} · ${value.toLocaleString()} ${t('capacity.tokens')}`
  const change = (patch: Partial<Limits>) => setDraft(current => ({ ...current, ...patch }))

  return <form className="formPanel capacityForm" onSubmit={(event: FormEvent) => { event.preventDefault(); if (canSubmit) void submit(true) }}>
    {error && <div className="error" role="alert">{error}</div>}
    <p className="notice capacityCurrent" role="status">
      <strong>{t('capacity.current')}</strong>
      <span>{item.agentStatus}</span>
      <span>{initial.context.toLocaleString()} {t('capacity.tokens')}</span>
      <span>{initial.sequences} {t('capacity.parallelShort')}</span>
    </p>

    <label>{t('capacity.context')}
      <select aria-label={t('model.editContext')} value={custom ? 'custom' : String(draft.context)}
        onChange={event => {
          if (event.target.value === 'custom') { setCustom(true); return }
          setCustom(false); change({ context: Number(event.target.value) })
        }}>
        {contextOptions.map(value => <option key={value} value={value}>{tokenText(value)}</option>)}
        <option value="custom">{t('capacity.contextCustom')}</option>
      </select>
    </label>
    {custom && <label>{t('capacity.customLabel')}
      <input aria-label={t('capacity.customLabel')} type="number" min={256} max={ceiling} value={draft.context}
        onChange={event => change({ context: Number(event.target.value) })} required />
    </label>}
    {!contextValid && <p className="error" role="alert">256 – {ceiling.toLocaleString()} {t('capacity.tokens')}</p>}
    <p className="muted capacityHint">{(sharesContextPool(runtime) ? t('capacity.perRequestPool') : t('capacity.perRequestWhole')).replace('{n}', perRequest.toLocaleString())}</p>

    <label>{t('capacity.sequences')}
      <select aria-label={t('model.editSequences')} value={draft.sequences} onChange={event => change({ sequences: Number(event.target.value) })}>
        {stepsWith(SEQUENCE_STEPS, initial.sequences).map(value => <option key={value} value={value}>{value}</option>)}
      </select>
    </label>

    {runtime === 'airllm' && <label>{t('capacity.output')}
      <select aria-label={t('model.editOutput')} value={draft.output ?? 256} onChange={event => change({ output: Number(event.target.value) })}>
        {stepsWith(OUTPUT_STEPS, initial.output, draft.context).map(value => <option key={value} value={value}>{value.toLocaleString()}</option>)}
      </select>
      {!outputValid && <span className="error" role="alert">≤ {draft.context.toLocaleString()}</span>}
    </label>}

    {runtime === 'vllm' && <label>{t('capacity.gpuFraction')}
      <select aria-label="vLLM GPU memory fraction" value={draft.gpu ?? 0.7} onChange={event => change({ gpu: Number(event.target.value) })}>
        {stepsWith(GPU_FRACTION_STEPS, initial.gpu).map(value => <option key={value} value={value}>{value.toFixed(2)}</option>)}
      </select>
    </label>}

    <p className="muted">{t('capacity.inputOutput')}</p>
    <p className="muted">{curated ? t('model.contextNativeDisclaimer') : t('model.contextUnknownDisclaimer')} {t('capacity.memoryNote')}</p>
    {runtime === 'airllm' && <p className="muted">{t('model.editNotice')}</p>}

    {offerSync && <div className="stack">
      {published != null && <p className="muted capacityHint">{t('capacity.publishedNow').replace('{n}', published.toLocaleString())}</p>}
      <label className="inlineCheck"><input type="checkbox" checked={sync} onChange={event => setSyncChoice(event.target.checked)} />
        {t('capacity.syncPublished').replace('{n}', perRequest.toLocaleString())}</label>
    </div>}

    {siblings.length > 0 && <fieldset className="capacitySiblings">
      <legend>{t('capacity.otherServers')}</legend>
      {siblings.map(({ deployment, nodeName: name }) => <label className="inlineCheck" key={deployment.id}>
        <input type="checkbox" checked={picked.has(deployment.id)} onChange={event => setPicked(current => {
          const next = new Set(current)
          if (event.target.checked) next.add(deployment.id); else next.delete(deployment.id)
          return next
        })} /> {name}</label>)}
      <p className="muted">{t('capacity.otherServersHint')}</p>
    </fieldset>}

    {running && <div className="notice" role="note">{t('capacity.restartNotice')}</div>}
    <div className="modalActions">
      <button type="button" className="secondary" onClick={onCancel}>{t('publishing.cancel')}</button>
      {!running && <button type="button" className="secondary" disabled={!canSubmit} onClick={() => void submit(false)}>{t('catalog.saveLimits')}</button>}
      <button className="primary" disabled={!canSubmit}>{running ? t('capacity.applyRestart') : t('capacity.applyStart')}</button>
    </div>
  </form>
}
