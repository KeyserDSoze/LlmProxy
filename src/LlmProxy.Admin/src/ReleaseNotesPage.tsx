import { useEffect, useState } from 'react'

type ProductRelease = {
  version: string
  releasedOn: string
  title: string
  sections: Record<string, string[]>
}

type ProductReleaseInfo = {
  product: string
  version: string
  channel: string
  releasedOn: string
  buildRevision?: string | null
  builtAtUtc?: string | null
  releases: ProductRelease[]
}

export default function ReleaseNotesPage({ embedded = false }: { embedded?: boolean }) {
  const [product, setProduct] = useState<ProductReleaseInfo | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    fetch('/api/admin/product', { credentials: 'same-origin' })
      .then(async response => {
        if (response.status === 401) throw new Error('AUTH_REQUIRED')
        if (response.status === 403) throw new Error('FORBIDDEN')
        if (!response.ok) throw new Error(await response.text() || `${response.status} ${response.statusText}`)
        return response.json() as Promise<ProductReleaseInfo>
      })
      .then(setProduct)
      .catch(reason => setError(reason instanceof Error ? reason.message : String(reason)))
  }, [])

  if (error === 'AUTH_REQUIRED') {
    return <div className={embedded ? undefined : 'releasePage'}><div className="notice">Authentication is required. <a href="/auth/login">Sign in with Entra ID</a>.</div></div>
  }

  if (error === 'FORBIDDEN') {
    return <div className={embedded ? undefined : 'releasePage'}><div className="error">Access denied. Your Entra account does not have an administrative LlmProxy role.</div></div>
  }

  if (error) return <div className={embedded ? undefined : 'releasePage'}><div className="error">{error}</div></div>
  if (!product) return <div className={embedded ? undefined : 'releasePage'}><div className="loading">Loading release notes…</div></div>

  return <div className={embedded ? 'stack' : 'releasePage'}>
    {!embedded && <div className="releaseHeader">
      <div>
        <span className="releaseEyebrow">{product.product} · {product.channel}</span>
        <h1>Release notes</h1>
        <p>What changed in the product, grouped by version.</p>
      </div>
      <a className="secondary releaseBack" href="/admin/">Back to Admin</a>
    </div>}

    {embedded && <div className="muted">{product.product} · {product.channel} · build {shortRevision(product.buildRevision)}</div>}

    <section className="cards cardsFive">
      <div className="metric"><span>Current version</span><strong>{product.version}</strong></div>
      <div className="metric"><span>Channel</span><strong>{product.channel}</strong></div>
      <div className="metric"><span>Release date</span><strong>{formatDate(product.releasedOn)}</strong></div>
      <div className="metric"><span>Build</span><strong className="releaseBuild">{shortRevision(product.buildRevision)}</strong></div>
      <div className="metric"><span>Published releases</span><strong>{product.releases.length}</strong></div>
    </section>

    <div className="stack">
      {product.releases.map(release => <section className="panel releaseCard" key={release.version}>
        <div className="panelTitle">
          <div><h2>v{release.version} · {release.title}</h2><div className="muted">Released {formatDate(release.releasedOn)}</div></div>
          <span>{release.version === product.version ? 'Current' : 'Previous'}</span>
        </div>
        <div className="releaseSections">
          {Object.entries(release.sections).map(([section, items]) => <div className="releaseSection" key={section}>
            <h3>{section}</h3>
            <ul>{items.map(item => <li key={item}>{item}</li>)}</ul>
          </div>)}
        </div>
      </section>)}
    </div>
  </div>
}

function formatDate(value: string) {
  const date = new Date(`${value}T00:00:00Z`)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleDateString()
}

function shortRevision(value?: string | null) {
  if (!value) return 'local / unknown'
  return value.length > 12 ? value.slice(0, 12) : value
}
