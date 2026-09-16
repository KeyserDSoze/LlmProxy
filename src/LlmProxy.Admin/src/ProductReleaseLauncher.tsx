import { useEffect, useState } from 'react'

type ProductSummary = {
  version: string
  channel: string
}

export default function ProductReleaseLauncher() {
  const [product, setProduct] = useState<ProductSummary | null>(null)

  useEffect(() => {
    let cancelled = false
    fetch('/api/admin/product', { credentials: 'same-origin' })
      .then(response => response.ok ? response.json() as Promise<ProductSummary> : null)
      .then(result => { if (!cancelled && result) setProduct(result) })
      .catch(() => undefined)
    return () => { cancelled = true }
  }, [])

  return <a className="releaseLauncher" href="/admin/releases">
    {product ? `v${product.version} · Release notes` : 'Release notes'}
  </a>
}
