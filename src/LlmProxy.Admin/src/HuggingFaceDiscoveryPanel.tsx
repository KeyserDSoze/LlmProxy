import { useCallback, useEffect, useState } from 'react'
import { api } from './api'
import type { HuggingFaceModel } from './types'

export default function HuggingFaceDiscoveryPanel({
  canWrite, onReview
}: {
  canWrite: boolean
  onReview: (repoId: string) => void
}) {
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
      <h2>Hugging Face model discovery</h2>
      <span>Live public Hub catalog (cached 10 minutes). Discovery is not a runtime compatibility or licensing guarantee.</span>
    </div></div>
    <form className="formPanel" onSubmit={event => { event.preventDefault(); void fetchModels(query, sort) }}>
      <div className="actions">
        <label>Search models
          <input aria-label="Search Hugging Face models" value={query}
            maxLength={100} onChange={event => setQuery(event.target.value)}
            placeholder="Qwen, Llama, Gemma, Mistral…" />
        </label>
        <label>Sort by
          <select aria-label="Hugging Face sort" value={sort}
            onChange={event => {
              const order = event.target.value as typeof sort
              setSort(order)
              void fetchModels(query, order)
            }}>
            <option value="trending">Trending</option>
            <option value="recent">Recently updated</option>
            <option value="downloads">Downloads</option>
          </select>
        </label>
        <button type="submit" disabled={busy}>{busy ? 'Searching…' : 'Search Hub'}</button>
      </div>
    </form>
    <p className="muted">Curated models above have estimated hardware requirements and reviewed runtimes. Hub search results are candidates only: architecture, dependencies, licensing, hardware fit, context length and functionality must be verified before installing.</p>
    {error && <div className="error" role="alert">{error}</div>}
    <div className="tableScroll"><table><thead><tr>
      <th>Hugging Face model</th><th>Popularity</th><th>Latest commit</th><th>Access</th><th>Action</th>
    </tr></thead><tbody>
      {models.map(model => <tr key={model.id}>
        <td><strong>{model.id}</strong><div className="muted">{model.pipelineTag ?? 'unknown task'} · {model.license ?? 'license unknown'}</div>
          <a href={model.url} target="_blank" rel="noreferrer">Official model card</a>
        </td>
        <td>{model.downloads.toLocaleString()} downloads<div className="muted">{model.likes.toLocaleString()} likes</div></td>
        <td className="mono">{model.revision?.slice(0,12) ?? 'unknown'}<div className="muted">{model.lastModified ? new Date(model.lastModified).toLocaleDateString() : 'unknown date'}</div></td>
        <td>{model.gated ? 'Gated · approval required' : 'Public'}<div className="muted">Unverified deployment</div></td>
        <td><button disabled={!canWrite || model.gated}
          title={model.gated ? 'This checkpoint requires Hub approval/access. Do not attempt unauthenticated Agent download.' : 'Review model license, hardware and runtime before installing'}
          onClick={() => onReview(model.id)}>Review custom install</button></td>
      </tr>)}
      {models.length === 0 && <tr><td colSpan={5} className="muted">{busy ? 'Loading Hub models…' : 'No matching public models returned.'}</td></tr>}
    </tbody></table></div>
  </section>
}
