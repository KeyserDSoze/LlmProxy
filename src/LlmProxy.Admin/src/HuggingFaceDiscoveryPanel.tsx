import { useCallback, useEffect, useState } from 'react'
import { api } from './api'
import { useT, useUiPreferences } from './uiPreferences'
import type { HuggingFaceModel } from './types'

export default function HuggingFaceDiscoveryPanel({
  canWrite, onReview
}: {
  canWrite: boolean
  onReview: (repoId: string, hasGguf: boolean) => void
}) {
  const t = useT()
  const { locale } = useUiPreferences()
  const [query, setQuery] = useState('')
  const [sort, setSort] = useState<'trending' | 'downloads' | 'recent'>('trending')
  const [models, setModels] = useState<HuggingFaceModel[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const fetchModels = useCallback(async (search: string, order: typeof sort) => {
    setBusy(true); setError(null)
    try {
      const result = await api.discoverHuggingFaceModels(search, order)
      setModels(result.models)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
      setModels([])
    } finally { setBusy(false) }
  }, [])
  useEffect(() => { void fetchModels('', 'trending') }, [fetchModels])
  return <section className="panel">
    <div className="panelTitle"><div>
      <h2>{t('hf.title')}</h2>
      <span>{t('hf.subtitle')}</span>
    </div></div>
    <form className="formPanel" onSubmit={event => { event.preventDefault(); void fetchModels(query, sort) }}>
      <div className="actions">
        <label>{t('hf.search')}
          <input aria-label={t('hf.search')} value={query}
            maxLength={100} onChange={event => setQuery(event.target.value)}
            placeholder="Qwen, Llama, Gemma, Mistral…" />
        </label>
        <label>{t('hf.sort')}
          <select aria-label={t('hf.sort')} value={sort}
            onChange={event => {
              const order = event.target.value as typeof sort
              setSort(order)
              void fetchModels(query, order)
            }}>
            <option value="trending">{t('hf.trending')}</option>
            <option value="recent">{t('hf.recent')}</option>
            <option value="downloads">{t('hf.downloads')}</option>
          </select>
        </label>
        <button type="submit" disabled={busy}>{busy ? t('hf.searching') : t('hf.searchHub')}</button>
      </div>
    </form>
    <p className="muted">{t('hf.caution')}</p>
    {error && <div className="error" role="alert">{error}</div>}
    <div className="tableScroll"><table><thead><tr>
      <th>{t('hf.model')}</th><th>{t('hf.popularity')}</th><th>{t('hf.latest')}</th><th>{t('hf.access')}</th><th>{t('hf.action')}</th>
    </tr></thead><tbody>
      {models.map(model => <tr key={model.id}>
        <td><strong>{model.id}</strong><div className="muted">{model.pipelineTag ?? t('hf.taskUnknown')} · {model.license ?? t('hf.licenseUnknown')}{model.hasGguf ? ' · GGUF (llama.cpp)' : ''}</div>
          <a href={model.url} target="_blank" rel="noreferrer">{t('hf.modelCard')}</a>
        </td>
        <td>{model.downloads.toLocaleString(locale)} {t('hf.downloads')}<div className="muted">{model.likes.toLocaleString(locale)} {t('hf.likes')}</div></td>
        <td className="mono">{model.revision?.slice(0,12) ?? t('hf.unknown')}<div className="muted">{model.lastModified ? new Date(model.lastModified).toLocaleDateString(locale) : t('hf.dateUnknown')}</div></td>
        <td>{model.gated ? t('hf.gated') : t('hf.public')}<div className="muted">{t('hf.unverified')}</div></td>
        <td><button disabled={!canWrite || model.gated}
          title={model.gated ? t('hf.gatedHelp') : t('hf.reviewHelp')}
          onClick={() => onReview(model.id, Boolean(model.hasGguf))}>{t('hf.review')}</button></td>
      </tr>)}
      {models.length === 0 && <tr><td colSpan={5} className="muted">{busy ? t('hf.loading') : t('hf.noResults')}</td></tr>}
    </tbody></table></div>
  </section>
}
