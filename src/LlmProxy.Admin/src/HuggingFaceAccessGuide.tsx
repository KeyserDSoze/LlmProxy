import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { api } from './api'
import { huggingFaceAccessIssue, huggingFaceRepositoryPage, huggingFaceSearchPage, huggingFaceTokenPage } from './huggingFaceAccess'
import { useT } from './uiPreferences'
import type { HuggingFaceAccessStatus } from './types'

/**
 * Guided fix for gated/private Hugging Face repositories: shows only when the failure text says
 * access is the problem. Saves the token on the server's Agent (write-only) and then retries the
 * operation that failed.
 */
export default function HuggingFaceAccessGuide({ nodeId, failure, canWrite, onConfigured }: {
  nodeId: string
  failure: string
  canWrite: boolean
  onConfigured: () => void | Promise<void>
}) {
  const t = useT()
  const issue = huggingFaceAccessIssue(failure)
  const [status, setStatus] = useState<HuggingFaceAccessStatus | null>(null)
  const [token, setToken] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [note, setNote] = useState<string | null>(null)

  useEffect(() => {
    if (!issue) return
    let current = true
    api.huggingFaceAccess(nodeId).then(value => { if (current) setStatus(value) }, () => undefined)
    return () => { current = false }
  }, [nodeId, issue?.kind])

  if (!issue) return null

  async function save(event: FormEvent) {
    event.preventDefault()
    if (!token.trim()) return
    setBusy(true); setError(null); setNote(null)
    try {
      const saved = await api.setHuggingFaceAccess(nodeId, token)
      setToken('')
      setStatus(saved)
      setNote(saved.verified && saved.account
        ? `${t('hfaccess.verifiedFor')} ${saved.account}` : t('hfaccess.unverified'))
      await onConfigured()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    } finally { setBusy(false) }
  }

  async function remove() {
    setBusy(true); setError(null); setNote(null)
    try { setStatus(await api.clearHuggingFaceAccess(nodeId)) }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(false) }
  }

  const missing = issue.kind === 'missing'
  const intro = missing ? t('hfaccess.missingIntro') : issue.kind === 'rejected' ? t('hfaccess.rejectedIntro')
    : issue.kind === 'forbidden' ? t('hfaccess.forbiddenIntro') : t('hfaccess.requiredIntro')

  return <form className="hfAccess" onSubmit={event => void save(event)} aria-label={t('hfaccess.title')}>
    <strong>{t('hfaccess.title')}</strong>
    <p>{intro}</p>
    <ol className="hfSteps">
      <li>
        <span>{missing ? t('hfaccess.missingStep1') : t('hfaccess.step1')}</span>
        {issue.repository && <a className="buttonLink secondary compact" target="_blank" rel="noopener noreferrer"
          href={missing ? huggingFaceSearchPage(issue.repository) : huggingFaceRepositoryPage(issue.repository)}>
          {missing ? t('hfaccess.searchRepo') : t('hfaccess.openRepo')} · {issue.repository}</a>}
      </li>
      <li>
        <span>{missing ? t('hfaccess.missingStep2') : t('hfaccess.step2')}</span>
        <a className="buttonLink secondary compact" target="_blank" rel="noopener noreferrer"
          href={huggingFaceTokenPage}>{t('hfaccess.openTokens')}</a>
      </li>
      <li>
        <label>{t('hfaccess.step3')}
          <input type="password" autoComplete="off" spellCheck={false} placeholder="hf_…"
            aria-label={t('hfaccess.tokenLabel')} value={token} disabled={!canWrite || busy}
            onChange={event => setToken(event.target.value)} />
        </label>
      </li>
    </ol>
    {status?.configured && <p className="muted">{status.source === 'environment'
      ? t('hfaccess.storedEnv') : `${t('hfaccess.storedFor')} ${status.account ?? '—'}.`}</p>}
    {note && <p className="notice" role="status">{note}</p>}
    {error && <p className="error" role="alert">{error}</p>}
    <div className="diagnosisActions">
      <button className="primary" disabled={!canWrite || busy || !token.trim()}>
        {busy ? t('hfaccess.saving') : t('hfaccess.saveRetry')}</button>
      {status?.source === 'admin' && <button type="button" className="secondary" disabled={!canWrite || busy}
        onClick={() => void remove()}>{t('hfaccess.remove')}</button>}
    </div>
    <p className="muted hfPrivacy">{t('hfaccess.privacy')}</p>
  </form>
}
