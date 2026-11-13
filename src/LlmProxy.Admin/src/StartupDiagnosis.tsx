import { useState } from 'react'
import type { ReactNode } from 'react'
import { useT } from './uiPreferences'
import { copyText } from './UiPrimitives'

export type ParsedDiagnosis = {
  summary: string
  rootCause?: string
  whatToDo?: string
  hostLog?: string
  details: string[]
  tail: string[]
}

/** Splits Agent startup/install errors into the parts an operator acts on. */
export function parseDiagnosis(text: string): ParsedDiagnosis {
  const result: ParsedDiagnosis = { summary: '', details: [], tail: [] }
  let inTail = false
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim()
    if (!line) continue
    if (inTail) { result.tail.push(line); continue }
    let match: RegExpMatchArray | null
    if ((match = line.match(/^Likely root cause:\s*(.*)$/i))) result.rootCause = match[1]
    else if ((match = line.match(/^What to do:\s*(.*)$/i))) result.whatToDo = match[1]
    else if ((match = line.match(/^Full bounded startup log saved on the inference host:\s*(.*)$/i))) result.hostLog = match[1]
    else if (/^Last Docker log lines:?$/i.test(line)) inTail = true
    else if (!result.summary) result.summary = line
    else result.details.push(line)
  }
  return result
}

export default function StartupDiagnosis({ title, text, tone = 'error', guide, children }: {
  title: string; text: string; tone?: 'error' | 'warning'; guide?: ReactNode; children?: ReactNode
}) {
  const t = useT()
  const [copied, setCopied] = useState(false)
  const parsed = parseDiagnosis(text)
  async function copy() {
    if (await copyText(text)) {
      setCopied(true)
      window.setTimeout(() => setCopied(false), 2000)
    }
  }
  return <section className={'diagnosis diagnosis-' + tone} role="alert" aria-label={title}>
    <div className="diagnosisHeader">
      <span className="diagnosisIcon" aria-hidden="true">!</span>
      <strong>{title}</strong>
    </div>
    {parsed.summary && <p className="diagnosisSummary">{parsed.summary}</p>}
    {parsed.rootCause && <div className="diagnosisField">
      <span className="diagnosisLabel">{t('diag.rootCause')}</span>
      <code className="diagnosisCode">{parsed.rootCause}</code>
    </div>}
    {parsed.whatToDo && <div className="diagnosisAdvice">
      <span className="diagnosisLabel">{t('diag.whatToDo')}</span>
      <p>{parsed.whatToDo}</p>
    </div>}
    {parsed.details.map((line, index) => <p className="diagnosisDetail" key={index}>{line}</p>)}
    {parsed.hostLog && <div className="diagnosisField">
      <span className="diagnosisLabel">{t('diag.hostLog')}</span>
      <code className="diagnosisCode">{parsed.hostLog}</code>
    </div>}
    {parsed.tail.length > 0 && <details className="diagnosisTail">
      <summary>{t('diag.lastLines')} ({parsed.tail.length})</summary>
      <pre>{parsed.tail.join('\n')}</pre>
    </details>}
    {guide}
    <div className="diagnosisActions">
      <button type="button" className="secondary" onClick={() => void copy()}>
        {copied ? t('diag.copied') : t('diag.copy')}</button>
      {children}
    </div>
  </section>
}
