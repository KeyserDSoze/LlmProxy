import { useEffect, useState } from 'react'
import { api } from './api'
import { useT } from './uiPreferences'
import type { HuggingFaceInspectionResponse, HuggingFaceQuantizedBuild } from './types'

export type InspectorChoice = {
  repository: string
  runtime: 'vllm' | 'llama.cpp'
  maxModelLen?: number
}

// Formats the managed vLLM image can load directly; MLX/EXL2 have no engine here.
const vllmFormats = new Set(['awq', 'gptq', 'fp8', 'compressed-tensors', 'bitsandbytes', '4-bit', '8-bit'])

const repositoryPattern = /^[A-Za-z0-9][A-Za-z0-9_.-]*\/[A-Za-z0-9][A-Za-z0-9_.-]*$/

export function formatParameterCount(value: number): string {
  if (value >= 1e9) return (value / 1e9).toFixed(value >= 1e11 ? 0 : 1).replace(/\.0$/, '') + 'B'
  if (value >= 1e6) return (value / 1e6).toFixed(0) + 'M'
  return String(value)
}

function formatSize(bytes: number): string {
  return bytes >= 1024 ** 3 ? (bytes / 1024 ** 3).toFixed(1) + ' GiB' : (bytes / 1024 ** 2).toFixed(0) + ' MiB'
}

/**
 * Reads the Hugging Face metadata of the typed repository (parameters, formats, context window,
 * GGUF quantizations and other publishers' quantized builds) so the operator picks instead of guessing.
 */
export default function HuggingFaceRepositoryInspector({ repository, onChoose }: {
  repository: string
  onChoose: (choice: InspectorChoice) => void
}) {
  const t = useT()
  const [data, setData] = useState<HuggingFaceInspectionResponse | null>(null)
  const [state, setState] = useState<'idle' | 'loading' | 'error'>('idle')
  const valid = repositoryPattern.test(repository)

  useEffect(() => {
    setData(null)
    if (!valid) { setState('idle'); return }
    let live = true
    setState('loading')
    const timer = window.setTimeout(() => {
      api.inspectHuggingFaceRepository(repository)
        .then(result => { if (live) { setData(result); setState('idle') } })
        .catch(() => { if (live) setState('error') })
    }, 500)
    return () => { live = false; window.clearTimeout(timer) }
  }, [repository, valid])

  if (!valid) return null
  if (state === 'loading') return <p className="muted" role="status">{t('hfinspect.loading')}</p>
  if (state === 'error') return <p className="muted">{t('hfinspect.unavailable')}</p>
  if (!data) return null
  const { inspection, quantizedBuilds } = data
  if (!inspection.found) return <p className="notice" role="status">{t('hfinspect.notFound')}</p>

  const suggestion = inspection.suggestion
  const facts: Array<[string, string]> = [
    [t('hfinspect.parameters'), inspection.parameterCount ? formatParameterCount(inspection.parameterCount) : ''],
    [t('hfinspect.precision'), inspection.precision ?? ''],
    [t('hfinspect.weights'), inspection.weightsBytes ? formatSize(inspection.weightsBytes) : ''],
    [t('hfinspect.context'), inspection.contextLength ? inspection.contextLength.toLocaleString() + ' tokens' : ''],
    [t('hfinspect.architecture'), inspection.architecture ?? ''],
    [t('hfinspect.license'), inspection.license ?? ''],
    [t('hfinspect.quantized'), inspection.quantization ? inspection.quantization.toUpperCase() : '']
  ]
  const shownBuilds = quantizedBuilds.slice(0, 12)

  return <section className="hfInspect" aria-label={t('hfinspect.title')}>
    <header className="hfInspectHeader">
      <strong>{t('hfinspect.title')}</strong>
      <a href={inspection.url} target="_blank" rel="noopener noreferrer">huggingface.co/{inspection.id}</a>
    </header>
    <dl className="hfInspectFacts">
      {facts.filter(([, value]) => value).map(([label, value]) =>
        <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}
      {inspection.multimodal && <div><dt>{t('catalog.capabilities')}</dt><dd>{t('hfinspect.multimodal')}</dd></div>}
    </dl>
    {inspection.gated && <p className="notice">{t('hfinspect.gated')}</p>}

    {suggestion && <div className="hfSuggestion">
      <p><strong>{t('hfinspect.suggested')}: {suggestion.runtime === 'vllm' ? 'vLLM' : 'llama.cpp'}</strong> — {suggestion.reason}</p>
      <button type="button" className="secondary compact"
        onClick={() => onChoose({ repository, runtime: suggestion.runtime, maxModelLen: suggestion.contextTokens })}>
        {t('hfinspect.useSuggestion')}</button>
    </div>}

    {inspection.ggufVariants.length > 0 && <div className="tableScroll">
      <table aria-label={t('hfinspect.ggufTitle')}>
        <caption>{t('hfinspect.ggufTitle')}</caption>
        <thead><tr><th>{t('hfinspect.quantization')}</th><th>{t('hfinspect.size')}</th><th>{t('hfinspect.parts')}</th><th /></tr></thead>
        <tbody>{inspection.ggufVariants.map(variant => <tr key={variant.quantization}>
          <td className="mono">{variant.quantization}</td>
          <td>{variant.sizeBytes ? formatSize(variant.sizeBytes) : '—'}</td>
          <td>{variant.parts ?? 1}</td>
          <td><button type="button" className="secondary compact"
            aria-label={`${t('hfinspect.use')} ${variant.quantization}`}
            onClick={() => onChoose({ repository: `${inspection.id}:${variant.quantization}`, runtime: 'llama.cpp',
              maxModelLen: suggestion?.contextTokens })}>{t('hfinspect.use')}</button></td>
        </tr>)}</tbody>
      </table>
    </div>}

    {!inspection.hasGguf && quantizedBuilds.some(build => build.format === 'gguf') &&
      <p className="muted">{t('hfinspect.noGgufHint')}</p>}

    {shownBuilds.length > 0 && <div className="tableScroll">
      <table aria-label={t('hfinspect.buildsTitle')}>
        <caption>{t('hfinspect.buildsTitle')}<span className="muted"> — {t('hfinspect.buildsHint')}</span></caption>
        <thead><tr><th>{t('hfinspect.build')}</th><th>{t('hfinspect.format')}</th><th>{t('hfinspect.parameters')}</th><th>{t('hfinspect.downloads')}</th><th /></tr></thead>
        <tbody>{shownBuilds.map(build => <BuildRow key={build.id} build={build}
          contextTokens={suggestion?.contextTokens} onChoose={onChoose} />)}</tbody>
      </table>
    </div>}
  </section>
}

function BuildRow({ build, contextTokens, onChoose }: {
  build: HuggingFaceQuantizedBuild
  contextTokens?: number
  onChoose: (choice: InspectorChoice) => void
}) {
  const t = useT()
  const runtime = build.format === 'gguf' ? 'llama.cpp' : vllmFormats.has(build.format) ? 'vllm' : null
  return <tr>
    <td className="mono">{build.id}</td>
    <td><span className="pill pill-neutral">{build.format.toUpperCase()}</span></td>
    <td>{build.parameterCount ? formatParameterCount(build.parameterCount) : '—'}</td>
    <td>{build.downloads.toLocaleString()}</td>
    <td><button type="button" className="secondary compact" disabled={!runtime}
      title={runtime ? undefined : t('hfinspect.unsupported')}
      aria-label={`${t('hfinspect.use')} ${build.id}`}
      onClick={() => runtime && onChoose({ repository: build.id, runtime, maxModelLen: contextTokens })}>
      {t('hfinspect.use')}</button></td>
  </tr>
}
