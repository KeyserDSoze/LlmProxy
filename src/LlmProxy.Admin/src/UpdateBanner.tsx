import { useCallback, useEffect, useState } from 'react'
import { api } from './api'
import type { AvailableProductRelease, ProductUpdateOverview } from './types'
import { useT } from './uiPreferences'

const DISMISSED_KEY = 'llmproxy.updateBanner.dismissedVersion'
const IDLE_POLL_MS = 60_000
const ACTIVE_POLL_MS = 5_000

function parts(version: string): [number, number, number] {
  const match = /^v?(\d+)\.(\d+)\.(\d+)/.exec(version)
  return match ? [Number(match[1]), Number(match[2]), Number(match[3])] : [0, 0, 0]
}

function compare(left: string, right: string) {
  const a = parts(left); const b = parts(right)
  return a[0] - b[0] || a[1] - b[1] || a[2] - b[2]
}

/** Newest published release that is newer than the running version, regardless of API ordering. */
export function latestAvailableRelease(overview: ProductUpdateOverview | null): AvailableProductRelease | null {
  const newer = overview?.releases.filter(release => release.isNewer) ?? []
  return newer.reduce<AvailableProductRelease | null>(
    (best, release) => best === null || compare(release.version, best.version) > 0 ? release : best, null)
}

export function availableUpdateCount(overview: ProductUpdateOverview | null) {
  return overview?.releases.filter(release => release.isNewer).length ?? 0
}

export function useProductUpdates() {
  const [overview, setOverview] = useState<ProductUpdateOverview | null>(null)
  const refresh = useCallback(async () => {
    try { setOverview(await api.productUpdates()) }
    catch { /* keep the last known state; the gateway may be restarting during an update */ }
  }, [])
  const updating = !!overview?.agent?.activeJob
  useEffect(() => {
    void refresh()
    const timer = window.setInterval(() => void refresh(), updating ? ACTIVE_POLL_MS : IDLE_POLL_MS)
    return () => window.clearInterval(timer)
  }, [refresh, updating])
  return { overview, refresh }
}

type Props = {
  overview: ProductUpdateOverview | null
  loadedVersion: string | null
  canWrite: boolean
  onOpenReleases: () => void
  onRefresh: () => Promise<void>
}

export default function UpdateBanner({ overview, loadedVersion, canWrite, onOpenReleases, onRefresh }: Props) {
  const t = useT()
  const [dismissed, setDismissed] = useState(() => window.localStorage.getItem(DISMISSED_KEY))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  if (!overview) return null

  const running = overview.agent?.activeJob ?? null
  const latest = latestAvailableRelease(overview)
  const runningVersion = overview.currentVersion.replace(/^v/, '')
  const pageVersion = loadedVersion?.replace(/^v/, '') ?? null

  if (pageVersion && pageVersion !== runningVersion && !running) {
    return <div className="updateBanner updateBanner--done" role="status" data-testid="update-banner">
      <div className="updateBannerText"><strong>{t('update.updated')} v{runningVersion}</strong>
        <span>{t('update.reloadHint')}</span></div>
      <div className="updateBannerActions">
        <button className="primary" onClick={() => window.location.reload()}>{t('update.reload')}</button></div>
    </div>
  }

  if (running) {
    const pending = running.status === 'Pending'
    return <div className="updateBanner" role="status" data-testid="update-banner">
      <div className="updateBannerText">
        <strong>{pending ? t('update.scheduled') : t('update.running')}: v{running.version}</strong>
        <span>{running.currentStep ? `v${running.currentStep} · ` : ''}{t('update.runningHint')}</span>
      </div>
      <div className="updateBannerActions">
        <button className="secondary" onClick={onOpenReleases}>{t('update.details')}</button></div>
    </div>
  }

  if (!latest || dismissed === latest.version) return null

  async function startUpdate() {
    if (!latest || !window.confirm(`v${latest.version}\n\n${t('update.confirm')}`)) return
    setBusy(true); setError(null)
    try { await api.scheduleProductUpdate({ version: latest.version, force: true }); await onRefresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(false) }
  }

  function dismiss() {
    if (!latest) return
    window.localStorage.setItem(DISMISSED_KEY, latest.version)
    setDismissed(latest.version)
  }

  const count = availableUpdateCount(overview)
  return <div className="updateBanner" role="status" data-testid="update-banner">
    <div className="updateBannerText">
      <strong>{t('update.available')}: v{latest.version}</strong>
      <span>{t('update.currentLabel')} v{runningVersion}{count > 1 ? ` · ${count} ${t('update.releasesCount')}` : ''}</span>
      {!overview.agentAvailable && <span className="updateBannerWarn">{t('update.agentDown')}</span>}
      {overview.agentAvailable && !canWrite && <span>{t('update.readOnly')}</span>}
      {error && <span className="updateBannerWarn">{error}</span>}
    </div>
    <div className="updateBannerActions">
      {canWrite && <button className="primary" disabled={busy || !overview.agentAvailable}
        onClick={() => void startUpdate()}>{busy ? t('update.starting') : t('update.now')}</button>}
      <button className="secondary" onClick={onOpenReleases}>{t('update.details')}</button>
      <button className="secondary" aria-label={t('update.dismiss')} onClick={dismiss}>×</button>
    </div>
  </div>
}
